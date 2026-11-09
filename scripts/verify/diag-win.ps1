# diagnostic: what top-level windows does JockeyBox own right now?
$env:TMP = 'C:\Dev\tmp'; $env:TEMP = 'C:\Dev\tmp'
New-Item -ItemType Directory -Force -Path 'C:\Dev\tmp' | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Diag -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr l);
[DllImport("user32.dll")] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(System.IntPtr h);
[DllImport("user32.dll")] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindowEnabled(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out R r);
[StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
'@
$p = Get-Process JockeyBox -ErrorAction SilentlyContinue | Select-Object -First 1
$lines = New-Object System.Collections.ArrayList
if ($null -eq $p) { [void]$lines.Add('JockeyBox not running'); $lines | Set-Content C:\Dev\shots\diag-win.log; exit 0 }
[void]$lines.Add("pid $($p.Id) main=$($p.MainWindowHandle) title='$($p.MainWindowTitle)'")
$cb = [Diag.W+EnumProc]{
    param([System.IntPtr]$h, [System.IntPtr]$l)
    $wpid = [uint32]0
    [void][Diag.W]::GetWindowThreadProcessId($h, [ref]$wpid)
    if ($wpid -eq [uint32]$script:targetPid) {
        $len = [Diag.W]::GetWindowTextLength($h)
        $sb = New-Object System.Text.StringBuilder ($len + 2)
        [void][Diag.W]::GetWindowText($h, $sb, $sb.Capacity)
        $cn = New-Object System.Text.StringBuilder 256
        [void][Diag.W]::GetClassName($h, $cn, 256)
        $r = New-Object Diag.W+R
        [void][Diag.W]::GetWindowRect($h, [ref]$r)
        [void]$script:Lines.Add(("hwnd={0} vis={1} en={2} cls='{3}' title='{4}' rect={5},{6},{7},{8}" -f `
            $h.ToInt64(), [Diag.W]::IsWindowVisible($h), [Diag.W]::IsWindowEnabled($h), $cn.ToString(), $sb.ToString(), $r.L, $r.T, $r.Rt, $r.B))
    }
    return $true
}
$script:targetPid = $p.Id
$script:Lines = $lines
[void][Diag.W]::EnumWindows($cb, [System.IntPtr]::Zero)
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
$bmp.Save('C:\Dev\shots\diag-screen.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
[void]$lines.Add("screen $($b.Width)x$($b.Height) -> diag-screen.png")
$lines | Set-Content C:\Dev\shots\diag-win.log -Encoding UTF8
'done' | Set-Content C:\Dev\shots\diag-win.done
