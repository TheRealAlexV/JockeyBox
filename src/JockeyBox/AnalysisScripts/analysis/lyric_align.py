#!/usr/bin/env python3
"""Offline lyric alignment sidecar for JockeyBox using stable-ts and Demucs (vocal isolation).

Contract:
    CLI: python lyric_align.py <audio_path> <lyrics_input_json> <output_json>

Input JSON format:
    {
        "lines": [
            { "text": "I figure it's out of your hands", "time_ms": 12150 },
            ...
        ]
    }
    or simply an array of strings / array of line objects.

Output JSON format:
    {
        "status": "ok",
        "lines": [
            {
                "time_ms": 12150,
                "text": "I figure it's out of your hands",
                "words": [
                    { "start_ms": 12150, "end_ms": 12300, "text": "I" },
                    { "start_ms": 12310, "end_ms": 12520, "text": "figure" }
                ]
            }
        ]
    }
    Or on error:
    {
        "status": "error",
        "error": "Error description"
    }
"""

import json
import os
import sys
import tempfile
import traceback


def extract_vocals_demucs(audio_path: str, temp_dir: str) -> str | None:
    """Attempt vocal isolation using demucs if installed."""
    try:
        import torch
        from demucs.apply import apply_model
        from demucs.pretrained import get_model
        import torchaudio

        # Check if torch and demucs are usable
        device = "cuda" if torch.cuda.is_available() else "cpu"
        model = get_model("htdemucs")
        model.to(device)
        model.eval()

        wav, sr = torchaudio.load(audio_path)
        if wav.shape[0] == 1:
            wav = wav.repeat(2, 1)  # Stereo required
        if sr != model.samplerate:
            wav = torchaudio.functional.resample(wav, sr, model.samplerate)

        ref = wav.mean(0)
        wav_norm = (wav - ref.mean()) / (ref.std() + 1e-8)
        wav_in = wav_norm[None].to(device)

        with torch.no_grad():
            sources = apply_model(model, wav_in, device=device, split=True, overlap=0.25)

        # Sources: drums, bass, other, vocals (index 3 in htdemucs)
        vocals_idx = model.sources.index("vocals") if "vocals" in model.sources else 3
        vocals = sources[0, vocals_idx].cpu() * (ref.std() + 1e-8) + ref.mean()

        vocal_path = os.path.join(temp_dir, "vocals.wav")
        torchaudio.save(vocal_path, vocals, model.samplerate)
        return vocal_path
    except Exception as exc:
        sys.stderr.write(f"Demucs isolation skipped / failed ({exc}), falling back to direct audio.\n")
        return None


def align_lyrics(audio_path: str, lines_input: list[dict], model_name: str = "base") -> dict:
    """Align lyrics using stable-ts without modifying lyric text."""
    import stable_whisper

    with tempfile.TemporaryDirectory() as temp_dir:
        # Try vocal isolation first
        effective_audio = extract_vocals_demucs(audio_path, temp_dir) or audio_path

        # Load faster-whisper backend via stable-ts
        # Compute type int8 for fast CPU/cross-platform execution
        model = stable_whisper.load_faster_whisper(model_name, compute_type="int8", device="cpu")

        # Prepare input lines / text
        # Ground truth lines: preserve original text and approximate timing if provided
        plain_lines = [l.get("text", "").strip() for l in lines_input if l.get("text", "").strip()]
        if not plain_lines:
            return {"status": "error", "error": "No non-empty lyric lines provided."}

        full_lyrics_text = "\n".join(plain_lines)

        # Run stable_whisper alignment
        result = model.align(effective_audio, full_lyrics_text, language="en", original_split=True)

        aligned_lines = []
        for segment in result.segments:
            seg_start_ms = int(round(segment.start * 1000))
            seg_text = segment.text.strip()
            words_list = []
            if hasattr(segment, "words") and segment.words:
                for w in segment.words:
                    w_start_ms = int(round(w.start * 1000))
                    w_end_ms = int(round(w.end * 1000))
                    w_text = w.word.strip()
                    if w_text:
                        words_list.append({
                            "start_ms": w_start_ms,
                            "end_ms": max(w_end_ms, w_start_ms),
                            "text": w_text
                        })

            aligned_lines.append({
                "time_ms": seg_start_ms,
                "text": seg_text,
                "words": words_list
            })

        return {
            "status": "ok",
            "lines": aligned_lines
        }


def main():
    if len(sys.argv) < 4:
        print("Usage: python lyric_align.py <audio_path> <lyrics_input_json> <output_json>", file=sys.stderr)
        sys.exit(1)

    audio_path = sys.argv[1]
    input_json_path = sys.argv[2]
    output_json_path = sys.argv[3]

    if not os.path.exists(audio_path):
        err_res = {"status": "error", "error": f"Audio file not found: {audio_path}"}
        with open(output_json_path, "w", encoding="utf-8") as f:
            json.dump(err_res, f, indent=2)
        sys.exit(1)

    try:
        # utf-8-sig strips the BOM the C# writer's Encoding.UTF8 emits, and is a
        # no-op for input without one, so this side stays robust to whoever writes it.
        with open(input_json_path, "r", encoding="utf-8-sig") as f:
            raw_input = json.load(f)

        if isinstance(raw_input, list):
            lines_data = [{"text": x} if isinstance(x, str) else x for x in raw_input]
        elif isinstance(raw_input, dict) and "lines" in raw_input:
            lines_data = raw_input["lines"]
        else:
            lines_data = []

        result = align_lyrics(audio_path, lines_data)
        with open(output_json_path, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=2)

        if result.get("status") != "ok":
            sys.exit(1)

    except Exception as exc:
        traceback.print_exc(file=sys.stderr)
        err_res = {"status": "error", "error": str(exc)}
        with open(output_json_path, "w", encoding="utf-8") as f:
            json.dump(err_res, f, indent=2)
        sys.exit(1)


if __name__ == "__main__":
    main()
