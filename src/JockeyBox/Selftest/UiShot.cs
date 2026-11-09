using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JockeyBox.Tools;
using JockeyBox.Views;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--uishot &lt;file.png&gt; [--uishot-wait ms]</c>: builds the real main window,
/// lays it out, and writes a PNG of it with no desktop involved.
///
/// <para><b>Why.</b> Verification must never drive the operator's console session
/// (mouse, focus, windows). This runs in whatever session the process started in -
/// including a non-interactive service session - with software rendering, so a
/// screenshot is produced without a visible window and without a logged-on user
/// watching it happen.</para>
/// </summary>
internal static class UiShot
{
    public static bool Requested(string[] args) => Find(args, "--uishot") != null;

    public static int Run(string[] args)
    {
        string? path = Find(args, "--uishot");
        if (string.IsNullOrWhiteSpace(path)) return 2;

        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        int waitMs = 2500;
        string? waitText = Find(args, "--uishot-wait");
        if (waitText != null && int.TryParse(waitText, out int parsed)) waitMs = Math.Clamp(parsed, 200, 60000);

        string target = (Find(args, "--uishot-window") ?? "main").ToLowerInvariant();

        // Provenance: a capture is only evidence if you know which settings file
        // the window read. Record home, resolved path and its timestamp before the
        // window is constructed, so a later rewrite of settings.json cannot be
        // mistaken for the state in the picture.
        string settingsPath = Settings.AppSettings.SettingsPath;
        string settingsStamp = File.Exists(settingsPath)
            ? File.GetLastWriteTime(settingsPath).ToString("yyyy-MM-dd HH:mm:ss")
            : "absent";
        Console.Out.WriteLine(
            $"uishot: home {AppPaths.Home}, settings {settingsPath}, last-write {settingsStamp}");
        Console.Out.Flush();

        Window window = target == "settings"
            ? new SettingsWindow(Settings.AppSettings.Load())
            : new MainWindow();

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -6000;
        window.Top = -6000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;



        window.Show();
        Pump(waitMs);
        window.UpdateLayout();
        Pump(200);

        // The render is driven at an explicit size so the result does not depend
        // on the window station's virtual screen (a service session reports
        // 1024x768 and would otherwise clip the layout).
        int width = 1400;
        int height = 900;
        string? sizeText = Find(args, "--uishot-size");
        if (sizeText != null)
        {
            string[] parts = sizeText.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                width = Math.Clamp(w, 320, 5120);
                height = Math.Clamp(h, 240, 2880);
            }
        }

        // The window owns an HWND, and the service session clamps its client area
        // (1024x768 desktop minus chrome). Layout has to leave that tree entirely:
        // detach the content and host it in a Border that is its own layout root,
        // then lay that out at the requested size. Measuring content while it is
        // still parented is discarded, because UpdateLayout() walks up to the HWND
        // root and re-arranges everything at the client size.
        if (window.Content is not FrameworkElement content)
        {
            Console.Error.WriteLine("uishot: window has no framework content element to render");
            Console.Error.Flush();
            window.Close();
            return 3;
        }

        window.Content = null;
        var host = new Border { Background = window.Background, Child = content };
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        Pump(150);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using (var stream = File.Create(path)) encoder.Save(stream);

        double arrangedWidth = host.ActualWidth;
        double arrangedHeight = host.ActualHeight;
        bool clamped = Math.Abs(arrangedWidth - width) > 1 || Math.Abs(arrangedHeight - height) > 1;

        Console.Out.WriteLine(
            $"uishot: canvas {width}x{height}, arranged {arrangedWidth}x{arrangedHeight} -> {path}");
        if (clamped)
        {
            Console.Error.WriteLine(
                $"uishot: CLAMPED - requested {width}x{height}, arranged {arrangedWidth}x{arrangedHeight}");
            Console.Error.Flush();
        }

        Console.Out.Flush();

        window.Close();
        return clamped ? 3 : 0;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static string? Find(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];

            if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(flag.Length + 1)..];
        }
        return null;
    }
}
