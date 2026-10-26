use crate::{allocate, Document, Error};

pub(super) fn thumbnail(source: &Document, width: u32, height: u32) -> Result<Vec<u8>, Error> {
    if source.width > width * 2 || source.height > height * 2 {
        area(source, width, height)
    } else {
        bilinear(source, width, height)
    }
}

fn bilinear(source: &Document, width: u32, height: u32) -> Result<Vec<u8>, Error> {
    let mut output = allocate(width as usize * height as usize * 4)?;
    if width == source.width && height == source.height {
        output.extend_from_slice(&source.pixels);
        return Ok(output);
    }
    for y in 0..height {
        let (y0, y1, fy) = sample_axis(y, height, source.height);
        for x in 0..width {
            let (x0, x1, fx) = sample_axis(x, width, source.width);
            let samples = [
                (x0, y0, (1.0 - fx) * (1.0 - fy)),
                (x1, y0, fx * (1.0 - fy)),
                (x0, y1, (1.0 - fx) * fy),
                (x1, y1, fx * fy),
            ];
            let mut alpha = 0.0;
            let mut rgb = [0.0; 3];
            for (sx, sy, weight) in samples {
                let offset = (sy as usize * source.width as usize + sx as usize) * 4;
                let alpha_weight = f64::from(source.pixels[offset + 3]) * weight;
                alpha += alpha_weight;
                for (channel, sum) in rgb.iter_mut().enumerate() {
                    *sum += f64::from(source.pixels[offset + channel]) * alpha_weight;
                }
            }
            let output_alpha = alpha.round().clamp(0.0, 255.0) as u8;
            if output_alpha == 0 {
                output.extend_from_slice(&[0; 4]);
            } else {
                // Interpolate premultiplied channels, then return straight RGBA.
                // Hidden RGB in transparent pixels must not tint visible edges.
                for channel in rgb {
                    output.push((channel / alpha).round().clamp(0.0, 255.0) as u8);
                }
                output.push(output_alpha);
            }
        }
    }
    Ok(output)
}

// Coordinates use destination-sized integer units: each destination interval
// has exactly `source` units, so weighted coverage needs no floating point.
struct AreaSpan {
    first: usize,
    last: usize,
    first_weight: u64,
    last_weight: u64,
}

impl AreaSpan {
    fn new(position: u32, source: u32, destination: u32) -> Self {
        let start = u64::from(position) * u64::from(source);
        let end = u64::from(position + 1) * u64::from(source);
        let unit = u64::from(destination);
        let first = start / unit;
        let last = (end - 1) / unit;
        Self {
            first: first as usize,
            last: last as usize,
            first_weight: if first == last {
                end - start
            } else {
                (first + 1) * unit - start
            },
            last_weight: end - last * unit,
        }
    }

    fn weight(&self, position: usize, full_weight: u64) -> u64 {
        if position == self.first {
            self.first_weight
        } else if position == self.last {
            self.last_weight
        } else {
            full_weight
        }
    }
}

fn area(source: &Document, width: u32, height: u32) -> Result<Vec<u8>, Error> {
    let mut output = allocate(width as usize * height as usize * 4)?;
    let mut horizontal = Vec::new();
    horizontal
        .try_reserve_exact(width as usize)
        .map_err(|_| Error::AllocationFailed)?;
    horizontal.extend((0..width).map(|x| AreaSpan::new(x, source.width, width)));
    let mut sampled_row = zeroed_row(width as usize)?;
    let mut destination_row = zeroed_row(width as usize)?;
    let mut cached_source_y = None;
    let total_weight = u64::from(source.width) * u64::from(source.height);

    for y in 0..height {
        destination_row.fill([0; 4]);
        let vertical = AreaSpan::new(y, source.height, height);
        for source_y in vertical.first..=vertical.last {
            if cached_source_y != Some(source_y) {
                sample_row(source, source_y, &horizontal, width, &mut sampled_row);
                cached_source_y = Some(source_y);
            }
            let weight = vertical.weight(source_y, u64::from(height));
            for (destination, sampled) in destination_row.iter_mut().zip(&sampled_row) {
                for (value, sample) in destination.iter_mut().zip(sampled) {
                    *value += sample * weight;
                }
            }
        }
        for pixel in &destination_row {
            let alpha = (pixel[3] + total_weight / 2) / total_weight;
            if alpha == 0 {
                output.extend_from_slice(&[0; 4]);
            } else {
                for value in &pixel[..3] {
                    output.push(((value + pixel[3] / 2) / pixel[3]) as u8);
                }
                output.push(alpha as u8);
            }
        }
    }
    Ok(output)
}

fn zeroed_row(width: usize) -> Result<Vec<[u64; 4]>, Error> {
    let mut row = Vec::new();
    row.try_reserve_exact(width)
        .map_err(|_| Error::AllocationFailed)?;
    row.resize(width, [0; 4]);
    Ok(row)
}

fn sample_row(source: &Document, y: usize, spans: &[AreaSpan], width: u32, row: &mut [[u64; 4]]) {
    for (destination, span) in row.iter_mut().zip(spans) {
        *destination = [0; 4];
        for x in span.first..=span.last {
            let offset = (y * source.width as usize + x) * 4;
            let weighted_alpha =
                u64::from(source.pixels[offset + 3]) * span.weight(x, u64::from(width));
            for (channel, value) in destination[..3].iter_mut().enumerate() {
                *value += u64::from(source.pixels[offset + channel]) * weighted_alpha;
            }
            destination[3] += weighted_alpha;
        }
    }
}

fn sample_axis(position: u32, destination: u32, source: u32) -> (u32, u32, f64) {
    let center = ((f64::from(position) + 0.5) * f64::from(source) / f64::from(destination) - 0.5)
        .clamp(0.0, f64::from(source - 1));
    let first = center.floor() as u32;
    (
        first,
        (first + 1).min(source - 1),
        center - f64::from(first),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn thumbnail_averages_neighboring_opaque_pixels() {
        let document = Document::from_rgba(
            2,
            2,
            8,
            &[
                255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255,
            ],
        )
        .unwrap();
        assert_eq!(document.preview(1, 1).unwrap().pixels, [128, 128, 128, 255]);
    }

    #[test]
    fn transparent_rgb_does_not_bleed_into_visible_edges() {
        let document = Document::from_rgba(2, 1, 8, &[255, 0, 0, 255, 0, 0, 255, 0]).unwrap();
        assert_eq!(document.preview(1, 1).unwrap().pixels, [255, 0, 0, 128]);
        let transparent = Document::from_rgba(2, 1, 8, &[255, 99, 0, 0, 0, 2, 255, 0]).unwrap();
        assert_eq!(transparent.preview(1, 1).unwrap().pixels, [0, 0, 0, 0]);
    }

    #[test]
    fn strong_reduction_keeps_thin_stripes_instead_of_skipping_them() {
        let source: Vec<u8> = (0..12)
            .flat_map(|x| {
                let value = if x % 4 == 0 { 0 } else { 255 };
                [value, value, value, 255]
            })
            .collect();
        let document = Document::from_rgba(12, 1, 48, &source).unwrap();
        let thumbnail = document.preview(3, 1).unwrap();
        assert_eq!(thumbnail.pixels, [191, 191, 191, 255].repeat(3));
    }

    #[test]
    fn area_filter_ignores_hidden_rgb_and_retains_partial_alpha() {
        let mut source = [0, 0, 255, 0].repeat(12);
        source[..4].copy_from_slice(&[255, 0, 0, 255]);
        let document = Document::from_rgba(12, 1, 48, &source).unwrap();
        assert_eq!(
            document.preview(3, 1).unwrap().pixels,
            [255, 0, 0, 64, 0, 0, 0, 0, 0, 0, 0, 0]
        );
    }

    #[test]
    fn fractional_asymmetric_reduction_weights_both_axes() {
        let source: Vec<u8> = (0..5)
            .flat_map(|y| (0..7).flat_map(move |x| [x * 30, y * 40, 17, 255]))
            .collect();
        let document = Document::from_rgba(7, 5, 28, &source).unwrap();
        let thumbnail = document.preview(3, 3).unwrap();
        assert_eq!((thumbnail.width, thumbnail.height), (3, 2));
        assert_eq!(
            thumbnail.pixels,
            [
                21, 32, 17, 255, 90, 32, 17, 255, 159, 32, 17, 255, 21, 128, 17, 255, 90, 128, 17,
                255, 159, 128, 17, 255,
            ]
        );
    }

    #[test]
    fn large_noninteger_reduction_keeps_uniform_color() {
        let source = [17, 83, 149, 255].repeat(3840 * 2160);
        let document = Document::from_rgba(3840, 2160, 3840 * 4, &source).unwrap();
        let thumbnail = document.preview(600, 360).unwrap();
        assert_eq!((thumbnail.width, thumbnail.height), (600, 337));
        assert!(thumbnail
            .pixels
            .chunks_exact(4)
            .all(|pixel| pixel == [17, 83, 149, 255]));
    }
}
