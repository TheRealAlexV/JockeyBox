#!/usr/bin/env python3
"""Merge the JockeyBox karaoke library (MusicK) into the music library (Music).

JockeyBox keeps one library tree: the karaoke version of a track sits beside it
as "Song.karaoke.mp3". This script moves every file from the old karaoke root
into the music root at the same relative path with the ".karaoke" marker
inserted before the extension, then repoints the JSON stores that remember
absolute paths or filenames.

Run it from WSL. It translates C:\\... to /mnt/c/... for its own file work and
writes Windows paths back into the JSON files the Windows app reads.

  # what would happen (default: nothing is touched, nothing is written)
  python3 scripts/merge-karaoke-library.py

  # do it (backs up the stores first, then moves, then rewrites)
  python3 scripts/merge-karaoke-library.py --apply

  # check the result (or check before starting: "files left in MusicK")
  python3 scripts/merge-karaoke-library.py --verify

Rules the script keeps:

  * Never deletes a file and never touches audio contents. Folders left empty
    in the karaoke root are left alone. System junk that Windows scatters
    (Thumbs.db, desktop.ini, dotfiles) stays where it is and is skipped.
  * Never overwrites: a target that already exists gets " (2)", " (3)", ...
    before the ".karaoke" marker, and each collision is reported.
  * Moves, then verifies existence and size; every verified move is appended to
    %APPDATA%\\JockeyBox\\merge-karaoke.log, which makes a partial run
    resumable. A re-run skips what the log already records.
  * Backs up the JSON stores to "<name>.pre-merge.bak" before rewriting them,
    and never overwrites an existing backup.
  * Every rewrite is idempotent: a second --apply changes nothing.

Standard library only; Python 3.8+.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import re
import shutil
import sys
from pathlib import Path

# %APPDATA%\JockeyBox on the machine this was written for; override with
# --app-dir if the Windows profile lives somewhere else.
DEFAULT_APP_DIR = Path("/mnt/c/Users/Alex/AppData/Roaming/JockeyBox")

KARAOKE_MARKER = ".karaoke"
LOG_NAME = "merge-karaoke.log"
BACKUP_SUFFIX = ".pre-merge.bak"

# The extensions the app treats as audio (Library/LibraryScanner). A
# ".karaoke.<audio ext>" file is a karaoke partner, not a music track.
AUDIO_EXTENSIONS = {
    ".mp3", ".m4a", ".flac", ".wav", ".aac", ".ogg", ".opus", ".wma",
    ".aiff", ".aif", ".alac",
}

# JSON stores relative to the app dir. ledger.json lives in downloads\.
STORE_NAMES = (
    "settings.json",
    "downloads/ledger.json",
    "playlists.json",
    "playback-queue.json",
    "library.json",
)

MAX_COLLISION_SUFFIX = 999
SAMPLE_ROWS = 20  # example rows printed per report section


# ---------------------------------------------------------------- path mapping
def to_wsl(path: str) -> str:
    """C:\\Users\\Alex\\Music -> /mnt/c/Users/Alex/Music. Anything else unchanged."""
    m = re.match(r"^([A-Za-z]):[\\/](.*)$", path or "")
    if not m:
        return (path or "").replace("\\", "/")
    return "/mnt/%s/%s" % (m.group(1).lower(), m.group(2).replace("\\", "/"))


def to_win(path: str) -> str:
    """/mnt/c/Users/Alex/Music -> C:\\Users\\Alex\\Music. Anything else unchanged."""
    m = re.match(r"^/mnt/([A-Za-z])(?:/(.*))?$", path or "")
    if not m:
        return path or ""
    rest = (m.group(2) or "").replace("/", "\\")
    return "%s:\\%s" % (m.group(1).upper(), rest)


def norm_win(path: str) -> str:
    """Comparison form of a Windows path: backslashes, no trailing separator, lower case.

    A WSL path is translated first, so "/mnt/c/Users/Alex/Music" and
    "C:\\Users\\Alex\\Music" compare equal.
    """
    text = (path or "").strip()
    if text.startswith("/"):
        text = to_win(text)
    return text.replace("/", "\\").rstrip("\\").lower()


def is_audio(name: str) -> bool:
    return os.path.splitext(name)[1].lower() in AUDIO_EXTENSIONS


# Explorer/WSL litter that is never music: keep it where it is rather than
# dragging "Thumbs.karaoke.db" into the library.
SYSTEM_FILE_NAMES = {"thumbs.db", "desktop.ini", ".ds_store", "ehthumbs.db"}


def is_system_file(name: str) -> bool:
    return name.startswith(".") or name.lower() in SYSTEM_FILE_NAMES


def has_marker(name: str) -> bool:
    """True when the file name carries the karaoke marker before its extension."""
    stem, _ = os.path.splitext(name)
    return stem.lower().endswith(KARAOKE_MARKER)


def karaoke_name(name: str) -> str:
    """Song.mp3 -> Song.karaoke.mp3. A name that already has the marker is kept."""
    if has_marker(name):
        return name
    stem, ext = os.path.splitext(name)
    return stem + KARAOKE_MARKER + ext


def with_collision_suffix(name: str, n: int) -> str:
    """Song.karaoke.mp3 -> Song (2).karaoke.mp3 (the marker stays last)."""
    stem, ext = os.path.splitext(name)
    base = stem[: -len(KARAOKE_MARKER)] if has_marker(name) else stem
    return "%s (%d)%s%s" % (base, n, KARAOKE_MARKER, ext)


def base_of_karaoke_name(name: str) -> str:
    """Song.karaoke.mp3 -> Song.mp3 (the file it is the karaoke partner of)."""
    stem, ext = os.path.splitext(name)
    if stem.lower().endswith(KARAOKE_MARKER):
        stem = stem[: -len(KARAOKE_MARKER)]
    return stem + ext


def strip_marker_key(key: str) -> str:
    """'a/b/Song.karaoke' or 'a/b/Song.karaoke.mp3' -> 'a/b/Song'.

    Library keys and LocalKey are extension-stripped, so a trailing audio
    extension goes together with the marker. Keys without a marker are returned
    unchanged - including 'Song.mp3', whose extension is not ours to drop.
    """
    slash = max(key.rfind("\\"), key.rfind("/"))
    dot = key.rfind(".")
    base = key[:dot] if dot > slash + 1 and key[dot:].lower() in AUDIO_EXTENSIONS else key

    if base.lower().endswith(KARAOKE_MARKER):
        return base[: -len(KARAOKE_MARKER)]
    return key


def inside(child: Path, parent: Path) -> bool:
    try:
        child.relative_to(parent)
        return True
    except ValueError:
        return False


def human_bytes(n: int) -> str:
    value = float(n)
    for unit in ("B", "KiB", "MiB", "GiB", "TiB"):
        if unit == "B" and n < 1024:
            return "%d B" % n
        if value < 1024 or unit == "TiB":
            return "%.1f %s" % (value, unit)
        value /= 1024
    return "%d B" % n


# ---------------------------------------------------------------------- output
def say(line: str = "") -> None:
    print(line, flush=True)


def head(title: str) -> None:
    say("")
    say(title)
    say("-" * len(title))


# ------------------------------------------------------------------------ JSON
def load_json(path: Path):
    """(data, exists). A missing file is (None, False); a corrupt one is (None, True)."""
    if not path.is_file():
        return None, False
    try:
        with path.open("r", encoding="utf-8-sig") as fh:
            return json.load(fh), True
    except (OSError, ValueError) as exc:
        say("  ! %s is unreadable (%s); left alone" % (path, exc))
        return None, True


def save_json(path: Path, data) -> None:
    """Write atomically: temp file in the same folder, then replace."""
    tmp = path.with_name(path.name + ".merge-tmp")
    with tmp.open("w", encoding="utf-8", newline="\n") as fh:
        json.dump(data, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    os.replace(str(tmp), str(path))


def backup_stores(app_dir: Path):
    """Copy each store to <name>.pre-merge.bak. Returns (created, kept)."""
    created, kept = [], []
    for rel in STORE_NAMES:
        src = app_dir / rel
        if not src.is_file():
            continue
        dst = src.with_name(src.name + BACKUP_SUFFIX)
        if dst.exists():
            kept.append(str(dst))
            continue
        shutil.copy2(str(src), str(dst))
        created.append(str(dst))
    return created, kept


# ------------------------------------------------------------------ the moves
class Move:
    __slots__ = ("src", "dst", "requested", "size", "reason")

    def __init__(self, src: Path, requested: Path, dst: Path, size: int,
                 collision: str | None = None):
        self.src = src
        self.requested = requested
        self.dst = dst
        self.size = size
        # None: plain move. Otherwise why the name was suffixed.
        self.reason = collision

    @property
    def collision(self) -> bool:
        return self.reason is not None


def read_log(log_path: Path) -> dict:
    """Verified moves from earlier runs, keyed by normalised source path."""
    done = {}
    if not log_path.is_file():
        return done
    try:
        with log_path.open("r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                parts = line.rstrip("\n").split("\t")
                if len(parts) >= 4 and parts[1] == "MOVE":
                    done[norm_win(parts[2])] = parts[3]
    except OSError as exc:
        say("  ! could not read %s (%s); assuming nothing was moved yet" % (log_path, exc))
    return done


def append_log(log_path: Path, kind: str, *fields: str) -> None:
    stamp = _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    log_path.parent.mkdir(parents=True, exist_ok=True)
    with log_path.open("a", encoding="utf-8", newline="\n") as fh:
        fh.write("\t".join((stamp, kind) + fields) + "\n")


def build_plan(music_root: Path, karaoke_root: Path, done: dict):
    """(plan, already_moved, skipped, ignored)."""
    plan, already, skipped, ignored = [], [], [], []

    # Targets this run has already handed to another file. Windows is
    # case-insensitive, and two source names can fold onto one target ("Song.mp3"
    # and "song.MP3" from a case-sensitive copy), so a claimed target counts as
    # taken exactly like an existing file.
    claimed = set()

    for dirpath, dirnames, filenames in os.walk(str(karaoke_root)):
        dirnames.sort()
        for name in sorted(filenames):
            src = Path(dirpath) / name
            rel = src.relative_to(karaoke_root)
            if is_system_file(name):
                ignored.append(src)
                continue
            if norm_win(to_win(str(src))) in done:
                already.append(src)
                continue

            target_name = karaoke_name(name)
            requested = music_root / rel.parent / target_name

            try:
                size = src.stat().st_size
            except OSError as exc:
                # A crash between move and log write leaves the file at the
                # target; treat that as done instead of reporting a failure.
                if norm_win(to_win(str(requested))) in done or requested.exists():
                    already.append(src)
                else:
                    skipped.append("%s (%s)" % (src, exc))
                continue

            dst = requested
            reason = None
            if requested.exists():
                reason = "target already exists"
            elif norm_win(to_win(str(requested))) in claimed:
                reason = "another file in this run maps to the same target"
            if reason:
                free = None
                for n in range(2, MAX_COLLISION_SUFFIX + 1):
                    candidate = requested.with_name(with_collision_suffix(target_name, n))
                    if not candidate.exists() and norm_win(to_win(str(candidate))) not in claimed:
                        free = candidate
                        break
                if free is None:
                    skipped.append("%s (no free target name up to %d)"
                                   % (src, MAX_COLLISION_SUFFIX))
                    continue
                dst = free

            claimed.add(norm_win(to_win(str(dst))))
            plan.append(Move(src, requested, dst, size, reason))

    return plan, already, skipped, ignored


def perform(moves, log_path: Path):
    """Move each file, verify it, log it. Returns (moved, failures)."""
    moved, failures = [], []
    for mv in moves:
        try:
            mv.dst.parent.mkdir(parents=True, exist_ok=True)
            size_before = mv.src.stat().st_size
            shutil.move(str(mv.src), str(mv.dst))
            if not mv.dst.exists():
                raise OSError("target missing after move")
            size_after = mv.dst.stat().st_size
            if size_after != size_before:
                raise OSError("size changed (%d -> %d bytes)" % (size_before, size_after))
            if mv.src.exists():
                raise OSError("source still exists after move")
            append_log(log_path, "MOVE", to_win(str(mv.src)), to_win(str(mv.dst)))
            moved.append(mv)
        except OSError as exc:
            failures.append("%s -> %s: %s" % (mv.src, mv.dst, exc))
            say("  ! failed: %s -> %s (%s)" % (mv.src, mv.dst, exc))
    return moved, failures


def move_map(moved, done: dict) -> dict:
    """old Windows path -> new Windows path, from this run and previous ones."""
    mapping = dict(done)
    for mv in moved:
        mapping[norm_win(to_win(str(mv.src)))] = to_win(str(mv.dst))
    return mapping


# ------------------------------------------------------------------- rewrites
def rewrite_ledger(path: Path, mapping: dict) -> int:
    data, exists = load_json(path)
    if not exists or not isinstance(data, list):
        return 0
    changed = 0
    for entry in data:
        if not isinstance(entry, dict):
            continue
        old = entry.get("Path")
        if isinstance(old, str) and old:
            new = mapping.get(norm_win(old))
            if new and new != old:
                entry["Path"] = new
                changed += 1
    if changed:
        save_json(path, data)
    return changed


def rewrite_playlists(path: Path, mapping: dict):
    """LibraryPath follows the move; LocalKey loses a stale karaoke marker."""
    data, exists = load_json(path)
    if not exists or not isinstance(data, list):
        return 0, 0
    paths_changed = keys_changed = 0

    for playlist in data:
        if not isinstance(playlist, dict):
            continue
        tracks = playlist.get("Tracks")
        if not isinstance(tracks, list):
            continue
        for track in tracks:
            if not isinstance(track, dict):
                continue
            old = track.get("LibraryPath")
            if isinstance(old, str) and old:
                new = mapping.get(norm_win(old))
                if new and new != old:
                    track["LibraryPath"] = new
                    paths_changed += 1
            key = track.get("LocalKey")
            if isinstance(key, str) and key:
                stripped = strip_marker_key(key)
                if stripped != key:
                    track["LocalKey"] = stripped
                    keys_changed += 1

    if paths_changed or keys_changed:
        save_json(path, data)
    return paths_changed, keys_changed


def rewrite_queue(path: Path) -> int:
    """Queue keys are library keys (already the base key); strip a stale marker."""
    data, exists = load_json(path)
    if not exists or not isinstance(data, list):
        return 0
    changed = 0
    for record in data:
        if not isinstance(record, dict):
            continue
        kind = record.get("kind", record.get("Kind"))
        if isinstance(kind, str) and kind.lower() != "library":
            continue
        field = "key" if "key" in record else "Key"
        key = record.get(field)
        if isinstance(key, str) and key:
            stripped = strip_marker_key(key)
            if stripped != key:
                record[field] = stripped
                changed += 1
    if changed:
        save_json(path, data)
    return changed


def rewrite_tag_cache(path: Path, mapping: dict) -> int:
    """library.json is {absolute path: cached tags}; re-key the moved entries."""
    data, exists = load_json(path)
    if not exists or not isinstance(data, dict):
        return 0
    changed = 0
    for old in [k for k in data if isinstance(k, str)]:
        new = mapping.get(norm_win(old))
        if not new or new == old:
            continue
        # A row already keyed at the new path wins: it is the same file. The
        # comparison is case-insensitive because Windows paths are.
        already_keyed = any(k != old and isinstance(k, str) and norm_win(k) == norm_win(new)
                            for k in data)
        if not already_keyed:
            data[new] = data[old]
        del data[old]
        changed += 1
    if changed:
        save_json(path, data)
    return changed


def rewrite_settings(path: Path, music_win: str, merge_root: bool):
    """KaraokeDirectory becomes the music root; per-track map keys lose the marker.

    merge_root=False is used after a partial run: the old root stays configured so
    the next --apply can finish the remaining files instead of the script deciding
    there is nothing left to merge.
    """
    data, exists = load_json(path)
    if not exists or not isinstance(data, dict):
        return False, 0

    merged = merge_root and data.get("KaraokeDirectory") != music_win
    if merged:
        data["KaraokeDirectory"] = music_win

    keys_changed = 0
    for name in ("LyricsOffsetByTrack", "LyricsSourceByTrack"):
        table = data.get(name)
        if not isinstance(table, dict):
            continue
        for key in [k for k in table if isinstance(k, str)]:
            stripped = strip_marker_key(key)
            if stripped == key:
                continue
            # An existing base-key entry wins; only re-key into a free slot.
            if stripped not in table:
                table[stripped] = table[key]
            del table[key]
            keys_changed += 1

    if merged or keys_changed:
        save_json(path, data)
    return merged, keys_changed


def rewrite_stores(app_dir: Path, mapping: dict, music_win: str, merge_root: bool) -> None:
    head("Rewriting stores")

    n = rewrite_ledger(app_dir / "downloads/ledger.json", mapping)
    say("  ledger.json          %d Path entr%s" % (n, "y" if n == 1 else "ies"))

    p, k = rewrite_playlists(app_dir / "playlists.json", mapping)
    say("  playlists.json       %d LibraryPath, %d LocalKey"
        % (p, k))

    n = rewrite_queue(app_dir / "playback-queue.json")
    say("  playback-queue.json  %d key%s" % (n, "" if n == 1 else "s"))

    n = rewrite_tag_cache(app_dir / "library.json", mapping)
    say("  library.json         %d tag-cache key%s" % (n, "" if n == 1 else "s"))

    merged, k = rewrite_settings(app_dir / "settings.json", music_win, merge_root)
    say("  settings.json        KaraokeDirectory -> music root (%s), %d per-track key%s"
        % ("changed" if merged else ("kept: old root still has files" if not merge_root else "already"),
           k, "" if k == 1 else "s"))


# -------------------------------------------------------------------- verify
def scan_root(root: Path) -> list:
    found = []
    if not root.is_dir():
        return found
    for dirpath, dirnames, filenames in os.walk(str(root)):
        dirnames.sort()
        for name in sorted(filenames):
            found.append(Path(dirpath) / name)
    return found


def target_present(music_root: Path, rel: Path, name: str) -> bool:
    """True when the music root already holds this file, plain or suffixed."""
    target = karaoke_name(name)
    stem, ext = os.path.splitext(target)
    folder = music_root / rel.parent
    if not folder.is_dir():
        return False
    pattern = re.compile(r"^%s \(\d+\)$" % re.escape(stem.lower()))
    for entry in os.listdir(str(folder)):
        entry_stem, entry_ext = os.path.splitext(entry)
        if entry_ext.lower() != ext.lower():
            continue
        if entry_stem.lower() == stem.lower() or pattern.match(entry_stem.lower()):
            return True
    return False


def references_old_root(app_dir: Path, karaoke_win: str) -> list:
    """Store values that still point into the old karaoke root (Windows paths)."""
    needle = norm_win(karaoke_win)
    prefix = needle + "\\"
    hits = []

    def walk(value, where):
        if isinstance(value, str):
            if value and norm_win(value).startswith(prefix):
                hits.append("%s: %s" % (where, value))
        elif isinstance(value, list):
            for i, item in enumerate(value):
                walk(item, "%s[%d]" % (where, i))
        elif isinstance(value, dict):
            for key, item in value.items():
                if isinstance(key, str) and norm_win(key).startswith(prefix):
                    hits.append("%s key: %s" % (where, key))
                walk(item, "%s.%s" % (where, key))

    for rel in STORE_NAMES:
        data, exists = load_json(app_dir / rel)
        if exists and data is not None:
            walk(data, rel)
    return hits


def verify(music_root: Path, karaoke_root, app_dir: Path, music_win: str,
           karaoke_win: str) -> int:
    """Return 0 when clean, 1 when a problem was reported."""
    problems = 0

    head("Verify: files still in the karaoke root")
    if karaoke_root is None:
        say("  skipped: no pre-merge karaoke root known")
    else:
        left = [p for p in scan_root(karaoke_root) if not is_system_file(p.name)]
        if left:
            problems += 1
            say("  %d file(s) still under %s" % (len(left), to_win(str(karaoke_root))))
            for path in left[:SAMPLE_ROWS]:
                rel = path.relative_to(karaoke_root)
                say("    %s  (%s)" % (to_win(str(path)),
                                     "already in the music library"
                                     if target_present(music_root, rel, path.name)
                                     else "target missing"))
            if len(left) > SAMPLE_ROWS:
                say("    ... %d more" % (len(left) - SAMPLE_ROWS))
        else:
            say("  none")

    music_files = scan_root(music_root)
    audio_files = [p for p in music_files if is_audio(p.name)]
    karaoke_files = [p for p in audio_files if has_marker(p.name)]

    orphans = [p for p in karaoke_files
               if not p.with_name(base_of_karaoke_name(p.name)).exists()]
    head("Verify: karaoke files whose base audio is missing")
    if orphans:
        # Report only: a karaoke-only track is a valid library row, not a fault.
        say("  %d karaoke-only file(s)" % len(orphans))
        for path in orphans[:SAMPLE_ROWS]:
            say("    %s" % to_win(str(path)))
        if len(orphans) > SAMPLE_ROWS:
            say("    ... %d more" % (len(orphans) - SAMPLE_ROWS))
    else:
        say("  none")

    paired = {str(p.with_name(base_of_karaoke_name(p.name))).lower() for p in karaoke_files}
    unpaired = [p for p in audio_files
                if not has_marker(p.name) and str(p).lower() not in paired]
    head("Verify: originals with no karaoke partner (informational)")
    say("  %d of %d audio file(s)" % (len(unpaired), len(audio_files)))
    for path in unpaired[:5]:
        say("    %s" % to_win(str(path)))
    if len(unpaired) > 5:
        say("    ... %d more" % (len(unpaired) - 5))

    head("Verify: store references to the old karaoke root")
    if karaoke_root is None:
        say("  skipped: no pre-merge karaoke root known")
    else:
        hits = references_old_root(app_dir, karaoke_win)
        if hits:
            problems += 1
            say("  %d reference(s) still name %s" % (len(hits), karaoke_win))
            for hit in hits[:SAMPLE_ROWS]:
                say("    %s" % hit)
            if len(hits) > SAMPLE_ROWS:
                say("    ... %d more" % (len(hits) - SAMPLE_ROWS))
        else:
            say("  none")

    head("Verify: settings")
    data, _ = load_json(app_dir / "settings.json")
    current_karaoke = data.get("KaraokeDirectory") if isinstance(data, dict) else "(unreadable)"
    say("  MusicDirectory    %s" % music_win)
    say("  KaraokeDirectory  %s" % current_karaoke)

    if problems:
        head("Result: problems found")
        return 1
    head("Result: clean")
    return 0


# ---------------------------------------------------------------------- main
def resolve_roots(settings_path: Path):
    """(music_win, karaoke_win, merged) or None with a message already printed."""
    data, exists = load_json(settings_path)
    if not exists:
        say("No settings file at %s" % settings_path)
        return None
    if not isinstance(data, dict):
        say("Settings file %s is not a JSON object" % settings_path)
        return None

    music_win = (data.get("MusicDirectory") or "").strip()
    karaoke_win = (data.get("KaraokeDirectory") or "").strip()
    if not music_win:
        say("MusicDirectory is empty in %s; set the music folder in JockeyBox first."
            % settings_path)
        return None
    if not karaoke_win:
        say("KaraokeDirectory is empty in %s; there is nothing to merge." % settings_path)
        return None
    return music_win, karaoke_win, norm_win(karaoke_win) == norm_win(music_win)


def old_karaoke_root(app_dir: Path):
    """KaraokeDirectory from the pre-merge settings backup, if there is one."""
    data, _ = load_json(app_dir / ("settings.json" + BACKUP_SUFFIX))
    if isinstance(data, dict):
        value = (data.get("KaraokeDirectory") or "").strip()
        if value:
            return value
    return None


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Merge the JockeyBox karaoke library into the music library.",
        epilog="Dry run is the default; add --apply to move files and rewrite stores.")
    parser.add_argument("--apply", action="store_true",
                        help="perform the moves and the store rewrites (default: plan only)")
    parser.add_argument("--verify", action="store_true",
                        help="report files left behind, orphaned karaoke files, unpaired "
                             "originals and store references to the old root")
    parser.add_argument("--app-dir", default=str(DEFAULT_APP_DIR),
                        help="JockeyBox app-data folder (default: %s)" % DEFAULT_APP_DIR)
    parser.add_argument("--settings", default=None,
                        help="settings.json to read (default: <app-dir>/settings.json)")
    args = parser.parse_args(argv)

    app_dir = Path(args.app_dir)
    settings_path = Path(args.settings) if args.settings else app_dir / "settings.json"
    log_path = app_dir / LOG_NAME

    resolved = resolve_roots(settings_path)
    if resolved is None:
        return 1
    music_win, karaoke_win, merged = resolved

    music_root = Path(to_wsl(music_win))
    karaoke_root = Path(to_wsl(karaoke_win))

    if not music_root.is_dir():
        say("Music folder does not exist from WSL: %s (%s)" % (music_root, music_win))
        return 1
    if not merged and not karaoke_root.is_dir():
        say("Karaoke folder does not exist from WSL: %s (%s)" % (karaoke_root, karaoke_win))
        return 1
    if not merged and (inside(karaoke_root, music_root) or inside(music_root, karaoke_root)):
        say("The two roots are nested (%s / %s); refusing to walk them."
            % (karaoke_win, music_win))
        return 1

    if merged:
        if args.verify:
            # After a merge both names point at the music root; the old root is
            # still recoverable from the settings backup the merge left behind.
            old_win = old_karaoke_root(app_dir)
            old_root = None
            if old_win and norm_win(old_win) != norm_win(music_win):
                candidate = Path(to_wsl(old_win))
                if candidate.is_dir():
                    old_root = candidate
            head("JockeyBox karaoke merge - verify")
            say("  music root    %s" % music_win)
            if old_root:
                say("  karaoke root  %s (the old root, from settings.json%s)"
                    % (to_win(str(old_root)), BACKUP_SUFFIX))
            else:
                say("  karaoke root  %s (same folder as the music root)" % karaoke_win)
            if not old_root:
                say("  note          no settings backup, so only the merged tree is checked")
                return verify(music_root, None, app_dir, music_win, karaoke_win)
            return verify(music_root, old_root, app_dir, music_win, old_win)
        say("KaraokeDirectory already is the music root (%s); nothing to merge." % music_win)
        return 0

    if args.verify and not args.apply:
        head("JockeyBox karaoke merge - verify")
        say("  music root    %s" % music_win)
        say("  karaoke root  %s" % karaoke_win)
        return verify(music_root, karaoke_root, app_dir, music_win, karaoke_win)

    head("JockeyBox karaoke merge - %s"
         % ("APPLY" if args.apply else "dry run (nothing is changed)"))
    say("  music root    %s" % music_win)
    say("  karaoke root  %s" % karaoke_win)
    say("  music (wsl)   %s" % music_root)
    say("  karaoke (wsl) %s" % karaoke_root)
    say("  app data      %s" % app_dir)

    done = read_log(log_path)
    plan, already, skipped, ignored = build_plan(music_root, karaoke_root, done)

    head("Plan")
    if not plan:
        say("  nothing to move")
    for mv in plan:
        say("  %s %s" % ("move     " if args.apply else "would move",
                        to_win(str(mv.src))))
        say("      -> %s%s" % (to_win(str(mv.dst)),
                              "   (collision: %s)" % mv.reason if mv.collision else ""))

    collisions = [mv for mv in plan if mv.collision]
    total_bytes = sum(mv.size for mv in plan)

    head("Summary")
    say("  files to move        %d" % len(plan))
    say("  already moved        %d  (in the old root, but the log says done)" % len(already))
    say("  sources on record    %d  (%s, one line per source path)"
        % (len(done), LOG_NAME))
    say("  target collisions    %d%s"
        % (len(collisions), '  (suffixed " (2)", " (3)", ...)' if collisions else ""))
    say("  bytes                %s (%d)" % (human_bytes(total_bytes), total_bytes))
    non_audio = [mv for mv in plan if not is_audio(mv.src.name)]
    if non_audio:
        say("  non-audio files      %d  (also moved, with the same marker rule)"
            % len(non_audio))
    if skipped:
        say("  skipped              %d" % len(skipped))
        for line in skipped:
            say("    ! %s" % line)
    if ignored:
        say("  left in place        %d  (Explorer/WSL system files)"
            % len(ignored))
    if collisions:
        say("")
        for mv in collisions:
            say("  collision: %s (%s)" % (to_win(str(mv.requested)), mv.reason))
            say("             -> %s" % to_win(str(mv.dst)))

    if not args.apply:
        say("")
        say("Nothing was changed. Re-run with --apply to move %d file(s)." % len(plan))
        return 0

    head("Backups")
    created, kept = backup_stores(app_dir)
    if not created and not kept:
        say("  no store files found")
    for path in created:
        say("  backed up  %s" % path)
    for path in kept:
        say("  kept       %s  (backup already existed)" % path)

    head("Moving")
    moved, failures = perform(plan, log_path)
    say("  moved   %d" % len(moved))
    say("  failed  %d" % len(failures))
    say("  log     %s" % log_path)

    rewrite_stores(app_dir, move_map(moved, done), music_win, merge_root=not failures)

    left = [p for p in scan_root(karaoke_root) if not is_system_file(p.name)]
    head("Result")
    say("  %d moved, %d failed, %d file(s) still in %s."
        % (len(moved), len(failures), len(left), karaoke_win))
    if failures:
        say("  KaraokeDirectory was left as it was, so re-running --apply resumes"
            " from the log.")

    if args.verify:
        return verify(music_root, karaoke_root, app_dir, music_win, karaoke_win)
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except BrokenPipeError:
        # Piping into head/less closes stdout early; that is not a failure.
        os._exit(0)
