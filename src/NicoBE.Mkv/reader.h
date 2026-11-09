#pragma once

#include <stdint.h>

#ifdef NICO_MKV_EXPORT
#define NICO_MKV_API __declspec(dllexport)
#else
#define NICO_MKV_API __declspec(dllimport)
#endif

enum NicoMkvEvent {
    NICO_MKV_RECORDING_TAG = 1,
    NICO_MKV_TRACK_NAME,
    NICO_MKV_ASS_HEADER,
    NICO_MKV_ASS_PACKET,
    NICO_MKV_SUBTITLE_COUNT /* track carries the total count, including non-ASS subtitles. */
};

/* Text is UTF-8, passed with its length. Bytes are valid only during the callback.
   For a packet, start and duration are in microseconds. INT64_MIN means no PTS.
   Returning nonzero stops reading. */
typedef int (__cdecl *NicoMkvCallback)(void *context, int event, int track,
                                     int64_t start, int64_t duration,
                                     const uint8_t *bytes, int length);
typedef int (__cdecl *NicoMkvCancel)(void *context);

NICO_MKV_API int __cdecl nico_mkv_read(const char *utf8_path, NicoMkvCallback callback,
                                    NicoMkvCancel cancel, void *context);
