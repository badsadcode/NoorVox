using System.Windows;
using System.Windows.Threading;

namespace NoorVox.Studio;

public partial class App : Application
{
    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One studio per user session: two studios would race for the same 'studio' worker and settings.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\NoorVox.Studio", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("NoorVox Studio is already running.", "NoorVox Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        new MainWindow(StudioOptions.Parse(e.Args)).Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        StudioLog.Write($"UNHANDLED {e.Exception}");
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nYour work on disk is safe. Details are in the studio log.",
            "NoorVox Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Command-line options (mainly for documentation screenshots and automated smoke tests).</summary>
public sealed record StudioOptions(string? Page, string? Screenshot, int ScreenshotDelayMs, bool ExitAfterScreenshot, string? Theme)
{
    public static StudioOptions Parse(string[] args)
    {
        string? Get(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        return new StudioOptions(Get("--page"), Get("--screenshot"), int.TryParse(Get("--screenshot-delay"), out var d) ? d : 2500,
            args.Contains("--exit-after-screenshot"), Get("--theme"));
    }
}
