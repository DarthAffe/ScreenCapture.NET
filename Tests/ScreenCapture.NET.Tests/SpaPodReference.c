// Prints the SPA PODs of SpaPodTests as built by libspa's own builder (the expected bytes of the tests).
// Needs the headers of libpipewire-0.3 and libspa-0.2 (Debian: libpipewire-0.3-dev):
//   gcc SpaPodReference.c -o SpaPodReference -I/usr/include/pipewire-0.3 -I/usr/include/spa-0.2 && ./SpaPodReference
#include <stdio.h>
#include <spa/param/video/format-utils.h>
#include <spa/param/buffers.h>
#include <spa/buffer/buffer.h>
#include <spa/buffer/meta.h>
#include <spa/pod/builder.h>

static void print(const char *name, const struct spa_pod *pod)
{
    const uint8_t *bytes = (const uint8_t *)pod;
    printf("%s (%u bytes):\n", name, (unsigned)SPA_POD_SIZE(pod));
    for (uint32_t i = 0; i < SPA_POD_SIZE(pod); i++)
        printf("%02X%s", bytes[i], (i % 32) == 31 ? "\n" : "");
    printf("\n\n");
}

int main(void)
{
    uint8_t buffer[4096];
    struct spa_pod_builder b;

    // SpaPod.BuildEnumFormat(1920, 1080)
    b = (struct spa_pod_builder)SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    print("EnumFormat 1920x1080", spa_pod_builder_add_object(&b, SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
        SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        SPA_FORMAT_VIDEO_format, SPA_POD_CHOICE_ENUM_Id(3, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRA),
        SPA_FORMAT_VIDEO_size, SPA_POD_CHOICE_RANGE_Rectangle(&SPA_RECTANGLE(1920, 1080), &SPA_RECTANGLE(1, 1), &SPA_RECTANGLE(16384, 16384)),
        SPA_FORMAT_VIDEO_framerate, SPA_POD_CHOICE_RANGE_Fraction(&SPA_FRACTION(0, 1), &SPA_FRACTION(0, 1), &SPA_FRACTION(1000, 1))));

    // SpaPod.BuildBuffers()
    b = (struct spa_pod_builder)SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    print("Buffers", spa_pod_builder_add_object(&b, SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
        SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int((1 << SPA_DATA_MemPtr) | (1 << SPA_DATA_MemFd))));

    // SpaPod.BuildMetaHeader()
    b = (struct spa_pod_builder)SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    print("Meta header", spa_pod_builder_add_object(&b, SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
        SPA_PARAM_META_type, SPA_POD_Id(SPA_META_Header),
        SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_header))));

    // A negotiated format like KWin's: fixed values, with a property the reader doesn't know (max frame rate) before the size.
    b = (struct spa_pod_builder)SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    print("Format BGRA 2560x1440", spa_pod_builder_add_object(&b, SPA_TYPE_OBJECT_Format, SPA_PARAM_Format,
        SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        SPA_FORMAT_VIDEO_format, SPA_POD_Id(SPA_VIDEO_FORMAT_BGRA),
        SPA_FORMAT_VIDEO_maxFramerate, SPA_POD_CHOICE_RANGE_Fraction(&SPA_FRACTION(60, 1), &SPA_FRACTION(1, 1), &SPA_FRACTION(60, 1)),
        SPA_FORMAT_VIDEO_size, SPA_POD_Rectangle(&SPA_RECTANGLE(2560, 1440)),
        SPA_FORMAT_VIDEO_framerate, SPA_POD_Fraction(&SPA_FRACTION(0, 1))));

    // The same with the pixel format and the size as choices (their defaults count).
    b = (struct spa_pod_builder)SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    print("Format choices BGRx 800x600", spa_pod_builder_add_object(&b, SPA_TYPE_OBJECT_Format, SPA_PARAM_Format,
        SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        SPA_FORMAT_VIDEO_format, SPA_POD_CHOICE_ENUM_Id(2, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRA),
        SPA_FORMAT_VIDEO_size, SPA_POD_CHOICE_RANGE_Rectangle(&SPA_RECTANGLE(800, 600), &SPA_RECTANGLE(1, 1), &SPA_RECTANGLE(4096, 4096))));

    // Not raw video.
    b = (struct spa_pod_builder)SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    print("Format mjpg", spa_pod_builder_add_object(&b, SPA_TYPE_OBJECT_Format, SPA_PARAM_Format,
        SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_mjpg),
        SPA_FORMAT_VIDEO_size, SPA_POD_Rectangle(&SPA_RECTANGLE(640, 480))));

    return 0;
}
