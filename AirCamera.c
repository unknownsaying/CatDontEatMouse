/*
 * Air Camera - C (Linux, V4L2)
 * Captures 10 frames from /dev/video0 and saves as JPEG (requires jpeglib)
 * Compile: gcc -o aircam aircam.c -ljpeg
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <linux/videodev2.h>
#include <jpeglib.h>

#define WIDTH  640
#define HEIGHT 480
#define DEVICE "/dev/video0"

// Structure for a single buffer
struct buffer {
    void   *start;
    size_t  length;
};

int main() {
    int fd = open(DEVICE, O_RDWR);
    if (fd == -1) {
        perror("Cannot open video device");
        return 1;
    }

    // Query capabilities
    struct v4l2_capability cap;
    ioctl(fd, VIDIOC_QUERYCAP, &cap);
    printf("Camera: %s\n", cap.card);

    // Set format: Motion‑JPEG
    struct v4l2_format fmt;
    memset(&fmt, 0, sizeof(fmt));
    fmt.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    fmt.fmt.pix.width = WIDTH;
    fmt.fmt.pix.height = HEIGHT;
    fmt.fmt.pix.pixelformat = V4L2_PIX_FMT_MJPEG;
    fmt.fmt.pix.field = V4L2_FIELD_ANY;
    ioctl(fd, VIDIOC_S_FMT, &fmt);

    // Request 4 buffers (memory mapping)
    struct v4l2_requestbuffers req;
    memset(&req, 0, sizeof(req));
    req.count = 4;
    req.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    req.memory = V4L2_MEMORY_MMAP;
    ioctl(fd, VIDIOC_REQBUFS, &req);

    struct buffer *buffers = calloc(req.count, sizeof(*buffers));
    for (int i = 0; i < req.count; i++) {
        struct v4l2_buffer buf;
        memset(&buf, 0, sizeof(buf));
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        buf.index = i;
        ioctl(fd, VIDIOC_QUERYBUF, &buf);
        buffers[i].length = buf.length;
        buffers[i].start = mmap(NULL, buf.length, PROT_READ | PROT_WRITE,
                                MAP_SHARED, fd, buf.m.offset);
    }

    // Queue buffers and start capture
    for (int i = 0; i < req.count; i++) {
        struct v4l2_buffer buf;
        memset(&buf, 0, sizeof(buf));
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        buf.index = i;
        ioctl(fd, VIDIOC_QBUF, &buf);
    }
    enum v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    ioctl(fd, VIDIOC_STREAMON, &type);

    printf("Air Camera started – capturing 10 frames\n");
    for (int frame = 0; frame < 10; frame++) {
        struct v4l2_buffer buf;
        memset(&buf, 0, sizeof(buf));
        buf.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = V4L2_MEMORY_MMAP;
        ioctl(fd, VIDIOC_DQBUF, &buf);  // wait for a frame

        // Save the JPEG (already MJPEG from camera)
        char filename[64];
        snprintf(filename, sizeof(filename), "frame_%03d.jpg", frame);
        FILE *out = fopen(filename, "wb");
        fwrite(buffers[buf.index].start, buf.bytesused, 1, out);
        fclose(out);
        printf("Saved %s (%u bytes)\n", filename, buf.bytesused);

        ioctl(fd, VIDIOC_QBUF, &buf);   // re‑queue buffer
    }

    // Cleanup
    ioctl(fd, VIDIOC_STREAMOFF, &type);
    for (int i = 0; i < req.count; i++)
        munmap(buffers[i].start, buffers[i].length);
    free(buffers);
    close(fd);
    return 0;
}