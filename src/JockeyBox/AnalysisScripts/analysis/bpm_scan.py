"""Offline BPM / beat-grid analyser for JockeyBox.

Runs inside the app's own Python venv (the one built for audio-separator, which
already depends on librosa). Reads one JSON job per line on stdin and writes one
JSON result per line on stdout, so a whole library can be analysed by one warm
process instead of paying NumPy/librosa start-up per track.

Request  (one per line):  {"id": "<opaque>", "path": "C:\\Music\\a.mp3"}
Response (one per line):  {"id": "<opaque>", "ok": true, "bpm": 128.0,
                           "firstBeatMs": 412, "beatsMs": [412, 880, ...],
                           "bpmP10": 126.4, "bpmP90": 129.1,
                           "confidence": 0.83, "durationMs": 214000,
                           "halfDoubleSuspect": false, "error": null}

Protocol notes:
  * Every response line is flushed immediately; the caller reads line by line.
  * A failed job returns ok=false with `error` and never kills the process.
  * `beatsMs` is capped at MAX_BEATS to keep a long DJ set from producing a
    megabyte of JSON; `bpm`/`firstBeatMs` are unaffected.
"""

import json
import sys

import numpy as np

SR = 22050
MAX_BEATS = 4000
START_BPM = 128.0


def _key_or_reason(estimator, y, sr) -> dict:
    """Run the key estimator, turning a crash into a reason.

    A key stage that throws must not cost the tempo: carrying the failure as
    ``keyReason`` keeps the grid (the BPM is already measured) and lets the log
    say "key stage failed: RuntimeError: ..." instead of the reasonless "no key"
    that a below-threshold margin also produces.
    """
    try:
        return estimator(y, sr)
    except Exception as exc:  # noqa: BLE001 - reported, never swallowed
        return {
            "key": None,
            "keyName": None,
            "keyConfidence": 0.0,
            "keyReason": f"key stage failed: {type(exc).__name__}: {exc}",
        }


def analyse(path: str) -> dict:
    import librosa
    from key_scan import estimate_key

    y, sr = librosa.load(path, sr=SR, mono=True)
    if y.size == 0:
        raise ValueError("decoded zero samples")

    duration_ms = int(round(len(y) / sr * 1000.0))
    onset = librosa.onset.onset_strength(y=y, sr=sr)
    tempo, beats = librosa.beat.beat_track(
        onset_envelope=onset,
        sr=sr,
        start_bpm=START_BPM,
        tightness=100,
        trim=True,
        units="time",
    )
    bpm = float(np.atleast_1d(tempo)[0])

    # Per-frame tempo distribution: the 70-vs-140 (octave error) decision.
    per_frame = librosa.feature.tempo(
        onset_envelope=onset, sr=sr, start_bpm=START_BPM, aggregate=None, max_tempo=200
    )
    p10 = float(np.percentile(per_frame, 10))
    p90 = float(np.percentile(per_frame, 90))

    beats_ms = [int(round(float(b) * 1000.0)) for b in beats[:MAX_BEATS]]

    # Confidence = how tightly the per-frame estimate agrees with the global one.
    spread = max(1e-6, p90 - p10)
    confidence = float(max(0.0, min(1.0, 1.0 - spread / max(1.0, bpm))))

    suspect = bool(bpm < 90.0 and bpm * 2.0 <= 200.0) or bool(bpm > 170.0 and bpm / 2.0 >= 60.0)

    # Key rides the same decode. estimate_key() has its own guards and returns
    # key=None when the track has no defensible tonal centre; a crash in that
    # stage comes back as a reason instead, so the tempo still lands.
    key = _key_or_reason(estimate_key, y, sr)

    return {
        "ok": True,
        "bpm": round(bpm, 3),
        "firstBeatMs": beats_ms[0] if beats_ms else None,
        "beatsMs": beats_ms,
        "bpmP10": round(p10, 2),
        "bpmP90": round(p90, 2),
        "confidence": round(confidence, 3),
        "durationMs": duration_ms,
        "halfDoubleSuspect": suspect,
        "key": key.get("key"),
        "keyName": key.get("keyName"),
        "keyConfidence": key.get("keyConfidence", 0.0),
        "keyReason": key.get("keyReason"),
        "error": None,
    }


def main() -> int:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        job = {}
        try:
            job = json.loads(line)
            result = analyse(job["path"])
        except Exception as exc:  # one bad file must not kill the batch
            result = {"ok": False, "error": f"{type(exc).__name__}: {exc}"}
        result["id"] = job.get("id")
        sys.stdout.write(json.dumps(result, separators=(",", ":")) + "\n")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
