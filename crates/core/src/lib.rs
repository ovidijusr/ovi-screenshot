//! Platform-independent immutable screenshot pixels and basic outputs.
//!
//! Inputs are SDR sRGB RGBA8 with straight alpha. Native adapters are
//! responsible for converting display-specific pixel/color representations.

use std::fmt;

mod png_output;
mod resample;
mod style;

pub use style::WINDOW_SHADOW_PADDING;

pub const MAX_DIMENSION: u32 = 32_768;
pub const MAX_PIXELS: u64 = 64 * 1024 * 1024;
pub const MAX_INPUT_BYTES: u64 = 512 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Error {
    InvalidArgument,
    InvalidDimensions,
    BufferTooSmall,
    ResourceLimit,
    EncodingFailed,
    AllocationFailed,
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::InvalidArgument => "Invalid argument or row stride",
            Self::InvalidDimensions => "Image dimensions must be positive",
            Self::BufferTooSmall => "Input does not contain all image rows",
            Self::ResourceLimit => "Image exceeds the configured resource limit",
            Self::EncodingFailed => "PNG encoding failed",
            Self::AllocationFailed => "Image allocation failed",
        })
    }
}

impl std::error::Error for Error {}

/// Validated byte counts, computed before FFI forms a borrowed slice.
#[derive(Clone, Copy, Debug)]
pub struct ValidatedLayout {
    pub row_bytes: usize,
    pub stride_bytes: usize,
    pub packed_length: usize,
    pub required_input_length: usize,
}

pub fn validate_input(
    width: u32,
    height: u32,
    stride_bytes: u64,
    data_length: u64,
) -> Result<ValidatedLayout, Error> {
    if width == 0 || height == 0 {
        return Err(Error::InvalidDimensions);
    }
    let pixel_count = u64::from(width) * u64::from(height);
    if width > MAX_DIMENSION
        || height > MAX_DIMENSION
        || pixel_count > MAX_PIXELS
        || data_length > MAX_INPUT_BYTES
    {
        return Err(Error::ResourceLimit);
    }
    let row_bytes = u64::from(width) * 4;
    if stride_bytes < row_bytes {
        return Err(Error::InvalidArgument);
    }
    let required = stride_bytes
        .checked_mul(u64::from(height - 1))
        .and_then(|offset| offset.checked_add(row_bytes))
        .ok_or(Error::ResourceLimit)?;
    if required > MAX_INPUT_BYTES || stride_bytes > MAX_INPUT_BYTES {
        return Err(Error::ResourceLimit);
    }
    if data_length < required {
        return Err(Error::BufferTooSmall);
    }
    // These checks also keep all slice offsets representable on 32-bit hosts.
    let to_size = |value| {
        usize::try_from(value)
            .ok()
            .filter(|value| *value <= isize::MAX as usize)
            .ok_or(Error::ResourceLimit)
    };
    Ok(ValidatedLayout {
        row_bytes: to_size(row_bytes)?,
        stride_bytes: to_size(stride_bytes)?,
        packed_length: to_size(pixel_count * 4)?,
        required_input_length: to_size(required)?,
    })
}

/// Owned, tightly packed immutable pixels. Padding and native buffers are never retained.
#[derive(Debug)]
pub struct Document {
    width: u32,
    height: u32,
    pixels: Vec<u8>,
}

#[derive(Debug)]
pub struct Bitmap {
    pub width: u32,
    pub height: u32,
    pub pixels: Vec<u8>,
}

impl Document {
    pub fn from_rgba(
        width: u32,
        height: u32,
        stride_bytes: u64,
        source: &[u8],
    ) -> Result<Self, Error> {
        let layout = validate_input(width, height, stride_bytes, source.len() as u64)?;
        let mut pixels = allocate(layout.packed_length)?;
        for row in 0..height as usize {
            let start = row * layout.stride_bytes;
            pixels.extend_from_slice(&source[start..start + layout.row_bytes]);
        }
        Ok(Self {
            width,
            height,
            pixels,
        })
    }

    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }

    pub fn pixels(&self) -> &[u8] {
        &self.pixels
    }

    pub fn add_window_shadow(&self) -> Result<Self, Error> {
        style::add_window_shadow(self)
    }

    /// Fits inside the bounds without upscaling. Integer math prevents an
    /// aspect-fit dimension from accidentally exceeding its requested bound.
    pub fn preview(&self, max_width: u32, max_height: u32) -> Result<Bitmap, Error> {
        if max_width == 0 || max_height == 0 {
            return Err(Error::InvalidDimensions);
        }
        let (width, height) = if self.width <= max_width && self.height <= max_height {
            (self.width, self.height)
        } else if u64::from(max_width) * u64::from(self.height)
            <= u64::from(max_height) * u64::from(self.width)
        {
            (
                max_width.min(self.width),
                ((u64::from(self.height) * u64::from(max_width)) / u64::from(self.width)).max(1)
                    as u32,
            )
        } else {
            (
                ((u64::from(self.width) * u64::from(max_height)) / u64::from(self.height)).max(1)
                    as u32,
                max_height.min(self.height),
            )
        };
        let pixels = resample::thumbnail(self, width, height)?;
        Ok(Bitmap {
            width,
            height,
            pixels,
        })
    }

    pub fn export_png(&self) -> Result<Vec<u8>, Error> {
        png_output::encode(self)
    }
}

fn allocate(length: usize) -> Result<Vec<u8>, Error> {
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(length)
        .map_err(|_| Error::AllocationFailed)?;
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn padded_rows_are_owned_and_padding_is_not_exported() {
        let expected = [
            255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 19, 29, 39, 255,
        ];
        let mut source = Vec::from(&expected[..8]);
        source.extend_from_slice(&[91, 92, 93, 94]);
        source.extend_from_slice(&expected[8..]);
        // There is deliberately no padding after the last row.
        let document = Document::from_rgba(2, 2, 12, &source).unwrap();
        source.fill(0);
        assert_eq!(document.pixels(), expected);
        let png = document.export_png().unwrap();
        let mut reader = png::Decoder::new(Cursor::new(png)).read_info().unwrap();
        assert_eq!(
            reader.info().srgb,
            Some(png::SrgbRenderingIntent::Perceptual)
        );
        let mut decoded = vec![0; reader.output_buffer_size().unwrap()];
        let info = reader.next_frame(&mut decoded).unwrap();
        assert_eq!((info.width, info.height), (2, 2));
        assert_eq!(info.color_type, png::ColorType::Rgba);
        assert_eq!(&decoded[..info.buffer_size()], expected);
    }

    #[test]
    fn malformed_layouts_fail_before_allocation() {
        assert_eq!(
            validate_input(0, 1, 4, 4).unwrap_err(),
            Error::InvalidDimensions
        );
        assert_eq!(
            validate_input(2, 1, 7, 8).unwrap_err(),
            Error::InvalidArgument
        );
        assert_eq!(
            validate_input(2, 2, 12, 19).unwrap_err(),
            Error::BufferTooSmall
        );
        assert_eq!(
            validate_input(1, 3, u64::MAX, 4).unwrap_err(),
            Error::ResourceLimit
        );
        assert_eq!(
            validate_input(u32::MAX, u32::MAX, 4, 4).unwrap_err(),
            Error::ResourceLimit
        );
        assert_eq!(
            validate_input(1, 1, 4, MAX_INPUT_BYTES + 1).unwrap_err(),
            Error::ResourceLimit
        );
        assert_eq!(
            validate_input(8193, 8192, 8193 * 4, 1).unwrap_err(),
            Error::ResourceLimit
        );
    }

    #[test]
    fn last_row_padding_is_optional_but_all_pixels_are_required() {
        assert_eq!(
            validate_input(2, 2, 12, 20).unwrap().required_input_length,
            20
        );
        assert_eq!(validate_input(2, 2, 12, 24).unwrap().packed_length, 16);
    }

    #[test]
    fn preview_fits_preserves_samples_and_never_upscales() {
        let source: Vec<u8> = (0..8).flat_map(|x| [x, x + 10, x + 20, x + 100]).collect();
        let document = Document::from_rgba(4, 2, 16, &source).unwrap();
        let preview = document.preview(2, 2).unwrap();
        assert_eq!((preview.width, preview.height), (2, 1));
        assert_eq!(preview.pixels, [3, 13, 23, 103, 5, 15, 25, 105]);
        let original = document.preview(u32::MAX, u32::MAX).unwrap();
        assert_eq!((original.width, original.height), (4, 2));
        assert_eq!(original.pixels, source);
        assert_eq!(
            document.preview(0, 2).unwrap_err(),
            Error::InvalidDimensions
        );
    }

    #[test]
    fn extreme_aspect_ratio_keeps_one_pixel_dimension() {
        let document = Document::from_rgba(1, 200, 4, &vec![0; 800]).unwrap();
        let preview = document.preview(1, 1).unwrap();
        assert_eq!(
            (preview.width, preview.height, preview.pixels.len()),
            (1, 1, 4)
        );
    }

    #[test]
    fn export_is_deterministic_and_has_no_editing_metadata() {
        let document = Document::from_rgba(1, 1, 4, &[5, 6, 7, 8]).unwrap();
        let first = document.export_png().unwrap();
        assert_eq!(first, document.export_png().unwrap());
        let reader = png::Decoder::new(Cursor::new(first)).read_info().unwrap();
        assert!(reader.info().uncompressed_latin1_text.is_empty());
        assert!(reader.info().utf8_text.is_empty());
        assert!(reader.info().compressed_latin1_text.is_empty());
    }
}
