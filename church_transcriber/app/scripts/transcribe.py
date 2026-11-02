#!/usr/bin/env python3
"""Local transcription with faster-whisper. Prints PROGRESS lines and writes JSON output."""
import argparse
import json
import os
import socket
import subprocess
import sys
import time

import numpy as np

SAMPLE_RATE = 16000

# Only look up IPv4 addresses: avoids a common Home Assistant DNS problem where IPv6 (AAAA) lookups fail.
_getaddrinfo = socket.getaddrinfo


def _ipv4_getaddrinfo(host, port, family=0, *args, **kwargs):
    return _getaddrinfo(host, port, socket.AF_INET if family in (0, socket.AF_UNSPEC) else family, *args, **kwargs)


socket.getaddrinfo = _ipv4_getaddrinfo


def load_audio(path: str) -> np.ndarray:
    """Decode to 16 kHz mono float32 with ffmpeg.

    faster-whisper's own decoder relies on PyAV, whose newer releases changed its API
    (e.g. "open() got an unexpected keyword argument 'metadata_errors'"). Decoding here
    keeps transcription working whatever PyAV version is installed.
    """
    cmd = [os.environ.get("FFMPEG", "ffmpeg"), "-nostdin", "-hide_banner", "-loglevel", "error",
           "-i", path, "-vn", "-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "s16le", "-"]
    result = subprocess.run(cmd, capture_output=True, check=False)
    if result.returncode != 0 or not result.stdout:
        raise RuntimeError("ffmpeg could not decode the audio: " + result.stderr.decode(errors="replace")[-800:])
    pcm = np.frombuffer(result.stdout, dtype=np.int16)
    del result
    return pcm.astype(np.float32) / 32768.0


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--audio", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--model", default="small")
    p.add_argument("--compute", default="int8")
    p.add_argument("--language", default="auto")
    p.add_argument("--prompt", default="")
    p.add_argument("--models-dir", default="/data/models")
    p.add_argument("--threads", type=int, default=0)
    p.add_argument("--beam", type=int, default=5)
    a = p.parse_args()

    print("STAGE loading model " + a.model, flush=True)
    from faster_whisper import WhisperModel

    def load(local_only: bool):
        return WhisperModel(a.model, device="cpu", compute_type=a.compute, download_root=a.models_dir,
                            cpu_threads=a.threads, local_files_only=local_only)

    # Use the already-downloaded model if there is one (no network needed); otherwise download it,
    # retrying a few times if the network hiccups.
    try:
        model = load(True)
    except Exception:  # noqa: BLE001 - not downloaded yet
        print("STAGE downloading model " + a.model, flush=True)
        for attempt in range(1, 5):
            try:
                model = load(False)
                break
            except Exception as e:  # noqa: BLE001
                if attempt == 4:
                    raise
                print(f"STAGE model download failed ({e}); retrying in {15 * attempt}s", file=sys.stderr, flush=True)
                time.sleep(15 * attempt)

    language = None if a.language in ("", "auto") else a.language
    print("STAGE decoding audio", flush=True)
    audio = load_audio(a.audio)
    print("STAGE transcribing", flush=True)
    segments, info = model.transcribe(
        audio,
        language=language,
        initial_prompt=a.prompt or None,
        beam_size=a.beam,
        vad_filter=True,
        vad_parameters={"min_silence_duration_ms": 800},
        # Prevents repetition loops during long services with music.
        condition_on_previous_text=False,
    )
    duration = max(float(info.duration or 0), 1.0)
    print(f"INFO language={info.language} duration={duration:.1f}", flush=True)

    out = []
    last = 0.0
    for s in segments:
        text = (s.text or "").strip()
        if text:
            out.append({"start": round(s.start, 2), "end": round(s.end, 2), "text": text})
        now = time.time()
        if now - last > 2:
            print(f"PROGRESS {min(s.end / duration, 1.0):.4f}", flush=True)
            last = now

    with open(a.out, "w", encoding="utf-8") as f:
        json.dump({"language": info.language, "duration": duration, "segments": out}, f, ensure_ascii=False)
    print("PROGRESS 1.0", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
