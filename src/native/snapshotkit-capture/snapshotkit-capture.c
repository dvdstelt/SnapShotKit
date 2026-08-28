// snapshotkit-capture: pulls frames from a PipeWire stream on behalf of snapshotkitd.
//
// This is a separate process for one reason: libpipewire and the .NET runtime cannot reliably share
// an address space for this workload. In-process, a stream reaches STREAMING and then never receives
// a single buffer, with no error reported anywhere. Out of process it works every time. The root
// cause is unknown; the process boundary is the fix. See docs/spikes/005-thread-pool-capture-failure.md.
//
// Protocol, line based, stdin to stdout:
//
//   grab            ->  ok <width> <height> <stride> <size>
//                   ->  err <message>
//   quit            ->  (exits)
//
// Frames are written into a shared file the parent maps read-only, so a frame crosses the process
// boundary without being copied through a pipe.
//
// Usage: snapshotkit-capture <pipewire-fd> <node-id> <frame-path>

#include <pipewire/pipewire.h>
#include <spa/param/video/format-utils.h>
#include <spa/param/video/type-info.h>
#include <spa/param/param.h>
#include <spa/buffer/meta.h>

#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

#define GRAB_TIMEOUT_MS 3000

struct capture {
    struct pw_thread_loop *loop;
    struct pw_context *context;
    struct pw_core *core;
    struct pw_stream *stream;
    struct spa_hook stream_listener;
    struct spa_video_info format;
    uint32_t node_id;

    int want_frame;
    int frame_ready;
    int stream_error;

    void *destination;
    size_t destination_size;

    uint32_t width;
    uint32_t height;
    uint32_t stride;
    uint64_t size;
};

static void on_state_changed(void *data, enum pw_stream_state old, enum pw_stream_state state, const char *error)
{
    struct capture *capture = data;
    (void)old;

    if (state == PW_STREAM_STATE_ERROR) {
        fprintf(stderr, "snapshotkit-capture: stream error: %s\n", error ? error : "unknown");
        capture->stream_error = 1;
    }

    pw_thread_loop_signal(capture->loop, false);
}

static void on_param_changed(void *data, uint32_t id, const struct spa_pod *param)
{
    struct capture *capture = data;

    if (param == NULL || id != SPA_PARAM_Format) {
        return;
    }

    if (spa_format_parse(param, &capture->format.media_type, &capture->format.media_subtype) < 0) {
        return;
    }

    if (capture->format.media_type != SPA_MEDIA_TYPE_video ||
        capture->format.media_subtype != SPA_MEDIA_SUBTYPE_raw) {
        return;
    }

    if (spa_format_video_raw_parse(param, &capture->format.info.raw) < 0) {
        return;
    }

    // Format negotiation is only half of it. A client that does not answer with the buffers it can
    // accept gets a stream that reaches STREAMING and never receives one. Declaring MemPtr is what
    // says "mappable memory please, not a DMA-BUF handle I cannot read".
    int32_t stride = SPA_ROUND_UP_N((int32_t)capture->format.info.raw.size.width * 4, 4);
    int32_t size = stride * (int32_t)capture->format.info.raw.size.height;

    uint8_t builder_buffer[1024];
    struct spa_pod_builder builder = SPA_POD_BUILDER_INIT(builder_buffer, sizeof(builder_buffer));

    const struct spa_pod *params[2];

    params[0] = spa_pod_builder_add_object(&builder,
        SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
        SPA_PARAM_BUFFERS_buffers,  SPA_POD_CHOICE_RANGE_Int(4, 2, 8),
        SPA_PARAM_BUFFERS_blocks,   SPA_POD_Int(1),
        SPA_PARAM_BUFFERS_size,     SPA_POD_Int(size),
        SPA_PARAM_BUFFERS_stride,   SPA_POD_Int(stride),
        SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int(1 << SPA_DATA_MemPtr));

    params[1] = spa_pod_builder_add_object(&builder,
        SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
        SPA_PARAM_META_type, SPA_POD_Id(SPA_META_Header),
        SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_header)));

    pw_stream_update_params(capture->stream, params, 2);
    pw_thread_loop_signal(capture->loop, false);
}

static void on_process(void *data)
{
    struct capture *capture = data;

    struct pw_buffer *b = pw_stream_dequeue_buffer(capture->stream);
    if (b == NULL) {
        return;
    }

    struct spa_data *d = &b->buffer->datas[0];

    // Frames keep arriving while the stream is active. Only a grab in progress wants one.
    if (capture->want_frame && d->data != NULL) {
        uint32_t stride = d->chunk->stride;
        uint32_t height = capture->format.info.raw.size.height;
        size_t size = d->chunk->size != 0 ? d->chunk->size : (size_t)stride * height;

        if (size > 0 && size <= capture->destination_size) {
            memcpy(capture->destination, d->data, size);

            capture->width = capture->format.info.raw.size.width;
            capture->height = height;
            capture->stride = stride;
            capture->size = size;

            capture->want_frame = 0;
            capture->frame_ready = 1;
            pw_thread_loop_signal(capture->loop, false);
        }
    }

    pw_stream_queue_buffer(capture->stream, b);
}

static const struct pw_stream_events stream_events = {
    PW_VERSION_STREAM_EVENTS,
    .state_changed = on_state_changed,
    .param_changed = on_param_changed,
    .process = on_process,
};

/// Creates and connects the stream. Must be called with the loop locked.
static int connect_stream(struct capture *capture)
{
    capture->frame_ready = 0;
    capture->stream_error = 0;

    capture->stream = pw_stream_new(capture->core, "snapshotkit-capture",
        pw_properties_new(
            PW_KEY_MEDIA_TYPE, "Video",
            PW_KEY_MEDIA_CATEGORY, "Capture",
            PW_KEY_MEDIA_ROLE, "Screen",
            NULL));

    if (capture->stream == NULL) {
        return -1;
    }

    pw_stream_add_listener(capture->stream, &capture->stream_listener, &stream_events, capture);

    uint8_t builder_buffer[1024];
    struct spa_pod_builder builder = SPA_POD_BUILDER_INIT(builder_buffer, sizeof(builder_buffer));

    const struct spa_pod *params[1];
    params[0] = spa_pod_builder_add_object(&builder,
        SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
        SPA_FORMAT_mediaType,       SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype,    SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        // BGR variants only. The daemon and the overlay both interpret the frame as BGRx, and the
        // negotiated format is never reported back to them, so offering RGBx here would not add a
        // capability: it would add a compositor that silently swaps red and blue.
        SPA_FORMAT_VIDEO_format,    SPA_POD_CHOICE_ENUM_Id(3,
                                        SPA_VIDEO_FORMAT_BGRx,
                                        SPA_VIDEO_FORMAT_BGRx,
                                        SPA_VIDEO_FORMAT_BGRA),
        SPA_FORMAT_VIDEO_size,      SPA_POD_CHOICE_RANGE_Rectangle(
                                        &SPA_RECTANGLE(1920, 1080),
                                        &SPA_RECTANGLE(1, 1),
                                        &SPA_RECTANGLE(16384, 16384)),
        SPA_FORMAT_VIDEO_framerate, SPA_POD_CHOICE_RANGE_Fraction(
                                        &SPA_FRACTION(60, 1),
                                        &SPA_FRACTION(0, 1),
                                        &SPA_FRACTION(1000, 1)));

    return pw_stream_connect(capture->stream,
        PW_DIRECTION_INPUT,
        capture->node_id,
        PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS,
        params, 1);
}

static void disconnect_stream(struct capture *capture)
{
    if (capture->stream != NULL) {
        pw_stream_destroy(capture->stream);
        capture->stream = NULL;
    }
}

/// Waits for one frame. Must be called with the loop locked. Returns 0 on success.
static int await_frame(struct capture *capture)
{
    struct timespec deadline;
    pw_thread_loop_get_time(capture->loop, &deadline, (int64_t)GRAB_TIMEOUT_MS * SPA_NSEC_PER_MSEC);

    while (!capture->frame_ready && !capture->stream_error) {
        if (pw_thread_loop_timed_wait_full(capture->loop, &deadline) < 0) {
            break;
        }
    }

    return capture->frame_ready ? 0 : -1;
}

/// Connects a fresh stream, takes one frame, and disconnects again so an idle SnapShotKit costs the
/// compositor nothing.
static int grab(struct capture *capture)
{
    pw_thread_loop_lock(capture->loop);

    capture->frame_ready = 0;
    capture->want_frame = 1;

    int status = -1;
    if (connect_stream(capture) >= 0) {
        status = await_frame(capture);
    }

    disconnect_stream(capture);
    capture->want_frame = 0;

    pw_thread_loop_unlock(capture->loop);
    return status;
}

int main(int argc, char **argv)
{
    if (argc != 6) {
        fprintf(stderr, "usage: %s <pipewire-fd> <node-id> <frame-path> <width> <height>\n", argv[0]);
        return 2;
    }

    int fd = atoi(argv[1]);
    uint32_t node_id = (uint32_t)strtoul(argv[2], NULL, 10);
    const char *frame_path = argv[3];
    long width = strtol(argv[4], NULL, 10);
    long height = strtol(argv[5], NULL, 10);

    if (width <= 0 || height <= 0 || width > 32768 || height > 32768) {
        fprintf(stderr, "snapshotkit-capture: implausible frame size %ldx%ld\n", width, height);
        return 2;
    }

    // Report what we were actually handed, because "could not connect" is indistinguishable between
    // a descriptor that was never inherited and one PipeWire rejected.
    int descriptor_flags = fcntl(fd, F_GETFD);
    if (descriptor_flags < 0) {
        fprintf(stderr, "snapshotkit-capture: fd %d is not open in this process (%s)\n", fd, strerror(errno));
        return 1;
    }

    pw_init(NULL, NULL);

    struct capture capture = { .node_id = node_id };

    capture.loop = pw_thread_loop_new("snapshotkit-capture", NULL);
    if (capture.loop == NULL || pw_thread_loop_start(capture.loop) < 0) {
        fprintf(stderr, "snapshotkit-capture: could not start the loop\n");
        return 1;
    }

    pw_thread_loop_lock(capture.loop);
    capture.context = pw_context_new(pw_thread_loop_get_loop(capture.loop), NULL, 0);
    capture.core = capture.context != NULL ? pw_context_connect_fd(capture.context, fd, NULL, 0) : NULL;
    pw_thread_loop_unlock(capture.loop);

    if (capture.core == NULL) {
        fprintf(stderr, "snapshotkit-capture: could not connect to PipeWire on fd %d\n", fd);
        return 1;
    }

    // Size the shared frame file to the stream the daemon negotiated. It lives in XDG_RUNTIME_DIR,
    // which is tmpfs, so this is RAM rather than disk.
    size_t capacity = (size_t)width * (size_t)height * 4;

    int frame_fd = open(frame_path, O_RDWR | O_CREAT | O_TRUNC, 0600);
    if (frame_fd < 0 || ftruncate(frame_fd, (off_t)capacity) < 0) {
        fprintf(stderr, "snapshotkit-capture: could not create the frame file %s\n", frame_path);
        return 1;
    }

    capture.destination = mmap(NULL, capacity, PROT_READ | PROT_WRITE, MAP_SHARED, frame_fd, 0);
    if (capture.destination == MAP_FAILED) {
        fprintf(stderr, "snapshotkit-capture: could not map the frame file\n");
        return 1;
    }

    capture.destination_size = capacity;

    printf("ready\n");
    fflush(stdout);

    char line[64];
    while (fgets(line, sizeof(line), stdin) != NULL) {
        if (strncmp(line, "quit", 4) == 0) {
            break;
        }

        if (strncmp(line, "grab", 4) != 0) {
            printf("err unknown command\n");
            fflush(stdout);
            continue;
        }

        if (grab(&capture) == 0) {
            printf("ok %u %u %u %llu\n", capture.width, capture.height, capture.stride,
                (unsigned long long)capture.size);
        } else {
            printf("err no frame arrived within %d ms\n", GRAB_TIMEOUT_MS);
        }

        fflush(stdout);
    }

    munmap(capture.destination, capacity);
    close(frame_fd);

    pw_thread_loop_lock(capture.loop);
    disconnect_stream(&capture);
    pw_thread_loop_unlock(capture.loop);

    pw_thread_loop_stop(capture.loop);
    pw_context_destroy(capture.context);
    pw_thread_loop_destroy(capture.loop);

    return 0;
}
