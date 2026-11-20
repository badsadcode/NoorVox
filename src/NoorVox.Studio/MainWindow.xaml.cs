using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using NoorVox.Core.Storage;
using NoorVox.Core.Workers;
using NoorVox.Studio.Bridge;

namespace NoorVox.Studio;

public partial class MainWindow : Window
{
    public const string AppHost = "app.noorvox.local";
    public const string MediaHost = "media.noorvox.local";
    public const string TakesHost = "takes.noorvox.local";

    readonly StudioOptions _options;
    /// <summary>Kill-on-close job shared by all studio-bound workers: if the studio dies, Windows ends them.</summary>
    readonly JobObject _job = new();
    readonly string _workerExe = Path.Combine(AppContext.BaseDirectory, "NoorVox.Worker.exe");
    BridgeHost? _bridge;
    StudioApi? _api;
    bool _closeReady;
    volatile string? _previewDir;
    volatile string? _takesDir;
    CoreWebView2Environment? _env;

    public MainWindow(StudioOptions options)
    {
        _options = options;
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    public static string PreviewDir(StorageContext s) => Path.Combine(s.TempDir, "studio", "previews");

    async Task InitializeAsync()
    {
        StorageContext storage;
        try { storage = StorageContext.Load(); }
        catch (Exception ex) { ShowFallback($"Settings could not be read: {ex.Message}"); return; }
        StudioLog.Configure(storage.LogsDir);
        ApplyTheme(_options.Theme ?? storage.Settings.Studio.Theme);
        CleanOldPreviews(storage);

        try
        {
            var userData = storage.Resolve(StorageKind.WebView).Path; // set before creation (spec §8.1)
            Directory.CreateDirectory(userData);
            // A desktop app may play the audio it just generated without a fresh click (Chromium autoplay policy).
            _env = await CoreWebView2Environment.CreateAsync(null, userData,
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            await Web.EnsureCoreWebView2Async(_env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException)
        {
            ShowFallback($"Microsoft Edge WebView2 Runtime is required. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and start NoorVox Studio again.\n\nDetails: {ex.Message}");
            return;
        }

        var core = Web.CoreWebView2;
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Deny);
        RemapPreviews(storage);
        // Audio is served by a request handler registered before the page loads. (Virtual-host folder mappings added
        // after load are not applied to the already-loaded page, which broke take playback when a project was opened.)
        core.AddWebResourceRequestedFilter($"https://{MediaHost}/*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter($"https://{TakesHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServeLocalAudio;

        bool dev = System.Diagnostics.Debugger.IsAttached || Environment.GetEnvironmentVariable("NOORVOX_DEVTOOLS") == "1";
        core.Settings.AreDevToolsEnabled = dev;
        core.Settings.AreDefaultContextMenusEnabled = dev;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = dev; // no F5/Ctrl+R reloads mid-generation
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        // The UI is local-only: block navigation anywhere else and never open new windows.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{AppHost}/", StringComparison.OrdinalIgnoreCase))
            {
                StudioLog.Write($"blocked navigation to {e.Uri}");
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;

        _bridge = new BridgeHost(core, Dispatcher);
        _api = new StudioApi(this, _job, _bridge, _workerExe);
        _bridge.Attach(_api.InvokeAsync, $"https://{AppHost}/");

        if (_options.Screenshot is not null)
            core.NavigationCompleted += async (_, _) => await CaptureScreenshotAsync(_options.Screenshot);

        var page = _options.Page is { Length: > 0 } p ? "#/" + p : "";
        core.Navigate($"https://{AppHost}/index.html{page}");
        StudioLog.Write($"studio started, ui={wwwroot}, webview data={storage.Resolve(StorageKind.WebView).Path}");
    }

    /// <summary>Points the preview host at the preview folder in the user's temp storage.</summary>
    public void RemapPreviews(StorageContext storage)
    {
        var dir = PreviewDir(storage);
        Directory.CreateDirectory(dir);
        _previewDir = Path.GetFullPath(dir);
    }

    /// <summary>Points the takes host at the open project's takes folder (one open project at a time).</summary>
    public void MapProjectTakes(string takesDir)
    {
        Directory.CreateDirectory(takesDir);
        _takesDir = Path.GetFullPath(takesDir);
    }

    /// <summary>
    /// Serves <c>.wav</c> files from the preview folder or the open project's takes folder. Anything else, or any path
    /// escaping those folders, gets a 404. Read-only, shared-read so a file being generated never blocks.
    /// </summary>
    void ServeLocalAudio(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        string? root = uri.Host.Equals(MediaHost, StringComparison.OrdinalIgnoreCase) ? _previewDir
            : uri.Host.Equals(TakesHost, StringComparison.OrdinalIgnoreCase) ? _takesDir : null;
        string? file = null;
        if (root is not null)
        {
            var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (rel.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) file = full;
            }
        }
        const string cors = "Access-Control-Allow-Origin: https://" + AppHost;
        if (file is null)
        {
            e.Response = _env!.CreateWebResourceResponse(null, 404, "Not Found", cors);
            return;
        }
        try
        {
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            e.Response = _env!.CreateWebResourceResponse(stream, 200, "OK",
                string.Join("\r\n", "Content-Type: audio/wav", $"Content-Length: {stream.Length}", "Cache-Control: no-cache", cors));
        }
        catch (IOException ex)
        {
            StudioLog.Write($"audio serve failed for {file}: {ex.Message}");
            e.Response = _env!.CreateWebResourceResponse(null, 500, "Error", cors);
        }
    }

    static void CleanOldPreviews(StorageContext storage)
    {
        try
        {
            var dir = PreviewDir(storage);
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-3)) File.Delete(f);
        }
        catch (IOException) { }
    }

    async Task CaptureScreenshotAsync(string path)
    {
        await Task.Delay(_options.ScreenshotDelayMs);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using (var fs = File.Create(path))
            await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        StudioLog.Write($"screenshot saved: {path}");
        if (_options.ExitAfterScreenshot) _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    void ShowFallback(string message)
    {
        Web.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Visible;
        FallbackText.Text = message;
    }

    /// <summary>Light themes get a light title bar, dark themes a dark one.</summary>
    public void ApplyTheme(string theme)
    {
        bool dark = theme is not ("daylight" or "lavender");
        var color = theme switch
        {
            "daylight" => "#F4F2FA", "lavender" => "#FBF5FB", "tape" => "#17120D", "thalassa" => "#08151A", "graphite" => "#111213", _ => "#12101C",
        };
        var brush = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        Background = new System.Windows.Media.SolidColorBrush(brush);
        Web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(color);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Quit Studio policy (spec §7.4): stop workers on exit by default; warn before leaving any running.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeReady) { base.OnClosing(e); return; }
        e.Cancel = true;
        _api?.Projects.CancelAll(); // running jobs become "interrupted" and can be resumed next time
        try
        {
            var storage = StorageContext.Load();
            using var sup = new WorkerSupervisor(storage, _workerExe, "studio", _job);
            var mine = (await sup.ListAsync(CancellationToken.None))
                .Where(w => w.LaunchedBy == "studio" && w.State is not ("error" or "stopped")).ToList();

            bool stop = storage.Settings.Workers.StopOnExit;
            if (!stop && mine.Count > 0)
            {
                long mb = mine.Sum(w => w.Resources?.WorkingSetBytes ?? 0) / 1048576;
                var answer = MessageBox.Show(this,
                    $"{mine.Count} NoorVox voice engine(s) will keep running in the background (about {mb} MB of memory), as set in Settings.\n\n" +
                    "Yes = keep running   ·   No = stop them now   ·   Cancel = don't close",
                    "NoorVox Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) return;
                stop = answer == MessageBoxResult.No;
            }
            if (stop && mine.Count > 0)
            {
                _bridge?.Emit("app.closing", new { message = "Stopping voice engine…" });
                foreach (var w in mine)
                {
                    try { await sup.StopAsync(w.WorkerId, force: false, TimeSpan.FromSeconds(10), CancellationToken.None); }
                    catch (NoorVox.Core.NoorVoxException) { await sup.StopAsync(w.WorkerId, force: true, TimeSpan.FromSeconds(5), CancellationToken.None); }
                }
            }
        }
        catch (Exception ex)
        {
            StudioLog.Write($"close policy failed: {ex}"); // the job object below still guarantees bound workers end
        }
        _closeReady = true;
        // Never call Close() inside a Closing event (WPF throws if the awaits above completed synchronously):
        // queue it so it runs after this event has fully unwound.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    protected override void OnClosed(EventArgs e)
    {
        _job.Dispose(); // any still-bound worker is terminated by Windows here
        StudioLog.Write("studio closed");
        base.OnClosed(e);
    }
}

public static class StudioLog
{
    static string? _path;
    static readonly object Lock = new();

    public static void Configure(string logsDir)
    {
        try { Directory.CreateDirectory(logsDir); _path = Path.Combine(logsDir, "studio.log"); } catch (IOException) { }
    }

    public static void Write(string message)
    {
        if (_path is null) return;
        lock (Lock)
        {
            try { File.AppendAllText(_path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}
