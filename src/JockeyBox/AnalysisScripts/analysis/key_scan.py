"""Offline musical-key estimator for JockeyBox.

Sibling of ``bpm_scan.py``, built on the same stack (librosa + numpy, already in
the separation venv), so it adds no dependency.

Two entry points:

* ``estimate_key(y, sr)`` - importable, works on samples the caller already
  decoded. ``bpm_scan.py`` uses this so one track is decoded once for BPM and
  key together.
* ``python key_scan.py`` - same line protocol as ``bpm_scan.py``:

    Request  {"id": "<opaque>", "path": "C:\\Music\\a.mp3"}
    Response {"id": "<opaque>", "ok": true, "key": "8A", "keyName": "Am",
              "keyConfidence": 0.71, "keyReason": null, "error": null}

  When the track has no defensible tonal centre (noise, drums-only, a single
  tone), ``key``/``keyName`` are null and ``keyReason`` says why. A wrong key
  is worse than an empty cell for harmonic mixing, so the estimator refuses
  rather than guesses.

Notation: **Camelot** ("8A", "11B"). The library Key column exists for harmonic
mixing, and Camelot encodes mixing compatibility directly (same number, +-1).

Method: harmonic/percussive separation, then chroma_cens (CQT chroma with
octave folding and per-frame normalisation), median-aggregated over the window
and correlated against the Krumhansl-Kessler major/minor profiles for all 24
rotations. Guards: an active-pitch-class floor (a single tone is not a key), a
chroma-flatness ceiling (noise), a correlation floor and a best-vs-runner-up
margin, and agreement across three sub-windows. A key is only reported when all
of them pass.

Cost (measured, 40 s window, warm librosa): ~1.5-2 s per track, dominated by
harmonic separation. That is why this rides the existing opt-in/background BPM
lane instead of a scan-time pass.
"""

import json
import sys

import numpy as np

SR = 22050

# Analysis window. Key is stable across a track, so a bounded middle slice keeps
# the cost well below a full-track chroma transform.
WINDOW_SECONDS = 40.0
MIN_SECONDS = 8.0

# Guards, calibrated on real library tracks against generated noise/drum/sine.
MAX_CHROMA_FLATNESS = 0.99      # noise/drums sit at 1.000; music <= 0.98
MIN_ACTIVE_PITCH_CLASSES = 3    # a single tone (sine) yields 1
ACTIVE_PEAK_FRACTION = 0.35
MIN_BEST_CORRELATION = 0.55     # noise tops out ~0.40-0.57 with no margin
MIN_MARGIN = 0.06
BLOCKS = 3
MIN_AGREEING_BLOCKS = 2

PITCH_NAMES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]

# Krumhansl-Kessler key profiles (the standard major/minor templates).
MAJOR_PROFILE = np.array(
    [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88],
    dtype=np.float64,
)
MINOR_PROFILE = np.array(
    [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17],
    dtype=np.float64,
)

# Camelot number by pitch class, per mode. Either spelling of a pitch class
# (C# / Db) shares a number on the wheel.
CAMELOT_MAJOR = [8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1]   # C, C#, D, ...
CAMELOT_MINOR = [5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10]   # Am=8A, Em=9A, ...


def _none(reason: str) -> dict:
    return {
        "ok": True,
        "key": None,
        "keyName": None,
        "keyConfidence": 0.0,
        "keyReason": reason,
        "error": None,
    }


def _correlate(profile: np.ndarray, template: np.ndarray) -> float:
    a = profile - profile.mean()
    b = template - template.mean()
    denom = float(np.sqrt((a * a).sum() * (b * b).sum()))
    if denom <= 1e-12:
        return 0.0
    return float((a * b).sum() / denom)


def _normalise(profile: np.ndarray) -> np.ndarray:
    total = float(profile.sum())
    return profile / total if total > 1e-9 else np.zeros_like(profile)


def _best(profile: np.ndarray):
    """Return (camelot, name, correlation, margin) for a chroma profile."""
    scored = []
    for mode, template in (("major", MAJOR_PROFILE), ("minor", MINOR_PROFILE)):
        for root in range(12):
            scored.append((_correlate(profile, np.roll(template, root)), mode, root))
    scored.sort(key=lambda s: s[0], reverse=True)

    best, second = scored[0], scored[1]
    best_corr, mode, root = best
    number = (CAMELOT_MAJOR if mode == "major" else CAMELOT_MINOR)[root]
    letter = "B" if mode == "major" else "A"
    name = PITCH_NAMES[root] + ("" if mode == "major" else "m")
    return f"{number}{letter}", name, best_corr, best_corr - second[0]


def estimate_key(y: np.ndarray, sr: int) -> dict:
    """Estimate the key of decoded mono samples. Never raises for audio defects."""
    import librosa

    if y is None or y.size < sr * MIN_SECONDS:
        return _none("too short to judge")

    # A middle slice avoids intros/outros that are often drums-only or a pad.
    window = int(sr * WINDOW_SECONDS)
    if y.size > window:
        start = (y.size - window) // 2
        y = y[start:start + window]

    # Strip drums first: percussion smears every pitch class and is the main
    # reason a full mix's chroma looks flat.
    try:
        y = librosa.effects.harmonic(y)
    except Exception:
        pass  # fall back to the raw signal rather than failing the job

    chroma = librosa.feature.chroma_cens(y=librosa.util.normalize(y), sr=sr)
    if chroma.size == 0 or chroma.shape[1] < BLOCKS:
        return _none("no chroma")

    profile = _normalise(np.median(chroma, axis=1))
    if profile.sum() <= 1e-9:
        return _none("silence")

    peak = float(profile.max())
    if peak <= 0:
        return _none("silence")
    if int((profile >= peak * ACTIVE_PEAK_FRACTION).sum()) < MIN_ACTIVE_PITCH_CLASSES:
        return _none("no tonal centre (single tone or percussive)")

    flatness = float(np.exp(np.mean(np.log(profile + 1e-9))) / max(float(np.mean(profile)), 1e-9))
    if flatness > MAX_CHROMA_FLATNESS:
        return _none("no tonal centre (noise-like spectrum)")

    key, name, best_corr, margin = _best(profile)
    if best_corr < MIN_BEST_CORRELATION or margin < MIN_MARGIN:
        return _none(f"ambiguous (best {name} corr {best_corr:.2f} margin {margin:.2f})")

    # The same key must come out of the sub-windows, not just the average.
    frames = chroma.shape[1]
    agreeing = 0
    for i in range(BLOCKS):
        block = chroma[:, i * frames // BLOCKS:(i + 1) * frames // BLOCKS]
        if block.size and _best(_normalise(np.median(block, axis=1)))[0] == key:
            agreeing += 1
    if agreeing < MIN_AGREEING_BLOCKS:
        return _none(f"unstable across the track ({agreeing}/{BLOCKS} agree on {name})")

    confidence = float(np.clip(margin / 0.25, 0.0, 1.0)) * (agreeing / BLOCKS)
    return {
        "ok": True,
        "key": key,
        "keyName": name,
        "keyConfidence": round(confidence, 3),
        "keyReason": None,
        "error": None,
    }


def analyse(path: str) -> dict:
    import librosa

    y, sr = librosa.load(path, sr=SR, mono=True)
    if y.size == 0:
        raise ValueError("decoded zero samples")
    return estimate_key(y, sr)


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
