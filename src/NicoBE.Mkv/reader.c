#define NICO_MKV_EXPORT
#include "reader.h"

#include <errno.h>
#include <string.h>
#include <libavformat/avformat.h>
#include <libavutil/mathematics.h>

static int is_ass(const AVStream *stream)
{
    return stream->codecpar->codec_id == AV_CODEC_ID_ASS ||
           stream->codecpar->codec_id == AV_CODEC_ID_SSA;
}

NICO_MKV_API int __cdecl nico_mkv_read(const char *path, NicoMkvCallback callback,
                                    NicoMkvCancel cancel, void *context)
{
    if (!path || !callback) return AVERROR(EINVAL);
    const AVInputFormat *demuxer = av_find_input_format("matroska");
    if (!demuxer) return AVERROR_DEMUXER_NOT_FOUND;

    AVFormatContext *input = avformat_alloc_context();
    if (!input) return AVERROR(ENOMEM);
    input->interrupt_callback.callback = cancel;
    input->interrupt_callback.opaque = context;
    AVPacket *packet = NULL;
    int result = avformat_open_input(&input, path, demuxer, NULL);
    if (result < 0) return result;

    const AVDictionaryEntry *tag = av_dict_get(input->metadata, "NICOBE_RECORDING", NULL, 0);
    if (tag && callback(context, NICO_MKV_RECORDING_TAG, -1, 0, 0,
                        (const uint8_t *)tag->value, (int)strlen(tag->value))) {
        result = AVERROR_EXIT;
        goto cleanup;
    }

    int subtitle_count = 0;
    for (unsigned i = 0; i < input->nb_streams; ++i)
        subtitle_count += input->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_SUBTITLE;
    if (callback(context, NICO_MKV_SUBTITLE_COUNT, subtitle_count, 0, 0, NULL, 0)) {
        result = AVERROR_EXIT;
        goto cleanup;
    }

    unsigned ass_count = 0;
    for (unsigned i = 0; i < input->nb_streams; ++i) {
        AVStream *stream = input->streams[i];
        if (!is_ass(stream)) {
            stream->discard = AVDISCARD_ALL;
            continue;
        }
        ++ass_count;
        const AVDictionaryEntry *title = av_dict_get(stream->metadata, "title", NULL, 0);
        const char *name = title ? title->value : "";
        if (callback(context, NICO_MKV_TRACK_NAME, stream->index, 0, 0,
                     (const uint8_t *)name, (int)strlen(name)) ||
            callback(context, NICO_MKV_ASS_HEADER, stream->index, 0, 0,
                     stream->codecpar->extradata, stream->codecpar->extradata_size)) {
            result = AVERROR_EXIT;
            goto cleanup;
        }
    }

    if (!ass_count) goto cleanup;
    packet = av_packet_alloc();
    if (!packet) {
        result = AVERROR(ENOMEM);
        goto cleanup;
    }

    while ((result = av_read_frame(input, packet)) >= 0) {
        if (cancel && cancel(context)) {
            result = AVERROR_EXIT;
            break;
        }
        const AVStream *stream = input->streams[packet->stream_index];
        if (is_ass(stream)) {
            int64_t start = av_rescale_q_rnd(packet->pts, stream->time_base, AV_TIME_BASE_Q,
                                            AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
            int64_t duration = av_rescale_q(packet->duration, stream->time_base, AV_TIME_BASE_Q);
            if (callback(context, NICO_MKV_ASS_PACKET, stream->index, start, duration,
                         packet->data, packet->size)) {
                result = AVERROR_EXIT;
                break;
            }
        }
        av_packet_unref(packet);
    }
    if (result == AVERROR_EOF) result = 0;

cleanup:
    av_packet_free(&packet);
    avformat_close_input(&input);
    return result;
}
