# A modal dialog (Settings is ShowDialog) blocks WM_CLOSE on its owner, so the
# dialog is closed first and only then the main window. Without this the restart
# waits 25 s for a WM_CLOSE that can never succeed.
if (-not ([System.Management.Automation.PSTypeName]'JbPre.Win32').Type) {
    $env:TMP = 'C:\Dev\tmp'; $env:TEMP = 'C:\Dev\tmp'
    New-Item -ItemType Directory -Force -Path 'C:\Dev\tmp' | Out-Null
    Add-Type -Namespace JbPre -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr l);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(System.IntPtr h);
[DllImport("user32.dll")] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
'@
}
$jbPre = Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1
if ($jbPre) {
    $script:PrePid = $jbPre.Id
    $script:PreHits = New-Object System.Collections.ArrayList
    $script:PreCb = [JbPre.Win32+EnumProc]{
        param([System.IntPtr]$h, [System.IntPtr]$l)
        $wpid = [uint32]0
        [void][JbPre.Win32]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -eq [uint32]$script:PrePid -and [JbPre.Win32]::IsWindowVisible($h)) {
            $len = [JbPre.Win32]::GetWindowTextLength($h)
            $sb = New-Object System.Text.StringBuilder ($len + 2)
            [void][JbPre.Win32]::GetWindowText($h, $sb, $sb.Capacity)
            $t = $sb.ToString()
            # the main window and the invisible WPF helper windows are not dialogs
            if ($t -ne '' -and $t -ne 'JockeyBox' -and $t -ne 'SystemResourceNotifyWindow' -and
                $t -ne 'MediaContextNotificationWindow' -and $t -ne 'WISPTIS' -and
                $t -ne 'CiceroUIWndFrame' -and $t -ne 'MSCTFIME UI' -and $t -ne 'Default IME') {
                [void]$script:PreHits.Add([pscustomobject]@{ hwnd = $h; title = $t })
            }
        }
        return $true
    }
    [void][JbPre.Win32]::EnumWindows($script:PreCb, [System.IntPtr]::Zero)
    foreach ($d in @($script:PreHits)) {
        [void][JbPre.Win32]::PostMessage($d.hwnd, 0x0010, [intptr]::Zero, [intptr]::Zero)
        Log ("  closed the open dialog '{0}' before restarting" -f $d.title)
    }
    if (@($script:PreHits).Count -gt 0) { Start-Sleep -Seconds 2 }
}
