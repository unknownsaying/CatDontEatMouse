#!/usr/bin/env python3
"""
Daily Life Recorder & Uploader for Bilibili Account "猫不吃仓鼠" (MID: 3546649584470894)
Records video+audio from webcam/mic, saves as MP4, and publishes to Bilibili.
"""

import os
import sys
import time
import datetime
import threading
import queue
import subprocess
import json
from dotenv import load_dotenv

import cv2
import sounddevice as sd
import numpy as np
import wave
import ffmpeg

from bilibili_api import Credential, video_uploader, sync

# Load environment variables
load_dotenv()

SESSDATA = os.getenv("BILIBILI_SESSDATA")
CSRF = os.getenv("BILIBILI_CSRF")
MID = int(os.getenv("BILIBILI_MID", "3546649584470894"))

if not SESSDATA or not CSRF:
    print("❌ Please set BILIBILI_SESSDATA and BILIBILI_CSRF in your .env file.")
    sys.exit(1)

# Constants
VIDEO_FPS = 30
AUDIO_SAMPLERATE = 44100
AUDIO_CHANNELS = 1
RECORDING_DURATION = 60  # seconds – change as needed
OUTPUT_DIR = "vlogs"
os.makedirs(OUTPUT_DIR, exist_ok=True)

# ============================================================
# 1. RECORD VIDEO (separate thread) + AUDIO (main thread)
# ============================================================
class VideoRecorder:
    def __init__(self, filename, fps=30):
        self.filename = filename
        self.fps = fps
        self.cap = cv2.VideoCapture(0)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
        self.cap.set(cv2.CAP_PROP_FPS, fps)
        self.running = False
        self.frames = []

    def start(self):
        self.running = True
        self.thread = threading.Thread(target=self._record)
        self.thread.start()

    def _record(self):
        while self.running:
            ret, frame = self.cap.read()
            if ret:
                self.frames.append(frame)
            else:
                break
            time.sleep(0.001)  # small delay to avoid maxing CPU

    def stop(self):
        self.running = False
        self.thread.join()
        self.cap.release()
        # Write frames to temporary AVI (no audio yet)
        height, width, _ = self.frames[0].shape
        fourcc = cv2.VideoWriter_fourcc(*'XVID')
        out = cv2.VideoWriter(self.filename + "_temp.avi", fourcc, self.fps, (width, height))
        for frame in self.frames:
            out.write(frame)
        out.release()
        print(f"✅ Video saved: {self.filename}_temp.avi")

# ============================================================
# 2. RECORD AUDIO
# ============================================================
class AudioRecorder:
    def __init__(self, filename, samplerate=44100, channels=1):
        self.filename = filename
        self.samplerate = samplerate
        self.channels = channels
        self.recorded_data = []

    def callback(self, indata, frames, time_info, status):
        self.recorded_data.append(indata.copy())

    def start(self):
        self.stream = sd.InputStream(
            samplerate=self.samplerate,
            channels=self.channels,
            callback=self.callback,
            dtype='float32'
        )
        self.stream.start()

    def stop(self):
        self.stream.stop()
        self.stream.close()
        # Convert to int16 for WAV
        audio_data = np.concatenate(self.recorded_data, axis=0)
        audio_int16 = (audio_data * 32767).astype(np.int16)
        with wave.open(self.filename + ".wav", 'wb') as wf:
            wf.setnchannels(self.channels)
            wf.setsampwidth(2)  # 16-bit
            wf.setframerate(self.samplerate)
            wf.writeframes(audio_int16.tobytes())
        print(f"🎤 Audio saved: {self.filename}.wav")

# ============================================================
# 3. COMBINE VIDEO + AUDIO WITH FFMPEG
# ============================================================
def combine_audio_video(video_file, audio_file, output_mp4):
    try:
        input_video = ffmpeg.input(video_file)
        input_audio = ffmpeg.input(audio_file)
        (
            ffmpeg.output(
                input_video,
                input_audio,
                output_mp4,
                vcodec='libx264',
                acodec='aac',
                strict='experimental'
            )
            .overwrite_output()
            .run(quiet=True)
        )
        print(f"🎬 Final video created: {output_mp4}")
        # Clean up temporary files
        os.remove(video_file)
        os.remove(audio_file)
        return True
    except ffmpeg.Error as e:
        print(f"❌ FFmpeg error: {e}")
        return False

# ============================================================
# 4. UPLOAD TO BILIBILI
# ============================================================
def upload_to_bilibili(video_path, title, desc, tags):
    credential = Credential(sessdata=SESSDATA, bili_jct=CSRF)
    uploader = video_uploader.VideoUploader(credential)

    # Prepare upload task
    task = uploader.upload(
        video_path=video_path,
        title=title,
        desc=desc,
        tags=tags,
        source="",
        cover_path="",          # auto generate
        no_reprint=1,           # 1 = allow reprint, 0 = forbid
    )
    print("🚀 Uploading to Bilibili...")
    result = sync(task)         # sync() turns async into synchronous
    if result["code"] == 0:
        bvid = result["data"]["bvid"]
        print(f"✅ Upload successful! Video link: https://www.bilibili.com/video/{bvid}")
        return bvid
    else:
        print(f"❌ Upload failed: {result}")
        return None

# ============================================================
# MAIN ROUTINE
# ============================================================
def main():
    print(f"📹 Daily Life Vlog - {datetime.date.today()}")
    print(f"Account: 猫不吃仓鼠 (MID: {MID})")
    print(f"Recording for {RECORDING_DURATION} seconds...")

    # Prepare filenames
    timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    base_name = f"{OUTPUT_DIR}/vlog_{timestamp}"
    video_temp = f"{base_name}_temp.avi"
    audio_temp = f"{base_name}.wav"
    final_video = f"{base_name}.mp4"

    # Start recorders
    video_rec = VideoRecorder(video_temp, fps=VIDEO_FPS)
    audio_rec = AudioRecorder(audio_temp, samplerate=AUDIO_SAMPLERATE, channels=AUDIO_CHANNELS)

    video_rec.start()
    audio_rec.start()

    # Progress indicator
    for remaining in range(RECORDING_DURATION, 0, -1):
        sys.stdout.write(f"\r⏳ {remaining}s remaining...")
        sys.stdout.flush()
        time.sleep(1)

    print("\n🛑 Stopping recorders...")
    video_rec.stop()
    audio_rec.stop()

    # Combine and upload
    if combine_audio_video(video_temp, audio_temp, final_video):
        title = f"Daily Life – {datetime.date.today()}"
        desc = f"Life recording by 猫不吃仓鼠. #daily #vlog #life"
        tags = ["日常", "vlog", "生活记录", "猫不吃仓鼠"]

        upload_to_bilibili(final_video, title, desc, tags)
    else:
        print("❌ Failed to combine video, upload aborted.")

if __name__ == "__main__":
    main()