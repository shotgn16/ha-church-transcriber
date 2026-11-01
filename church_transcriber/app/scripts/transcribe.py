#!/usr/bin/env python3
"""Local transcription with faster-whisper. Prints PROGRESS lines and writes JSON output."""
import argparse
import json
import sys
import time


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

    model = WhisperModel(
        a.model,
        device="cpu",
        compute_type=a.compute,
        download_root=a.models_dir,
        cpu_threads=a.threads,
    )

    language = None if a.language in ("", "auto") else a.language
    print("STAGE transcribing", flush=True)
    segments, info = model.transcribe(
        a.audio,
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
