/*
 * Air Studio – All-in-One C Module
 *    Air Camera   : V4L2 video capture (saves JPEG frames)
 *    Air Motion   : simulated accelerometer data (prints to stdout)
 *    Air Micro Phone: ALSA audio capture (saves WAV file)
 *
 * Compile (Linux):
 *    gcc -o air_studio air_studio.c -ljpeg -lasound -lpthread -lm
 *
 * Usage:
 *    ./air_studio [duration_sec] [output_dir]
 */

#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <linux/videodev2.h>
#include <jpeglib.h>
#include <alsa/asoundlib.h>
#include <pthread.h>
#include <time.h>
#include <math.h>
#include <signal.h>

// ---------- Configuration ----------
#define VIDEO_DEVICE    "/dev/video0"
#define AUDIO_DEVICE    "default"      // ALSA device name
#define FRAME_WIDTH     640
#define FRAME_HEIGHT    480
#define AUDIO_RATE      44100
#define AUDIO_CHANNELS  1
#define WAV_BITS        16
#define MOTION_INTERVAL 100000         // microseconds (0.1 sec)

static volatile int keep_running = 1;

// ---------- Utility: save JPEG from MJPEG buffer ----------
void save_jpeg(const char *filename, unsigned char *data, size_t len) {
    FILE *f = fopen(filename, "wb");
    if (f) {
        fwrite(data, 1, len, f);
        fclose(f);
    }
}

// ---------- WAV file writer ----------
typedef struct {
    FILE *fp;
    size_t data_size;
} WavWriter;

WavWriter wav_open(const char *filename, int rate, int channels, int bits) {
    WavWriter w = {NULL, 0};
    w.fp = fopen(filename, "wb");
    if (!w.fp) return w;
    // Write placeholder header
    unsigned char header[44] = {0};
    fwrite(header, 1, 44, w.fp);
    return w;
}

void wav_write(WavWriter *w, const void *data, size_t bytes) {
    if (w->fp) {
        fwrite(data, 1, bytes, w->fp);
        w->data_size += bytes;
    }
}

void wav_close(WavWriter *w) {
    if (!w->fp) return;
    size_t file_size = 44 + w->data_size;
    rewind(w->fp);
    // RIFF header
    fwrite("RIFF", 1, 4, w->fp);
    uint32_t size = file_size - 8;
    fwrite(&size, 4, 1, w->fp);
    fwrite("WAVE", 1, 4, w->fp);
    // fmt chunk
    fwrite("fmt ", 1, 4, w->fp);
    uint32_t fmt_size = 16;
    fwrite(&fmt_size, 4, 1, w->fp);
    uint16_t audio_format = 1; // PCM
    fwrite(&audio_format, 2, 1, w->fp);
    uint16_t ch = AUDIO_CHANNELS;
    fwrite(&ch, 2, 1, w->fp);
    uint32_t sample_rate = AUDIO_RATE;
    fwrite(&sample_rate, 4, 1, w->fp);
    uint32_t byte_rate = AUDIO_RATE * AUDIO_CHANNELS * (WAV_BITS/8);
    fwrite(&byte_rate, 4, 1, w->fp);
    uint16_t block_align = AUDIO_CHANNELS * (WAV_BITS/8);
    fwrite(&block_align, 2, 1, w->fp);
    uint16_t bits = WAV_BITS;
    fwrite(&bits, 2, 1, w->fp);
    // data chunk
    fwrite("data", 1, 4, w->fp);
    fwrite(&w->data_size, 4, 1, w->fp);
    fclose(w->fp);
}

// ---------- Air Camera thread (V4L2) ----------
void *air_camera_thread(void *arg) {
    const char *out_dir = (const char *)arg;
    int fd = open(VIDEO_DEVICE, O_RDWR);
    if (fd < 0) {
        perror("Air Camera: Cannot open device");
        return NULL;
    }

    struct v4l2_format fmt = {0};
    fmt.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    fmt.fmt.pix.width = FRAME_WIDTH;
    fmt.fmt.pix.height = FRAME_HEIGHT;
    fmt.fmt.pix.pixelformat = V4L2_PIX_FMT_MJPEG;
    fmt.fmt.pix.field = V4L2_FIELD_ANY;
    if (ioctl(fd, VIDIOC_S_FMT, &fmt) < 0) {
        perror("Air Camera: Set format failed (MJPEG)");
        close(fd);
        return NULL;
    }

    struct v4l2_requestbuffers req = {0};
    req.count = 4;
    req.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    req.memory = V4L2_MEMORY_MMAP;
    if (ioctl(fd, VIDIOC_REQBUFS, &req) < 0) {
        perror("Air Camera: Request buffers failed");
        close(fd);
        return NULL;
    }

    struct buffer {
        void *start;
        size_t length;
    } *buffers = calloc(req.count, sizeof(*buffers));
    for (int i = 0; i < req.count; i++) {
        struct v4l2_buffer buf = {0};
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        buf.index = i;
        ioctl(fd, VIDIOC_QUERYBUF, &buf);
        buffers[i].length = buf.length;
        buffers[i].start = mmap(NULL, buf.length, PROT_READ | PROT_WRITE,
                                MAP_SHARED, fd, buf.m.offset);
    }

    for (int i = 0; i < req.count; i++) {
        struct v4l2_buffer buf = {0};
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        buf.index = i;
        ioctl(fd, VIDIOC_QBUF, &buf);
    }

    enum v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    ioctl(fd, VIDIOC_STREAMON, &type);

    printf("[Air Camera] Started\n");
    int frame_count = 0;
    while (keep_running) {
        struct v4l2_buffer buf = {0};
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        if (ioctl(fd, VIDIOC_DQBUF, &buf) < 0) break;

        char fname[256];
        snprintf(fname, sizeof(fname), "%s/frame_%04d.jpg", out_dir, frame_count++);
        save_jpeg(fname, buffers[buf.index].start, buf.bytesused);
        ioctl(fd, VIDIOC_QBUF, &buf);
    }

    ioctl(fd, VIDIOC_STREAMOFF, &type);
    for (int i = 0; i < req.count; i++)
        munmap(buffers[i].start, buffers[i].length);
    free(buffers);
    close(fd);
    printf("[Air Camera] Stopped (%d frames)\n", frame_count);
    return NULL;
}

// ---------- Air Motion thread (simulated accelerometer) ----------
void *air_motion_thread(void *arg) {
    (void)arg;
    printf("[Air Motion] Started\n");
    srand(time(NULL));
    struct timespec ts = {0, MOTION_INTERVAL * 1000};
    while (keep_running) {
        double x = 0.2 * sin(time(NULL) * 0.8) + ((rand() % 100) / 100.0 - 0.5) * 0.5;
        double y = 0.3 * cos(time(NULL) * 1.1) + ((rand() % 100) / 100.0 - 0.5) * 0.5;
        double z = 9.81 + 0.15 * sin(time(NULL) * 1.3) + ((rand() % 100) / 100.0 - 0.5) * 0.3;
        printf("[Air Motion] X: %.2f  Y: %.2f  Z: %.2f\n", x, y, z);
        nanosleep(&ts, NULL);
    }
    printf("[Air Motion] Stopped\n");
    return NULL;
}

// ---------- Air Micro Phone thread (ALSA) ----------
void *air_mic_thread(void *arg) {
    const char *out_dir = (const char *)arg;
    char wav_path[512];
    snprintf(wav_path, sizeof(wav_path), "%s/audio.wav", out_dir);

    snd_pcm_t *handle;
    if (snd_pcm_open(&handle, AUDIO_DEVICE, SND_PCM_STREAM_CAPTURE, 0) < 0) {
        fprintf(stderr, "Air Mic: Cannot open audio device\n");
        return NULL;
    }

    snd_pcm_hw_params_t *params;
    snd_pcm_hw_params_alloca(&params);
    snd_pcm_hw_params_any(handle, params);
    snd_pcm_hw_params_set_access(handle, params, SND_PCM_ACCESS_RW_INTERLEAVED);
    snd_pcm_hw_params_set_format(handle, params, SND_PCM_FORMAT_S16_LE);
    snd_pcm_hw_params_set_channels(handle, params, AUDIO_CHANNELS);
    unsigned int rate = AUDIO_RATE;
    snd_pcm_hw_params_set_rate_near(handle, params, &rate, 0);
    snd_pcm_hw_params(handle, params);

    WavWriter wav = wav_open(wav_path, AUDIO_RATE, AUDIO_CHANNELS, WAV_BITS);
    if (!wav.fp) {
        fprintf(stderr, "Air Mic: Cannot create WAV file\n");
        snd_pcm_close(handle);
        return NULL;
    }

    printf("[Air Mic] Recording to %s\n", wav_path);
    const int frames_per_period = 512;
    short buffer[frames_per_period * AUDIO_CHANNELS];

    while (keep_running) {
        int rc = snd_pcm_readi(handle, buffer, frames_per_period);
        if (rc == -EPIPE) {
            snd_pcm_prepare(handle);
        } else if (rc < 0) {
            break;
        } else if (rc > 0) {
            wav_write(&wav, buffer, rc * AUDIO_CHANNELS * sizeof(short));
        }
    }

    wav_close(&wav);
    snd_pcm_drain(handle);
    snd_pcm_close(handle);
    printf("[Air Mic] Stopped\n");
    return NULL;
}

// ---------- Signal handler ----------
void sig_handler(int sig) {
    keep_running = 0;
}

int main(int argc, char **argv) {
    int duration = 10;          // seconds
    const char *out_dir = "output";

    if (argc >= 2) duration = atoi(argv[1]);
    if (argc >= 3) out_dir = argv[2];

    mkdir(out_dir, 0755);

    signal(SIGINT, sig_handler);
    signal(SIGTERM, sig_handler);

    pthread_t cam_thread, motion_thread, mic_thread;

    printf("=== Air Studio (C) ===\n");
    printf("Duration: %d sec, Output: %s/\n", duration, out_dir);

    pthread_create(&cam_thread, NULL, air_camera_thread, (void*)out_dir);
    pthread_create(&motion_thread, NULL, air_motion_thread, NULL);
    pthread_create(&mic_thread, NULL, air_mic_thread, (void*)out_dir);

    // Let them run for the specified duration (unless interrupted)
    for (int i = 0; i < duration && keep_running; i++) {
        sleep(1);
    }
    keep_running = 0;

    pthread_join(cam_thread, NULL);
    pthread_join(motion_thread, NULL);
    pthread_join(mic_thread, NULL);

    printf("All modules stopped. Files saved in '%s/'\n", out_dir);
    return 0;
}