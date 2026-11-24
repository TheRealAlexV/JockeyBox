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


def align_lyrics(audio_path: str, lines_input: list[dict], model_name: str = "small.en") -> dict:
    """Align lyrics to the audio without modifying the lyric text.

    stable-ts's ``align()`` is deliberately not used here. On this stack it puts the first
    line of Sweater Weather at 3.04 s when the singer starts at 2.30 s, and that offset
    follows unrelated knobs instead of the audio: 0.32 s with silence suppression off,
    0.54 s under fast_mode with small.en, 3.14 s on a demucs vocal stem. Plain
    transcription of the same audio with the same model reports 2.32 s, so the line times
    are derived by matching the known lyric word sequence against the recognised words.
    """
    import difflib
    import re

    import stable_whisper
    import whisper

    def norm(word: str) -> str:
        return re.sub(r"[^a-z0-9']", "", word.lower())

    with tempfile.TemporaryDirectory() as temp_dir:
        # Try vocal isolation first
        effective_audio = extract_vocals_demucs(audio_path, temp_dir) or audio_path

        # Decode here rather than handing the path to the model: stable-ts passes it
        # straight to faster-whisper's decoder, which calls av.open(metadata_errors=...)
        # and dies on PyAV 19 with "unexpected keyword argument 'metadata_errors'".
        audio = whisper.load_audio(effective_audio)

        # Load faster-whisper backend via stable-ts
        # Compute type int8 for fast CPU/cross-platform execution
        model = stable_whisper.load_faster_whisper(model_name, compute_type="int8", device="cpu")

        # Ground truth lines: keep the caller's order and text; empty lines carry no timing
        entries = [(i, l.get("text", "").strip()) for i, l in enumerate(lines_input)]
        entries = [(i, text) for i, text in entries if text]
        if not entries:
            return {"status": "error", "error": "No non-empty lyric lines provided."}

        # Silence suppression is stable-ts's own default and is what moves a line start off
        # the true onset, so it stays off here.
        result = model.transcribe(audio, language="en", word_timestamps=True,
                                  suppress_silence=False)

        heard = []
        for segment in result.segments:
            for w in (segment.words or []):
                token = norm(w.word)
                if token:
                    heard.append((float(w.start), float(w.end), token))

        # Flatten the lyric lines into one word sequence, remembering which line owns each word
        lyric, owner = [], []
        for pos, (_, text) in enumerate(entries):
            for word in text.split():
                token = norm(word)
                if token:
                    lyric.append((token, word))
                    owner.append(pos)

        starts = [None] * len(entries)
        times = [[] for _ in entries]
        if lyric and heard:
            matcher = difflib.SequenceMatcher(None, [w[0] for w in lyric],
                                              [h[2] for h in heard], autojunk=False)
            for a, b, size in matcher.get_matching_blocks():
                for k in range(size):
                    pos = owner[a + k]
                    start, end, _ = heard[b + k]
                    if starts[pos] is None:
                        starts[pos] = start
                    times[pos].append((start, end, lyric[a + k][1]))

        aligned_lines = []
        previous_start = -1.0
        for pos, (orig_index, text) in enumerate(entries):
            start = starts[pos]
            # One matched word is not evidence of where a line begins: a stopword like
            # "we" appears throughout the song and once anchored a line 4.7 s late. A line
            # needs a real run of words, unless the line itself is a single word.
            if start is not None and len(times[pos]) < 2 and len(text.split()) > 1:
                start = None
            # Lines are sung in order, so a mapped start behind the previous one is noise.
            if start is not None and start < previous_start:
                start = None
            if start is None:
                # A line the model never heard keeps the caller's own timestamp rather
                # than inventing one from its neighbours.
                fallback = lines_input[orig_index].get("time_ms")
                if fallback is None:
                    continue
                start = float(fallback) / 1000.0

            words_list = []
            for start_s, end_s, word_text in times[pos]:
                w_start_ms = int(round(start_s * 1000))
                w_end_ms = max(int(round(end_s * 1000)), w_start_ms)
                words_list.append({
                    "start_ms": w_start_ms,
                    "end_ms": w_end_ms,
                    "text": word_text
                })

            aligned_lines.append({
                "time_ms": int(round(start * 1000)),
                "text": text,
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
