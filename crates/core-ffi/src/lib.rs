//! Small C ABI shared by the native Windows and macOS clients.
//! See bindings/include/ovi_core.h for the caller/lifetime contract.

#![deny(unsafe_op_in_unsafe_fn)]

use ovi_image_core::{Document, Error};
use std::ffi::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

pub const API_VERSION: u32 = 2;
pub const OK: i32 = 0;
pub const INVALID_ARGUMENT: i32 = 1;
pub const INVALID_DIMENSIONS: i32 = 2;
pub const UNSUPPORTED_FORMAT: i32 = 3;
pub const BUFFER_TOO_SMALL: i32 = 4;
pub const RESOURCE_LIMIT: i32 = 5;
pub const ENCODING_FAILED: i32 = 6;
pub const ALLOCATION_FAILED: i32 = 7;
pub const INTERNAL_ERROR: i32 = 255;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct OviImageDescriptor {
    pub width: u32,
    pub height: u32,
    pub stride_bytes: u64,
    pub pixel_format: u32,
    pub alpha_mode: u32,
    pub color_space: u32,
    pub reserved: u32,
}

impl OviImageDescriptor {
    fn packed(width: u32, height: u32) -> Self {
        Self {
            width,
            height,
            stride_bytes: u64::from(width) * 4,
            pixel_format: 1,
            alpha_mode: 1,
            color_space: 1,
            reserved: 0,
        }
    }

    fn validate_format(&self) -> Result<(), i32> {
        if self.reserved != 0 {
            return Err(INVALID_ARGUMENT);
        }
        if self.pixel_format != 1 || self.alpha_mode != 1 || self.color_space != 1 {
            return Err(UNSUPPORTED_FORMAT);
        }
        Ok(())
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct OviDocumentInfo {
    pub image: OviImageDescriptor,
    pub revision: u64,
}

pub struct OviDocument {
    inner: Document,
}

pub struct OviBuffer {
    bytes: Vec<u8>,
}

fn error_code(error: Error) -> i32 {
    match error {
        Error::InvalidArgument => INVALID_ARGUMENT,
        Error::InvalidDimensions => INVALID_DIMENSIONS,
        Error::BufferTooSmall => BUFFER_TOO_SMALL,
        Error::ResourceLimit => RESOURCE_LIMIT,
        Error::EncodingFailed => ENCODING_FAILED,
        Error::AllocationFailed => ALLOCATION_FAILED,
    }
}

fn boundary(operation: impl FnOnce() -> Result<(), i32>) -> i32 {
    match catch_unwind(AssertUnwindSafe(operation)) {
        Ok(Ok(())) => OK,
        Ok(Err(status)) => status,
        Err(_) => INTERNAL_ERROR,
    }
}

#[no_mangle]
pub extern "C" fn ovi_core_api_version() -> u32 {
    API_VERSION
}

#[no_mangle]
pub extern "C" fn ovi_status_message(status: i32) -> *const c_char {
    let message: &'static [u8] = match status {
        OK => b"Success\0",
        INVALID_ARGUMENT => b"Invalid argument, pointer, reserved field, or row stride\0",
        INVALID_DIMENSIONS => b"Image and preview dimensions must be positive\0",
        UNSUPPORTED_FORMAT => b"Expected SDR sRGB RGBA8 with straight alpha\0",
        BUFFER_TOO_SMALL => b"Input buffer does not contain every image row\0",
        RESOURCE_LIMIT => b"Image exceeds the configured dimension or memory limit\0",
        ENCODING_FAILED => b"PNG encoding failed\0",
        ALLOCATION_FAILED => b"Image allocation failed\0",
        INTERNAL_ERROR => b"Unexpected internal failure; retry with a new document\0",
        _ => b"Unknown status code\0",
    };
    message.as_ptr().cast()
}

/// Copies pixels into an owned immutable document.
/// # Safety
/// Non-null pointers must be valid for their declared lengths and output types.
#[no_mangle]
pub unsafe extern "C" fn ovi_document_create_from_pixels(
    descriptor: *const OviImageDescriptor,
    data: *const u8,
    data_length: u64,
    out_document: *mut *mut OviDocument,
) -> i32 {
    boundary(|| {
        if out_document.is_null() {
            return Err(INVALID_ARGUMENT);
        }
        unsafe { out_document.write(ptr::null_mut()) };
        if descriptor.is_null() || data.is_null() {
            return Err(INVALID_ARGUMENT);
        }
        let descriptor = unsafe { *descriptor };
        descriptor.validate_format()?;
        let layout = ovi_image_core::validate_input(
            descriptor.width,
            descriptor.height,
            descriptor.stride_bytes,
            data_length,
        )
        .map_err(error_code)?;
        // Validation happens BEFORE constructing any slice. Only bytes actually
        // needed for the rows are borrowed, never the caller's trailing padding.
        let bytes = unsafe { std::slice::from_raw_parts(data, layout.required_input_length) };
        let inner = Document::from_rgba(
            descriptor.width,
            descriptor.height,
            descriptor.stride_bytes,
            bytes,
        )
        .map_err(error_code)?;
        unsafe { out_document.write(Box::into_raw(Box::new(OviDocument { inner }))) };
        Ok(())
    })
}

/// Returns packed document metadata.
/// # Safety
/// The document must be live; output must be writable and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn ovi_document_get_info(
    document: *const OviDocument,
    out_info: *mut OviDocumentInfo,
) -> i32 {
    boundary(|| {
        if out_info.is_null() {
            return Err(INVALID_ARGUMENT);
        }
        unsafe { out_info.write(OviDocumentInfo::default()) };
        let document = unsafe { document.as_ref() }.ok_or(INVALID_ARGUMENT)?;
        unsafe {
            out_info.write(OviDocumentInfo {
                image: OviImageDescriptor::packed(document.inner.width(), document.inner.height()),
                revision: 1,
            })
        };
        Ok(())
    })
}

/// Produces a new owned document with the portable window presentation style.
/// # Safety
/// Source must be live; output must be writable and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn ovi_document_add_window_shadow(
    document: *const OviDocument,
    out_document: *mut *mut OviDocument,
) -> i32 {
    boundary(|| {
        if out_document.is_null() {
            return Err(INVALID_ARGUMENT);
        }
        unsafe { out_document.write(ptr::null_mut()) };
        let document = unsafe { document.as_ref() }.ok_or(INVALID_ARGUMENT)?;
        let inner = document.inner.add_window_shadow().map_err(error_code)?;
        unsafe { out_document.write(Box::into_raw(Box::new(OviDocument { inner }))) };
        Ok(())
    })
}

/// Renders a bounded raw bitmap, with metadata matching the returned buffer.
/// # Safety
/// Handle must be live; both outputs must be writable and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn ovi_document_render_preview(
    document: *const OviDocument,
    max_width: u32,
    max_height: u32,
    out_buffer: *mut *mut OviBuffer,
    out_image: *mut OviImageDescriptor,
) -> i32 {
    boundary(|| {
        // Reset every available output, including when another output is null.
        if !out_buffer.is_null() {
            unsafe { out_buffer.write(ptr::null_mut()) };
        }
        if !out_image.is_null() {
            unsafe { out_image.write(OviImageDescriptor::default()) };
        }
        if out_buffer.is_null() || out_image.is_null() {
            return Err(INVALID_ARGUMENT);
        }
        let document = unsafe { document.as_ref() }.ok_or(INVALID_ARGUMENT)?;
        let bitmap = document
            .inner
            .preview(max_width, max_height)
            .map_err(error_code)?;
        let descriptor = OviImageDescriptor::packed(bitmap.width, bitmap.height);
        let buffer = Box::new(OviBuffer {
            bytes: bitmap.pixels,
        });
        unsafe {
            out_image.write(descriptor);
            out_buffer.write(Box::into_raw(buffer));
        }
        Ok(())
    })
}

/// Returns an owned buffer of encoded PNG data.
/// # Safety
/// Document must be live and output must be writable and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn ovi_document_export_png(
    document: *const OviDocument,
    out_buffer: *mut *mut OviBuffer,
) -> i32 {
    boundary(|| {
        if out_buffer.is_null() {
            return Err(INVALID_ARGUMENT);
        }
        unsafe { out_buffer.write(ptr::null_mut()) };
        let document = unsafe { document.as_ref() }.ok_or(INVALID_ARGUMENT)?;
        let bytes = document.inner.export_png().map_err(error_code)?;
        unsafe { out_buffer.write(Box::into_raw(Box::new(OviBuffer { bytes }))) };
        Ok(())
    })
}

/// Borrows a buffer's bytes until its release.
/// # Safety
/// Buffer must be live; output pointers must be writable and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn ovi_buffer_get_view(
    buffer: *const OviBuffer,
    out_data: *mut *const u8,
    out_length: *mut u64,
) -> i32 {
    boundary(|| {
        if !out_data.is_null() {
            unsafe { out_data.write(ptr::null()) };
        }
        if !out_length.is_null() {
            unsafe { out_length.write(0) };
        }
        if out_data.is_null() || out_length.is_null() {
            return Err(INVALID_ARGUMENT);
        }
        let buffer = unsafe { buffer.as_ref() }.ok_or(INVALID_ARGUMENT)?;
        unsafe {
            out_data.write(buffer.bytes.as_ptr());
            out_length.write(buffer.bytes.len() as u64);
        }
        Ok(())
    })
}

/// Releases a buffer; a null handle is a no-op.
/// # Safety
/// Non-null handle must be a live buffer returned by this library, released once.
#[no_mangle]
pub unsafe extern "C" fn ovi_buffer_release(buffer: *mut OviBuffer) -> i32 {
    boundary(|| {
        if !buffer.is_null() {
            unsafe { drop(Box::from_raw(buffer)) };
        }
        Ok(())
    })
}

/// Releases a document; a null handle is a no-op.
/// # Safety
/// Non-null handle must be a live document from this library, released once.
#[no_mangle]
pub unsafe extern "C" fn ovi_document_release(document: *mut OviDocument) -> i32 {
    boundary(|| {
        if !document.is_null() {
            unsafe { drop(Box::from_raw(document)) };
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;
    use std::mem::{align_of, offset_of, size_of};

    #[test]
    fn abi_struct_layout_matches_the_c_header() {
        assert_eq!(size_of::<OviImageDescriptor>(), 32);
        assert_eq!(align_of::<OviImageDescriptor>(), 8);
        assert_eq!(offset_of!(OviImageDescriptor, stride_bytes), 8);
        assert_eq!(offset_of!(OviImageDescriptor, pixel_format), 16);
        assert_eq!(offset_of!(OviImageDescriptor, reserved), 28);
        assert_eq!(size_of::<OviDocumentInfo>(), 40);
        assert_eq!(offset_of!(OviDocumentInfo, revision), 32);
        assert_eq!(ovi_core_api_version(), API_VERSION);
    }

    #[test]
    fn document_and_output_lifetimes_are_independent() {
        unsafe {
            let descriptor = OviImageDescriptor {
                stride_bytes: 12,
                ..OviImageDescriptor::packed(2, 2)
            };
            let expected = [1, 2, 3, 255, 4, 5, 6, 128, 7, 8, 9, 0, 10, 11, 12, 64];
            let mut input = [&expected[..8], &[90, 91, 92, 93], &expected[8..]].concat();
            let mut document = ptr::null_mut();
            assert_eq!(
                ovi_document_create_from_pixels(
                    &descriptor,
                    input.as_ptr(),
                    input.len() as u64,
                    &mut document
                ),
                OK
            );
            input.fill(0);
            let mut info = OviDocumentInfo::default();
            assert_eq!(ovi_document_get_info(document, &mut info), OK);
            assert_eq!(
                (
                    info.image.width,
                    info.image.height,
                    info.image.stride_bytes,
                    info.revision
                ),
                (2, 2, 8, 1)
            );
            let mut png_buffer = ptr::null_mut();
            assert_eq!(ovi_document_export_png(document, &mut png_buffer), OK);
            assert_eq!(ovi_document_release(document), OK);
            let mut view = ptr::null();
            let mut length = 0;
            assert_eq!(ovi_buffer_get_view(png_buffer, &mut view, &mut length), OK);
            let bytes = std::slice::from_raw_parts(view, length as usize);
            let mut reader = png::Decoder::new(Cursor::new(bytes)).read_info().unwrap();
            let mut decoded = vec![0; reader.output_buffer_size().unwrap()];
            let frame = reader.next_frame(&mut decoded).unwrap();
            assert_eq!(&decoded[..frame.buffer_size()], expected);
            assert_eq!(ovi_buffer_release(png_buffer), OK);
        }
    }

    #[test]
    fn preview_returns_its_actual_raw_layout() {
        unsafe {
            let descriptor = OviImageDescriptor::packed(4, 2);
            let source = [99u8; 32];
            let mut document = ptr::null_mut();
            assert_eq!(
                ovi_document_create_from_pixels(&descriptor, source.as_ptr(), 32, &mut document),
                OK
            );
            let mut buffer = ptr::null_mut();
            let mut preview = OviImageDescriptor::default();
            assert_eq!(
                ovi_document_render_preview(document, 2, 2, &mut buffer, &mut preview),
                OK
            );
            assert_eq!(
                (preview.width, preview.height, preview.stride_bytes),
                (2, 1, 8)
            );
            assert_eq!(
                (
                    preview.pixel_format,
                    preview.alpha_mode,
                    preview.color_space
                ),
                (1, 1, 1)
            );
            let mut view = ptr::null();
            let mut length = 0;
            assert_eq!(ovi_buffer_get_view(buffer, &mut view, &mut length), OK);
            assert_eq!(length, 8);
            assert_eq!(std::slice::from_raw_parts(view, 8), &[99; 8]);
            assert_eq!(ovi_document_release(document), OK);
            assert_eq!(ovi_buffer_release(buffer), OK);
        }
    }

    #[test]
    fn invalid_calls_reset_out_values_without_allocating() {
        unsafe {
            let descriptor = OviImageDescriptor::packed(2, 2);
            let source = [0u8; 16];
            let mut document = std::ptr::dangling_mut::<OviDocument>();
            assert_eq!(
                ovi_document_create_from_pixels(&descriptor, source.as_ptr(), 15, &mut document),
                BUFFER_TOO_SMALL
            );
            assert!(document.is_null());
            let unsupported = OviImageDescriptor {
                alpha_mode: 99,
                ..descriptor
            };
            assert_eq!(
                ovi_document_create_from_pixels(&unsupported, source.as_ptr(), 16, &mut document),
                UNSUPPORTED_FORMAT
            );
            let mut image = descriptor;
            let mut buffer = std::ptr::dangling_mut::<OviBuffer>();
            assert_eq!(
                ovi_document_render_preview(ptr::null(), 1, 1, &mut buffer, &mut image),
                INVALID_ARGUMENT
            );
            assert!(buffer.is_null());
            assert_eq!(image.width, 0);
            let mut view = std::ptr::dangling::<u8>();
            let mut length = 99;
            assert_eq!(
                ovi_buffer_get_view(ptr::null(), &mut view, &mut length),
                INVALID_ARGUMENT
            );
            assert!(view.is_null());
            assert_eq!(length, 0);
            assert_eq!(ovi_document_release(ptr::null_mut()), OK);
            assert_eq!(ovi_buffer_release(ptr::null_mut()), OK);
        }
    }

    #[test]
    fn styled_document_is_independent_of_source_lifetime() {
        unsafe {
            let descriptor = OviImageDescriptor::packed(1, 1);
            let source = [90, 30, 180, 128];
            let mut document = ptr::null_mut();
            assert_eq!(
                ovi_document_create_from_pixels(&descriptor, source.as_ptr(), 4, &mut document),
                OK
            );
            let mut styled = ptr::null_mut();
            assert_eq!(ovi_document_add_window_shadow(document, &mut styled), OK);
            assert_ne!(styled, document);
            assert_eq!(ovi_document_release(document), OK);
            let mut info = OviDocumentInfo::default();
            assert_eq!(ovi_document_get_info(styled, &mut info), OK);
            assert_eq!((info.image.width, info.image.height), (49, 49));
            let mut encoded = ptr::null_mut();
            assert_eq!(ovi_document_export_png(styled, &mut encoded), OK);
            assert_eq!(ovi_document_release(styled), OK);
            assert_eq!(ovi_buffer_release(encoded), OK);
            let mut failed = ptr::dangling_mut();
            assert_eq!(
                ovi_document_add_window_shadow(ptr::null(), &mut failed),
                INVALID_ARGUMENT
            );
            assert!(failed.is_null());
        }
    }

    #[test]
    fn panic_is_contained_and_status_messages_are_static_strings() {
        assert_eq!(
            boundary(|| panic!("simulated boundary failure")),
            INTERNAL_ERROR
        );
        let message = unsafe { std::ffi::CStr::from_ptr(ovi_status_message(UNSUPPORTED_FORMAT)) };
        assert!(message.to_str().unwrap().contains("straight alpha"));
    }
}
