using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

        if (window.Content is FrameworkElement content)
        {
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            Pump(150);

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var stream = File.Create(path)) encoder.Save(stream);

            Console.Out.WriteLine($"uishot: {width}x{height} -> {path}");
            Console.Out.Flush();

            window.Close();
            return 0;
        }

        var fallback = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        fallback.Render(window);

        var fallbackEncoder = new PngBitmapEncoder();
        fallbackEncoder.Frames.Add(BitmapFrame.Create(fallback));
        string? fallbackDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(fallbackDirectory)) Directory.CreateDirectory(fallbackDirectory);
        using (var stream = File.Create(path)) fallbackEncoder.Save(stream);
        Console.Out.WriteLine($"uishot: {width}x{height} (window visual) -> {path}");
        window.Close();
        return 0;
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
