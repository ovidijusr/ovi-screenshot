#ifndef OVI_CORE_H
#define OVI_CORE_H

#include <stdint.h>

#if defined(_WIN32)
#define OVI_CALL __cdecl
#else
#define OVI_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ABI version 2. The first release targets 64-bit platforms.
 * Header and wrappers must agree on this version before other calls.
 * All functions except version/message return an OviStatus as int32_t.
 * No function invokes callbacks or retains borrowed input pointers.
 */
#define OVI_CORE_API_VERSION 2u
#define OVI_WINDOW_SHADOW_PADDING 24u

typedef struct OviDocument OviDocument;
typedef struct OviBuffer OviBuffer;

enum OviStatus {
    OVI_OK = 0,
    OVI_INVALID_ARGUMENT = 1,
    OVI_INVALID_DIMENSIONS = 2,
    OVI_UNSUPPORTED_FORMAT = 3,
    OVI_BUFFER_TOO_SMALL = 4,
    OVI_RESOURCE_LIMIT = 5,
    OVI_ENCODING_FAILED = 6,
    OVI_ALLOCATION_FAILED = 7,
    OVI_INTERNAL_ERROR = 255
};

enum OviPixelFormat { OVI_RGBA8 = 1 };
enum OviAlphaMode { OVI_ALPHA_STRAIGHT = 1 };
enum OviColorSpace { OVI_SRGB = 1 };

/* Native ABI layout: 32 bytes, 8-byte alignment; no packing override.
 * Rows run top to bottom; pixels run left to right.
 * stride_bytes >= width * 4. Import requires at least
 * (height - 1) * stride_bytes + width * 4 readable bytes.
 * Padding is discarded; no padding is required after the last row.
 * Only RGBA8, straight alpha and SDR sRGB are accepted by ABI v2.
 * reserved must be zero. Returned bitmaps are tightly packed.
 */
typedef struct OviImageDescriptor {
    uint32_t width;
    uint32_t height;
    uint64_t stride_bytes;
    uint32_t pixel_format;
    uint32_t alpha_mode;
    uint32_t color_space;
    uint32_t reserved;
} OviImageDescriptor;

/* Native ABI layout: 40 bytes, 8-byte alignment. */
typedef struct OviDocumentInfo {
    OviImageDescriptor image;
    uint64_t revision;
} OviDocumentInfo;

uint32_t OVI_CALL ovi_core_api_version(void);
/* A borrowed, NUL-terminated static UTF-8 message. Never free it. */
const char *OVI_CALL ovi_status_message(int32_t status);

/* All pointer arguments must be valid/aligned for the declared type.
 * The caller guarantees readable/writable memory for stated lengths;
 * invalid/dangling non-NULL pointers cannot be validated by this ABI.
 * Returned out values are reset before ordinary validation failures.
 */
int32_t OVI_CALL ovi_document_create_from_pixels(
    const OviImageDescriptor *descriptor,
    const uint8_t *data,
    uint64_t data_length,
    OviDocument **out_document);

int32_t OVI_CALL ovi_document_get_info(
    const OviDocument *document,
    OviDocumentInfo *out_info);

/* New independently owned document: transparent 24px padding on every side,
 * generated soft black shadow beneath source content. Source is unchanged.
 * Presentation effect only; does not claim to reproduce native DWM shadows.
 */
int32_t OVI_CALL ovi_document_add_window_shadow(
    const OviDocument *document,
    OviDocument **out_document);

/* Fits inside positive max dimensions, preserving aspect ratio; never
 * upscales. Uses alpha-aware area/bilinear resampling. out_image describes
 * the returned RAW RGBA bitmap, not the original document dimensions.
 */
int32_t OVI_CALL ovi_document_render_preview(
    const OviDocument *document,
    uint32_t max_width,
    uint32_t max_height,
    OviBuffer **out_buffer,
    OviImageDescriptor *out_image);

/* Encoded, flattened PNG bytes; includes an sRGB declaration, no editor
 * payload. Read via buffer_get_view, then buffer_release.
 */
int32_t OVI_CALL ovi_document_export_png(
    const OviDocument *document,
    OviBuffer **out_buffer);

/* Borrowed view remains valid until that buffer is released. It does
 * not depend on document lifetime. Copy or retain the buffer owner
 * across asynchronous clipboard/save/drag consumers.
 */
int32_t OVI_CALL ovi_buffer_get_view(
    const OviBuffer *buffer,
    const uint8_t **out_data,
    uint64_t *out_length);

/* Release exactly once; NULL is accepted. Serialize all use/release of
 * an individual handle. Never use a released handle or release while
 * another call is using it. Wrappers should own these via SafeHandle
 * (C#) or an equivalent Swift lifetime wrapper.
 */
int32_t OVI_CALL ovi_buffer_release(OviBuffer *buffer);
int32_t OVI_CALL ovi_document_release(OviDocument *document);

#ifdef __cplusplus
}
#endif
#endif
