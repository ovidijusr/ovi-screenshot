use crate::{allocate, validate_input, Document, Error};

pub const WINDOW_SHADOW_PADDING: u32 = 24;
const OFFSET_Y: usize = 6;
const BLUR_RADIUS: usize = 4;

pub(super) fn add_window_shadow(source: &Document) -> Result<Document, Error> {
    let width = source
        .width
        .checked_add(WINDOW_SHADOW_PADDING * 2)
        .ok_or(Error::ResourceLimit)?;
    let height = source
        .height
        .checked_add(WINDOW_SHADOW_PADDING * 2)
        .ok_or(Error::ResourceLimit)?;
    let layout = validate_input(
        width,
        height,
        u64::from(width) * 4,
        u64::from(width) * u64::from(height) * 4,
    )?;
    let pixels = layout.packed_length / 4;
    let mut mask = allocate(pixels)?;
    let mut scratch = allocate(pixels)?;
    mask.resize(pixels, 0);
    scratch.resize(pixels, 0);
    let padding = WINDOW_SHADOW_PADDING as usize;
    let width_usize = width as usize;
    for y in 0..source.height as usize {
        for x in 0..source.width as usize {
            mask[(y + padding + OFFSET_Y) * width_usize + x + padding] =
                source.pixels[(y * source.width as usize + x) * 4 + 3];
        }
    }
    for _ in 0..3 {
        blur(&mask, &mut scratch, width_usize, height as usize, true);
        blur(&scratch, &mut mask, width_usize, height as usize, false);
    }
    drop(scratch);
    let mut output = allocate(layout.packed_length)?;
    output.resize(layout.packed_length, 0);
    for (index, alpha) in mask.into_iter().enumerate() {
        output[index * 4 + 3] = ((u32::from(alpha) * 72 + 127) / 255) as u8;
    }
    for y in 0..source.height as usize {
        for x in 0..source.width as usize {
            let input = (y * source.width as usize + x) * 4;
            let destination = ((y + padding) * width_usize + x + padding) * 4;
            let alpha = u32::from(source.pixels[input + 3]);
            let shadow = u32::from(output[destination + 3]);
            let denominator = alpha * 255 + shadow * (255 - alpha);
            if denominator == 0 {
                continue;
            }
            for channel in 0..3 {
                output[destination + channel] =
                    ((u32::from(source.pixels[input + channel]) * alpha * 255 + denominator / 2)
                        / denominator) as u8;
            }
            output[destination + 3] = ((denominator + 127) / 255) as u8;
        }
    }
    Ok(Document {
        width,
        height,
        pixels: output,
    })
}

fn blur(input: &[u8], output: &mut [u8], width: usize, height: usize, horizontal: bool) {
    let (lines, length, step) = if horizontal {
        (height, width, 1)
    } else {
        (width, height, width)
    };
    const KERNEL: u32 = (BLUR_RADIUS * 2 + 1) as u32;
    for line in 0..lines {
        let origin = if horizontal { line * width } else { line };
        let mut sum = 0;
        for position in 0..=BLUR_RADIUS.min(length - 1) {
            sum += u32::from(input[origin + position * step]);
        }
        for position in 0..length {
            output[origin + position * step] = ((sum + KERNEL / 2) / KERNEL) as u8;
            if position >= BLUR_RADIUS {
                sum -= u32::from(input[origin + (position - BLUR_RADIUS) * step]);
            }
            let entering = position + BLUR_RADIUS + 1;
            if entering < length {
                sum += u32::from(input[origin + entering * step]);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shadow_preserves_opaque_content_and_has_transparent_soft_edges() {
        let source = Document::from_rgba(
            2,
            2,
            8,
            &[
                45, 120, 210, 255, 45, 120, 210, 128, 0, 0, 0, 0, 45, 120, 210, 255,
            ],
        )
        .unwrap();
        let styled = source.add_window_shadow().unwrap();
        assert_eq!((styled.width, styled.height), (50, 50));
        let center = (24 * 50 + 24) * 4;
        assert_eq!(&styled.pixels[center..center + 4], &[45, 120, 210, 255]);
        assert_eq!(&styled.pixels[..4], &[0; 4]);
        assert_eq!(&styled.pixels[styled.pixels.len() - 4..], &[0; 4]);
        assert_eq!(source.pixels[7], 128);
        assert!(styled
            .pixels
            .chunks_exact(4)
            .any(|pixel| pixel[3] > 0 && pixel[3] < 72 && pixel[..3] == [0, 0, 0]));
    }

    #[test]
    fn shadow_size_is_checked_before_allocating_output() {
        let source = Document::from_rgba(32_768, 1, 32_768 * 4, &vec![0; 32_768 * 4]).unwrap();
        assert!(matches!(
            source.add_window_shadow(),
            Err(Error::ResourceLimit)
        ));
    }
}
