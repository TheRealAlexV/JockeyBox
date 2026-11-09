#!/usr/bin/env python3
"""Generate the deployable capture harness (jb-shots.ps1, UTF-8 with BOM).

The capture run reuses the verification harness (jb-e2e.ps1) for its prelude
(UIA helpers, app reads) and for the step-0 reset, and appends capture-body.ps1.
Keeping the two parts as sources means the capture run can never drift from the
helpers the e2e run proved.

usage: python3 build-shots.py [/mnt/c/Dev]
"""
import sys

HERE = '/home/alex/OBS/JockeyBox/scripts/verify'
DEST = (sys.argv[1] if len(sys.argv) > 1 else '/mnt/c/Dev') + '/jb-shots.ps1'

e2e = open(f'{HERE}/jb-e2e.ps1', encoding='utf-8').read().split('\n')

# jb-e2e.ps1 line numbers (1-based) that bracket the pieces we want
PRELUDE_END = 674       # the "app log marker" log line
STEP0_START = 688       # "# ---- step 0: reset the persisted state"
STEP0_END = 788         # the R4 tempo reset assertion

prelude = e2e[:PRELUDE_END]
step0 = e2e[STEP0_START:STEP0_END]
body = open(f'{HERE}/capture-body.ps1', encoding='utf-8').read().split('\n')
prerestart = open(f'{HERE}/pre-restart.ps1', encoding='utf-8').read().split('\n')

head = prelude + [
    '',
    '$libTab = Get-LibraryTab',
    '$script:tabNames = @()',
    'foreach ($t in (Get-TabItems)) { $script:tabNames += $t.Name }',
    'Log ("tabs: {0}" -f ($script:tabNames -join " | "))',
    'if ($libTab) { [void](Select-El $libTab) }',
    '',
] + step0 + [''] + body

src = '\n'.join(head)

# the dialog closer has to run before the prelude's restart block
anchor = 'if ($Restart) {'
assert anchor in src, 'restart block not found in the prelude'
src = src.replace(anchor, '\n'.join(prerestart) + '\n\n' + anchor, 1)

# jb-shot.ps1 ends with `exit`, which would end this script: run it in a child.
src = src.replace(
    """        $out = & 'C:\\Dev\\jb-shot.ps1' -Process 'JockeyBox' -Out $path @Args2 2>&1""",
    """        $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\\Dev\\jb-shot.ps1',
                  '-Process', 'JockeyBox', '-Out', $path) + @($Args2)
        $out = & powershell.exe @argv 2>&1""")

b = src.replace('\r\n', '\n').replace('\n', '\r\n').encode('utf-8')
open(DEST, 'wb').write(b'\xef\xbb\xbf' + b)
print(f'wrote {DEST} ({len(b)} bytes, {len(head)} lines, BOM)')
