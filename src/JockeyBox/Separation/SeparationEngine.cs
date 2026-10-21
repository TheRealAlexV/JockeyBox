using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace JockeyBox.Separation;

/// <summary>
/// Vocal-removal engine backed by the UVR model family, executed through the
/// audio-separator CLI (the headless implementation of Ultimate Vocal Remover)
/// running in a private Python venv under %APPDATA%\JockeyBox.
///
/// The engine owns two directories:
///   uvr-venv\    - dedicated Python virtual environment with audio-separator
///   uvr-models\  - downloaded UVR model files (fetched on first use)
/// </summary>
public partial class SeparationEngine
{
    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JockeyBox");

    public static string VenvDir => Path.Combine(AppDataDir, "uvr-venv");
    public static string ModelsDir => Path.Combine(AppDataDir, "uvr-models");
    public static string CliPath => Path.Combine(VenvDir, "Scripts", "audio-separator.exe");
    private static string VenvPython => Path.Combine(VenvDir, "Scripts", "python.exe");

    /// <summary>True when the engine venv is ready to separate.</summary>
    public static bool IsInstalled => File.Exists(CliPath) && File.Exists(VenvPython);

    // ---- environment setup -------------------------------------------------

    /// <summary>
    /// Create the venv and install audio-separator into it, streaming progress
    /// lines to <paramref name="log"/>. Safe to re-run (rebuilds the venv).
    /// </summary>
    public static async Task InstallAsync(string? customPython, bool useGpu,
                                          IProgress<string> log, CancellationToken ct)
    {
        var python = await DetectPythonAsync(customPython, log, ct)
            ?? throw new InvalidOperationException(
                "No suitable Python found. Install Python 3.10-3.13 from python.org " +
                "(or the Microsoft Store) and try again, or set an explicit python.exe " +
                "path in Settings.");

        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(ModelsDir);

        log.Report($"Creating virtual environment with {python}\u2026");
        await RunLoggedAsync(python, new[] { "-m", "venv", "--clear", VenvDir }, log, ct,
            "venv creation failed");

        log.Report("Upgrading pip\u2026");
        await RunLoggedAsync(VenvPython, new[] { "-m", "pip", "install", "--upgrade", "pip", "wheel" },
            log, ct, "pip upgrade failed");

        var package = useGpu ? "audio-separator[gpu]" : "audio-separator[cpu]";
        log.Report($"Installing {package} (this downloads PyTorch - several hundred MB, please wait)\u2026");
        await RunLoggedAsync(VenvPython, new[] { "-m", "pip", "install", "--upgrade", package },
            log, ct, "audio-separator install failed");

        log.Report("Verifying installation\u2026");
        await RunLoggedAsync(CliPath, new[] { "--version" }, log, ct, "engine verification failed");

        log.Report("Engine installed successfully.");
    }

    // ---- python auto-install ----------------------------------------------

    /// <summary>Pinned python.org fallback release used when winget is unavailable.</summary>
    private const string PythonFallbackUrl =
        "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";

    /// <summary>
    /// Install Python 3.12 for the current user, preferring winget and falling
    /// back to a silent python.org installer download. Returns the resolved
    /// python.exe path on success.
    /// </summary>
    public static async Task<string> InstallPythonAsync(IProgress<string> log, CancellationToken ct)
    {
        // --- Attempt 1: winget (ships with Windows 10 21H2+ / Windows 11) ---
        try
        {
            var (code, _) = await RunCaptureAsync("winget", new[] { "--version" }, ct);
            if (code == 0)
            {
                log.Report("Installing Python 3.12 via winget\u2026");
                var psi = BuildPsi("winget", new[]
                {
                    "install", "-e", "--id", "Python.Python.3.12",
                    "--scope", "user", "--silent",
                    "--accept-package-agreements", "--accept-source-agreements",
                    "--disable-interactivity",
                });
                using var winget = new Process { StartInfo = psi };
                winget.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(e.Data); };
                winget.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(e.Data); };
                winget.Start();
                winget.BeginOutputReadLine();
                winget.BeginErrorReadLine();
                using var reg = ct.Register(() => { try { winget.Kill(entireProcessTree: true); } catch { } });
                await winget.WaitForExitAsync(CancellationToken.None);
                ct.ThrowIfCancellationRequested();

                // 0 = installed; 0x8A15002B = already installed (treat as success).
                if (winget.ExitCode is 0 or unchecked((int)0x8A15002B))
                {
                    var found = await DetectPythonAsync(null, log, ct);
                    if (found != null) return found;
                    log.Report("winget reported success but Python was not found; trying the python.org installer\u2026");
                }
                else
                {
                    log.Report($"winget install failed (exit code {winget.ExitCode}); trying the python.org installer\u2026");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            log.Report("winget not available; using the python.org installer\u2026");
        }

        // --- Attempt 2: python.org official installer, silent per-user ---
        var installerPath = Path.Combine(Path.GetTempPath(), "JockeyBox",
            "python-installer-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
        try
        {
            log.Report($"Downloading {PythonFallbackUrl}\u2026");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            await using (var download = await http.GetStreamAsync(PythonFallbackUrl, ct))
            await using (var file = File.Create(installerPath))
            {
                await download.CopyToAsync(file, ct);
            }

            log.Report("Running the Python installer silently (per-user, adds PATH)\u2026");
            var psi = BuildPsi(installerPath, new[]
            {
                "/quiet", "InstallAllUsers=0", "PrependPath=1",
                "Include_test=0", "Include_doc=0", "SimpleInstall=1",
            });
            using var installer = new Process { StartInfo = psi };
            installer.Start();
            using var reg = ct.Register(() => { try { installer.Kill(entireProcessTree: true); } catch { } });
            await installer.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();

            if (installer.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Python installer exited with code {installer.ExitCode}. " +
                    "Install Python manually from python.org and retry.");
        }
        finally
        {
            try { File.Delete(installerPath); } catch { }
        }

        return await DetectPythonAsync(null, log, ct)
            ?? throw new InvalidOperationException(
                "Python was installed but could not be located. " +
                "Restart JockeyBox and try again (the PATH change needs a fresh process).");
    }

    /// <summary>Locate a Python 3.10-3.13 interpreter on this machine.</summary>
    public static async Task<string?> DetectPythonAsync(string? customPython,
                                                        IProgress<string> log, CancellationToken ct)
    {
        var candidates = new List<string[]>();
        if (!string.IsNullOrWhiteSpace(customPython)) candidates.Add(new[] { customPython });
        foreach (var v in new[] { "-3.12", "-3.11", "-3.13", "-3.10", "-3" })
            candidates.Add(new[] { "py", v });

        // Well-known install locations. Checking these directly matters right
        // after an automated Python install: this process still has the old
        // PATH, so bare "python"/"py" may not resolve yet.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var ver in new[] { "312", "311", "313", "310" })
        {
            candidates.Add(new[] { Path.Combine(localAppData, "Programs", "Python", $"Python{ver}", "python.exe") });
            candidates.Add(new[] { Path.Combine(programFiles, $"Python{ver}", "python.exe") });
        }
        candidates.Add(new[] { Path.Combine(windowsDir, "py.exe"), "-3" });
        candidates.Add(new[] { Path.Combine(localAppData, "Programs", "Python", "Launcher", "py.exe"), "-3" });
        candidates.Add(new[] { "python" });
        candidates.Add(new[] { "python3" });

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var exe = candidate[0];
            if (Path.IsPathRooted(exe) && !File.Exists(exe)) continue;
            var versionArgs = candidate.Skip(1).Append("--version").ToArray();
            try
            {
                var (code, output) = await RunCaptureAsync(exe, versionArgs, ct);
                var m = PythonVersionRegex().Match(output);
                if (code != 0 || !m.Success) continue;

                int major = int.Parse(m.Groups[1].Value);
                int minor = int.Parse(m.Groups[2].Value);
                if (major != 3 || minor < 10 || minor > 13) continue;

                log.Report($"Found Python {major}.{minor} via '{string.Join(' ', candidate)}'.");

                // The venv must be created by the concrete interpreter. Resolve
                // launcher invocations to the real executable path.
                if (Path.GetFileNameWithoutExtension(exe).Equals("py", StringComparison.OrdinalIgnoreCase))
                {
                    var launcherArgs = candidate.Skip(1)
                        .Append("-c").Append("import sys; print(sys.executable)").ToArray();
                    var (c2, path) = await RunCaptureAsync(exe, launcherArgs, ct);
                    if (c2 == 0 && File.Exists(path.Trim())) return path.Trim();
                    continue;
                }
                return exe;
            }
            catch
            {
                // candidate not present - try the next one
            }
        }
        return null;
    }

    // ---- separation ----------------------------------------------------------

    public record SeparationRequest(
        string InputPath,
        string OutputPath,       // final instrumental target (extension decides nothing; format param does)
        string ModelFilename,
        string OutputFormat);    // FLAC / MP3 / WAV

    /// <summary>
    /// Run one separation. Reports progress 0-100 (model download + inference)
    /// and returns the path of the created instrumental file.
    /// </summary>
    public async Task<string> SeparateAsync(SeparationRequest request,
                                            IProgress<(int percent, string phase)> progress,
                                            CancellationToken ct)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Vocal removal engine is not installed.");
        if (!File.Exists(request.InputPath))
            throw new FileNotFoundException("Source file not found.", request.InputPath);

        var tempDir = Path.Combine(Path.GetTempPath(), "JockeyBox",
            "sep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = CliPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(request.InputPath);
            psi.ArgumentList.Add("--model_filename"); psi.ArgumentList.Add(request.ModelFilename);
            psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(tempDir);
            psi.ArgumentList.Add("--model_file_dir"); psi.ArgumentList.Add(ModelsDir);
            psi.ArgumentList.Add("--output_format"); psi.ArgumentList.Add(request.OutputFormat);
            psi.ArgumentList.Add("--log_level"); psi.ArgumentList.Add("info");
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";

            var stderrTail = new Queue<string>(8);
            using var process = new Process { StartInfo = psi };

            void HandleLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                lock (stderrTail)
                {
                    if (stderrTail.Count >= 8) stderrTail.Dequeue();
                    stderrTail.Enqueue(line);
                }

                var phase = line.Contains("ownload", StringComparison.Ordinal)
                    ? "downloading model" : "separating";
                var pm = ProgressRegex().Match(line);
                if (pm.Success && int.TryParse(pm.Groups[1].Value, out int pct) && pct <= 100)
                    progress.Report((pct, phase));
                else if (phase == "downloading model")
                    progress.Report((0, phase));
            }

            process.Start();

            // tqdm rewrites its progress line with bare \r (no newline), so
            // event-based line reading would sit silent until the very end.
            // Pump both streams raw and split on \r or \n to get live percts.
            var pumps = Task.WhenAll(
                PumpLinesAsync(process.StandardOutput, HandleLine),
                PumpLinesAsync(process.StandardError, HandleLine));

            using var reg = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            });
            await process.WaitForExitAsync(CancellationToken.None);
            await pumps;
            ct.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                string detail;
                lock (stderrTail) detail = string.Join('\n', stderrTail);
                throw new InvalidOperationException(
                    $"audio-separator exited with code {process.ExitCode}:\n{detail}");
            }

            var instrumental = PickInstrumental(tempDir)
                ?? throw new InvalidOperationException("Separation produced no instrumental stem.");

            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            File.Move(instrumental, request.OutputPath, overwrite: true);
            progress.Report((100, "done"));
            return request.OutputPath;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Identify the instrumental stem among the CLI's output files. UVR models
    /// usually name stems "(Instrumental)"/"(Vocals)"; some emit "(No Vocals)",
    /// so check that before falling back to "the stem that isn't vocals".
    /// </summary>
    private static string? PickInstrumental(string dir)
    {
        var files = Directory.GetFiles(dir);
        return files.FirstOrDefault(f =>
                   Path.GetFileName(f).Contains("instrumental", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f =>
                   Path.GetFileName(f).Contains("no vocals", StringComparison.OrdinalIgnoreCase)
                   || Path.GetFileName(f).Contains("no_vocals", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f =>
                   !Path.GetFileName(f).Contains("vocal", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Read a process stream and emit segments terminated by \n OR \r, so
    /// tqdm-style carriage-return progress updates surface immediately.
    /// </summary>
    private static async Task PumpLinesAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var current = new StringBuilder(256);
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c is '\n' or '\r')
                {
                    if (current.Length > 0)
                    {
                        onLine(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                    if (current.Length > 8192) { onLine(current.ToString()); current.Clear(); }
                }
            }
        }
        if (current.Length > 0) onLine(current.ToString());
    }

    // ---- process helpers -------------------------------------------------------

    private static async Task RunLoggedAsync(string exe, string[] args,
                                             IProgress<string> log, CancellationToken ct,
                                             string errorContext)
    {
        var psi = BuildPsi(exe, args);
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Report(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{errorContext} (exit code {process.ExitCode}).");
    }

    private static async Task<(int exitCode, string output)> RunCaptureAsync(
        string exe, string[] args, CancellationToken ct)
    {
        var psi = BuildPsi(exe, args);
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var reg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static ProcessStartInfo BuildPsi(string exe, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUTF8"] = "1";
        return psi;
    }

    [GeneratedRegex(@"Python\s+(\d+)\.(\d+)")]
    private static partial Regex PythonVersionRegex();

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex ProgressRegex();
}
