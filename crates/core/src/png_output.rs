use crate::{Document, Error};
use std::io::{self, Write};

pub(super) fn encode(document: &Document) -> Result<Vec<u8>, Error> {
    // Filters, zlib overhead and PNG chunks fit comfortably above raw RGBA.
    let limit = document.pixels.len() + document.pixels.len() / 8 + 65_536;
    encode_with_limit(document, limit)
}

fn encode_with_limit(document: &Document, limit: usize) -> Result<Vec<u8>, Error> {
    let mut output = FallibleBytes {
        bytes: Vec::new(),
        limit,
        failure: None,
    };
    let result = (|| -> Result<(), png::EncodingError> {
        let mut encoder = png::Encoder::new(&mut output, document.width, document.height);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        encoder.set_source_srgb(png::SrgbRenderingIntent::Perceptual);
        let mut writer = encoder.write_header()?;
        {
            // Stream to the bounded destination rather than building another
            // full compressed-image Vec inside the encoder first.
            let mut stream = writer.stream_writer()?;
            stream.write_all(&document.pixels)?;
            stream.finish()?;
        }
        writer.finish()?;
        Ok(())
    })();
    result.map_err(|_| output.failure.unwrap_or(Error::EncodingFailed))?;
    Ok(output.bytes)
}

struct FallibleBytes {
    bytes: Vec<u8>,
    limit: usize,
    failure: Option<Error>,
}

impl Write for FallibleBytes {
    fn write(&mut self, data: &[u8]) -> io::Result<usize> {
        let required = self.bytes.len().checked_add(data.len());
        if required.is_none_or(|required| required > self.limit) {
            self.failure = Some(Error::ResourceLimit);
            return Err(io::ErrorKind::FileTooLarge.into());
        }
        let required = required.unwrap();
        if required > self.bytes.capacity() {
            let capacity = required
                .max(self.bytes.capacity().saturating_mul(2))
                .min(self.limit);
            if self
                .bytes
                .try_reserve_exact(capacity - self.bytes.len())
                .is_err()
            {
                self.failure = Some(Error::AllocationFailed);
                return Err(io::ErrorKind::OutOfMemory.into());
            }
        }
        self.bytes.extend_from_slice(data);
        Ok(data.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encoded_output_limit_returns_an_error_instead_of_partial_png() {
        let document = Document::from_rgba(1, 1, 4, &[12, 34, 56, 78]).unwrap();
        assert_eq!(
            encode_with_limit(&document, 16).unwrap_err(),
            Error::ResourceLimit
        );
        assert!(encode(&document).is_ok());
    }

    #[test]
    fn bounded_writer_does_not_grow_or_write_past_limit() {
        let mut output = FallibleBytes {
            bytes: Vec::new(),
            limit: 4,
            failure: None,
        };
        output.write_all(&[1, 2, 3, 4]).unwrap();
        assert!(output.write_all(&[5]).is_err());
        assert_eq!(output.bytes, [1, 2, 3, 4]);
        assert!(output.bytes.capacity() <= 4);
    }
}
