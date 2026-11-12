using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JockeyBox.Tools;

/// <summary>
/// One instance per resolved home.
///
/// <para><b>Why the name is keyed by the home.</b> A shared
/// <c>%APPDATA%\JockeyBox</c> means two processes are two independent writers
/// against one SQLite file - the writer gate in <c>JockeyBoxDatabase</c> is
/// in-process only - so a second launch on the same home must not start at all.
/// A constant name would also stop the sandbox (<c>JOCKEYBOX_HOME</c> pointed
/// somewhere else) from running beside the live app, which is the only way this
/// app gets verified headlessly. Hashing the resolved home gives one rule that
/// does both: same home refuses, different home runs independently.</para>
///
/// <para><b>Headless runs never touch focus.</b> <c>--selftest</c>,
/// <c>--uishot</c>, <c>--uishot-dump</c> and <c>--perf</c> render offscreen by
/// design, so neither side of a collision may raise a window: the refused launch
/// stays silent, and the holder ignores an activation request.</para>
/// </summary>
public static class SingleInstance
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static Thread? _listener;
    private static string? _pidFile;

    /// <summary>
    /// True for the offscreen modes. Set once, before <see cref="TryAcquire"/>.
    /// </summary>
    public static bool Headless { get; set; }

    /// <summary>
    /// A stable 16-hex key for a home: full path, trailing separators trimmed,
    /// lower-cased (Windows paths are case-insensitive), SHA-256.
    /// </summary>
    public static string KeyFor(string home)
    {
        var normalized = Path.GetFullPath(home).TrimEnd('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)), 0, 8)
            .ToLowerInvariant();
    }

    public static string MutexName(string home) => $@"Local\JockeyBox-{KeyFor(home)}";

    private static string ActivateName(string home) => $@"Local\JockeyBox-{KeyFor(home)}-activate";

    /// <summary>The owner's pid, written beside the stores it protects.</summary>
    public static string PidFile(string home) => Path.Combine(home, "instance.pid");

    /// <summary>
    /// Take the home, or report who already has it. A create failure (bad name,
    /// denied handle) must not wedge startup: the app runs, and says so.
    /// </summary>
    public static bool TryAcquire(string home, out string owner, out int ownerPid)
    {
        owner = "unknown";
        ownerPid = 0;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName(home), out bool createdNew);
            if (!createdNew)
            {
                owner = ReadOwner(home, out ownerPid);
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"single-instance: could not create the guard for {home}: {ex.GetType().Name}: {ex.Message}; starting anyway");
            _mutex = null;
            return true;
        }

        _pidFile = PidFile(home);
        try
        {
            File.WriteAllText(_pidFile,
                $"{Environment.ProcessId}\n{home}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        }
        catch (Exception ex)
        {
            Log.Warn($"single-instance: could not write {_pidFile}: {ex.Message}");
        }

        try
        {
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName(home));
        }
        catch (Exception ex)
        {
            Log.Warn($"single-instance: could not create the activation event for {home}: {ex.Message}");
        }

        Log.Info($"single-instance: home {home} held by pid {Environment.ProcessId}");
        return true;
    }

    /// <summary>
    /// Whether the owning process has a window that can be brought forward right
    /// now. A live process with <c>MainWindowHandle == 0</c> cannot be activated:
    /// calling <c>Activate()</c>/<c>SetForegroundWindow</c> on that zero handle
    /// does nothing, which is why a relaunch used to look like "nothing happened".
    /// </summary>
    public static bool HasWindow(int pid)
    {
        if (pid <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadOwner(string home, out int pid)
    {
        pid = 0;
        try
        {
            var path = PidFile(home);
            if (!File.Exists(path)) return "unknown";
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out var found)) return "unknown";
            pid = found;
            try
            {
                using var process = Process.GetProcessById(pid);
                return $"{pid} ({process.ProcessName})";
            }
            catch
            {
                return $"{pid} (no longer running; stale record)";
            }
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// The newcomer's half of "come to the front": let the owner take the
    /// foreground, then ask it to. Both are best effort - a window that only
    /// flashes in the taskbar is still better than a silent refusal.
    /// </summary>
    public static void SignalExisting(string home, int ownerPid)
    {
        if (ownerPid > 0)
        {
            try { AllowSetForegroundWindow(ownerPid); } catch { /* not fatal */ }
        }

        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateName(home), out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"single-instance: could not signal the running window: {ex.Message}");
        }
    }

    /// <summary>
    /// The holder's half: wait for an activation request and run
    /// <paramref name="activate"/> on a background thread. The callback must
    /// marshal to the UI thread itself.
    /// </summary>
    public static void ListenForActivation(Action activate)
    {
        if (_activate is null) return;

        _listener = new Thread(() =>
        {
            while (true)
            {
                bool signalled;
                try { signalled = _activate.WaitOne(); }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { return; }

                if (!signalled) return;
                if (Headless) continue;

                try { activate(); }
                catch (Exception ex) { Log.Warn("single-instance: activation failed: " + ex.Message); }
            }
        })
        {
            IsBackground = true,
            Name = "JockeyBox single-instance",
        };
        _listener.Start();
    }

    /// <summary>
    /// Give the home back. Runs on exit, so closing and relaunching immediately
    /// is never refused by our own stale handle.
    /// </summary>
    public static void Release()
    {
        try
        {
            if (_pidFile is not null && File.Exists(_pidFile)) File.Delete(_pidFile);
        }
        catch
        {
            // A pid file left behind is a stale label, never a lock.
        }

        try { _activate?.Dispose(); } catch { /* shutting down */ }
        _activate = null;

        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        try { _mutex?.Dispose(); } catch { /* shutting down */ }
        _mutex = null;
    }
}
