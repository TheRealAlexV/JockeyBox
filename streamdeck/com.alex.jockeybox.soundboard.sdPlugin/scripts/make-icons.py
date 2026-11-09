#!/usr/bin/env python3
"""Generate the Stream Deck plugin images (PNG) with no third-party deps.

Everything is drawn with signed-distance-field shapes and rendered with
analytic anti-aliasing, so the same drawing code scales from 20px action
icons up to the 512px marketplace icon.

Usage:  python3 scripts/make-icons.py
Output: imgs/*.png  (plus @2x variants)
"""
from __future__ import annotations

import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(HERE), "imgs")

# ---------------------------------------------------------------- primitives


def clamp(v, lo=0.0, hi=1.0):
    return lo if v < lo else hi if v > hi else v


def sd_round_rect(x, y, cx, cy, hw, hh, r):
    """Signed distance to a rounded rectangle (negative = inside)."""
    qx = abs(x - cx) - (hw - r)
    qy = abs(y - cy) - (hh - r)
    ax, ay = max(qx, 0.0), max(qy, 0.0)
    return math.hypot(ax, ay) + min(max(qx, qy), 0.0) - r


def sd_circle(x, y, cx, cy, r):
    return math.hypot(x - cx, y - cy) - r


def coverage(sd, aa=1.0):
    return clamp(0.5 - sd / aa)


def over(dst, src, a):
    """src-over compositing of a straight-alpha colour into dst (both RGBA 0..1)."""
    if a <= 0:
        return dst
    sa = a * src[3]
    da = dst[3]
    out_a = sa + da * (1.0 - sa)
    if out_a <= 0:
        return [0.0, 0.0, 0.0, 0.0]
    out = [(src[i] * sa + dst[i] * da * (1.0 - sa)) / out_a for i in range(3)]
    return [out[0], out[1], out[2], out_a]


# ------------------------------------------------------------------- drawing

WHITE = (0.94, 0.95, 0.97, 1.0)
ACCENT = (0.35, 0.70, 1.00, 1.0)
BASE = (0.078, 0.086, 0.106, 1.0)
RIM = (0.20, 0.23, 0.28, 1.0)


def waveform_bars(n=5):
    """Relative (cx, half-width, half-height) for a symmetric waveform."""
    heights = [0.20, 0.34, 0.46, 0.30, 0.18][:n]
    spans = 0.62
    gap = spans / max(n - 1, 1)
    left = 0.5 - spans / 2.0
    hw = min(0.055, gap * 0.30)
    return [(left + i * gap, hw, heights[i]) for i in range(n)]


def draw_plugin(size, pad_bg=True):
    bars = waveform_bars(5)
    px = bytearray()
    for y in range(size):
        row = bytearray()
        for x in range(size):
            fx = (x + 0.5) / size
            fy = (y + 0.5) / size
            aa = 1.2 / size
            c = [0.0, 0.0, 0.0, 0.0]
            if pad_bg:
                c = over(c, BASE, coverage(sd_round_rect(fx, fy, 0.5, 0.5, 0.455, 0.455, 0.16), aa))
                c = over(c, RIM, coverage(abs(sd_round_rect(fx, fy, 0.5, 0.5, 0.455, 0.455, 0.16)) - 0.006, aa))
            for i, (cx, hw, hh) in enumerate(bars):
                col = ACCENT if i == 2 else WHITE
                c = over(c, col, coverage(sd_round_rect(fx, fy, cx, 0.5, hw, hh, hw), aa))
            row += bytes(int(clamp(v) * 255 + 0.5) for v in c)
        px += row
    return size, size, bytes(px)


def draw_category(size):
    # Bars are chunky at 28px so the icon stays legible in the category list.
    bars = waveform_bars(3)
    px = bytearray()
    for y in range(size):
        row = bytearray()
        for x in range(size):
            fx = (x + 0.5) / size
            fy = (y + 0.5) / size
            aa = 1.2 / size
            c = [0.0, 0.0, 0.0, 0.0]
            for i, (cx, hw, hh) in enumerate(bars):
                col = ACCENT if i == 1 else WHITE
                c = over(c, col, coverage(sd_round_rect(fx, fy, cx, 0.5, hw * 1.5, hh * 1.1, hw * 1.2), aa))
            row += bytes(int(clamp(v) * 255 + 0.5) for v in c)
        px += row
    return size, size, bytes(px)


def draw_key(size, panic=False):
    """Default key image (72x72 / 144x144). Transparent so the deck shows black."""
    px = bytearray()
    bars = waveform_bars(5)
    for y in range(size):
        row = bytearray()
        for x in range(size):
            fx = (x + 0.5) / size
            fy = (y + 0.5) / size
            aa = 1.2 / size
            c = [0.0, 0.0, 0.0, 0.0]
            if panic:
                c = over(c, WHITE, coverage(abs(sd_circle(fx, fy, 0.5, 0.5, 0.30)) - 0.030, aa))
                c = over(c, WHITE, coverage(sd_round_rect(fx, fy, 0.5, 0.5, 0.115, 0.115, 0.028), aa))
            else:
                for i, (cx, hw, hh) in enumerate(bars):
                    col = ACCENT if i == 2 else WHITE
                    c = over(c, col, coverage(sd_round_rect(fx, fy, cx, 0.5, hw * 1.35, hh * 1.25, hw * 1.35), aa))
            row += bytes(int(clamp(v) * 255 + 0.5) for v in c)
        px += row
    return size, size, bytes(px)


# ------------------------------------------------------------------ png saver


def write_png(path, w, h, rgba):
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)  # filter: none
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as fh:
        fh.write(png)
    print(f"  {os.path.relpath(path, os.path.dirname(HERE))}  {w}x{h}  {len(png)} bytes")


def main():
    os.makedirs(OUT, exist_ok=True)
    print("writing icons to imgs/")
    for name, fn, sizes in [
        ("plugin-icon", lambda s: draw_plugin(s, pad_bg=True), (256, 512)),
        ("category-icon", draw_category, (28, 56)),
        ("pad-icon", lambda s: draw_plugin(s, pad_bg=True), (20, 40)),
        ("panic-icon", lambda s: draw_key(s, panic=True), (20, 40)),
        ("pad-key", lambda s: draw_key(s), (72, 144)),
        ("panic-key", lambda s: draw_key(s, panic=True), (72, 144)),
    ]:
        for i, size in enumerate(sizes):
            suffix = "" if i == 0 else "@2x"
            write_png(os.path.join(OUT, f"{name}{suffix}.png"), *fn(size))


if __name__ == "__main__":
    main()
