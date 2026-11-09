/**
 * Key artwork, generated as SVG data URIs.
 *
 * Stream Deck accepts an SVG string for `setImage`, which means no asset
 * pipeline: one function per state, cached, no files to ship. The static PNGs in
 * `imgs/` are only used for the manifest (plugin list, category, default key).
 */

export type KeyMode = "playing" | "idle" | "empty" | "error";

const FULL = 72;
const ACCENT = "#5AB0FF";
const ACCENT_SOFT = "#2C6FA8";
const IDLE = "#8A94A6";
const IDLE_SOFT = "#4A5361";
const DANGER = "#FF6B5E";

const cache = new Map<KeyMode, string>();

function bars(color: string, heights: number[]): string {
  const width = 4.8;
  const gap = 9.2;
  const left = FULL / 2 - (gap * (heights.length - 1)) / 2;
  return heights
    .map((height, index) => {
      const x = left + index * gap - width / 2;
      const y = 30 - height / 2;
      return `<rect x="${x.toFixed(2)}" y="${y.toFixed(2)}" width="${width}" height="${height}" rx="${(width / 2).toFixed(2)}" fill="${color}"/>`;
    })
    .join("");
}

function tile(): string {
  return '<rect x="1.5" y="1.5" width="69" height="69" rx="13" fill="#14171D" stroke="#262D38" stroke-width="1.5"/>';
}

/** A hint of a label area at the bottom, so the title reads as part of the key. */
function labelRule(color: string): string {
  return `<rect x="26" y="55" width="20" height="2.4" rx="1.2" fill="${color}"/>`;
}

function build(mode: KeyMode): string {
  let body: string;
  switch (mode) {
    case "playing":
      body =
        '<rect x="10" y="8" width="52" height="46" rx="12" fill="#5AB0FF" opacity="0.10"/>' +
        bars(ACCENT, [14, 24, 33, 23, 13]) +
        `<circle cx="58" cy="14" r="3.4" fill="${ACCENT}"/>` +
        labelRule(ACCENT_SOFT);
      break;
    case "idle":
      body = bars(IDLE, [10, 17, 23, 16, 10]) + labelRule(IDLE_SOFT);
      break;
    case "empty":
      body =
        '<rect x="14" y="14" width="44" height="44" rx="11" fill="none" stroke="#3B4452" stroke-width="1.8" stroke-dasharray="5 4"/>' +
        '<rect x="33" y="24" width="6" height="24" rx="3" fill="#5C6675"/>' +
        '<rect x="24" y="33" width="24" height="6" rx="3" fill="#5C6675"/>';
      break;
    case "error":
      body =
        `<circle cx="36" cy="27" r="14" fill="none" stroke="${DANGER}" stroke-width="2"/>` +
        `<rect x="33.8" y="19" width="4.4" height="12" rx="2.2" fill="${DANGER}"/>` +
        `<circle cx="36" cy="35.4" r="2.4" fill="${DANGER}"/>` +
        `<rect x="22" y="47" width="28" height="2.4" rx="1.2" fill="${DANGER}" opacity="0.55"/>`;
      break;
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${FULL}" height="${FULL}" viewBox="0 0 ${FULL} ${FULL}">${tile()}${body}</svg>`;
}

/** SVG data URI for a key state; cached because `setImage` gets called per state change. */
export function keyImage(mode: KeyMode): string {
  const cached = cache.get(mode);
  if (cached) return cached;
  const svg = build(mode);
  const uri = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
  cache.set(mode, uri);
  return uri;
}

/** Keep a long pad name readable on a 72 px key. */
export function shortTitle(name: string, max = 24): string {
  const trimmed = name.trim();
  if (trimmed.length <= max) return trimmed;
  return `${trimmed.slice(0, max - 1).trimEnd()}…`;
}
