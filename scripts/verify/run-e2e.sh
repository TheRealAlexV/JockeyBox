#!/usr/bin/env bash
# Run the JockeyBox 2.0 end-to-end verification against the deployed app.
#
#   run-e2e.sh                 restart the app for a clean state, then verify
#   run-e2e.sh -KeepRunning    reuse the app that is already running
#   run-e2e.sh -NoDeploy       don't re-push the script (deployed copy is current)
#
# Everything runs through a Session-1 scheduled task: `ssh`/`winps` land in
# Session 0 and cannot see the app's windows.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="$here/jb-e2e.ps1"
dst_win='C:\Dev\jb-e2e.ps1'
dst="/mnt/c/Dev/jb-e2e.ps1"
shots='/mnt/c/Dev/shots'
task='JB-E2E'

ps_args=''
deploy=1
for a in "$@"; do
  case "$a" in
    -KeepRunning) ps_args="$ps_args -Restart \$false" ;;
    -NoDeploy)    deploy=0 ;;
    *) echo "unknown option: $a" >&2; exit 2 ;;
  esac
done

# PowerShell reads a BOM-less .ps1 as ANSI and mangles the transport glyphs.
deploy_script() {
  python3 - "$src" "$dst" <<'PY'
import sys
raw = open(sys.argv[1], 'rb').read()
if raw.startswith(b'\xef\xbb\xbf'):
    raw = raw[3:]
body = raw.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
open(sys.argv[2], 'wb').write(b'\xef\xbb\xbf' + body)
print('deployed %s (UTF-8 BOM, %d bytes)' % (sys.argv[2], len(body) + 3))
PY
}

[ "$deploy" = 1 ] && { deploy_script || exit 1; }

rm -f "$shots/e2e.json" "$shots/e2e-run.log" "$shots"/e2e-*.png

schtasks_cmd="schtasks /create /tn '$task' /tr 'cmd /c powershell -NoProfile -ExecutionPolicy Bypass -File $dst_win $ps_args > C:\\Dev\\shots\\e2e-run.log 2>&1' /sc once /st 23:59 /rl highest /ru Alex /it /f"
winps "$schtasks_cmd" >/dev/null || { echo "failed to create scheduled task" >&2; exit 1; }
winps "schtasks /run /tn '$task'" >/dev/null || { echo "failed to start scheduled task" >&2; exit 1; }

echo "running (restart + 5 steps + settings, ~9 min) ..."
for _ in $(seq 1 180); do
  [ -f "$shots/e2e.json" ] && break
  sleep 10
done
[ -f "$shots/e2e.json" ] || { echo "timed out; see $shots/e2e-run.log" >&2; exit 1; }

python3 - "$shots/e2e.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding='utf-8-sig'))
print('verdict:', d.get('verdict'), '' if not d.get('fatal') else 'fatal: %s' % d['fatal'])
for a in d['assertions']:
    mark = 'PASS' if a['pass'] is True else 'FAIL' if a['pass'] is False else 'INFO'
    print('  %-4s %-4s %s' % (mark, a['id'], a['assertion']))
    if a['pass'] is False:
        print('        expected=%s actual=%s' % (a.get('expected'), a.get('actual')))
c = d['counts']
sys.exit(0 if c['fail'] == 0 else 1)
PY
