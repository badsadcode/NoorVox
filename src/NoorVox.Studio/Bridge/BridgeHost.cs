using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NoorVox.Core;

namespace NoorVox.Studio.Bridge;

/// <summary>
/// Typed request/response/event bridge between the React UI and C# (spec §9.4).
/// UI → host: <c>{"id","method","params"}</c>. Host → UI: <c>{"id","ok","result"|"error"}</c> or <c>{"event","data"}</c>.
/// Only messages from the bundled app origin are accepted; every method validates its own parameters.
/// </summary>
public sealed class BridgeHost(CoreWebView2 core, Dispatcher dispatcher)
{
    Func<string, JsonElement?, CancellationToken, Task<object?>>? _dispatch;
    string _origin = "";

    sealed class Request
    {
        public string Id { get; set; } = "";
        public string Method { get; set; } = "";
        public JsonElement? Params { get; set; }
    }

    public void Attach(Func<string, JsonElement?, CancellationToken, Task<object?>> dispatch, string allowedOrigin)
    {
        _dispatch = dispatch;
        _origin = allowedOrigin;
        core.WebMessageReceived += OnMessage;
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(_origin, StringComparison.OrdinalIgnoreCase)) return;
        Request? req;
        try { req = JsonSerializer.Deserialize<Request>(e.TryGetWebMessageAsString(), Json.Compact); }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return; }
        if (req is null || req.Id.Length is 0 or > 64 || req.Method.Length is 0 or > 64) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _dispatch!(req.Method, req.Params, CancellationToken.None);
                Post(new { id = req.Id, ok = true, result });
            }
            catch (NoorVoxException ex)
            {
                Post(new { id = req.Id, ok = false, error = new { code = ex.Code, message = ex.Message, details = ex.Details } });
            }
            catch (Exception ex)
            {
                StudioLog.Write($"bridge {req.Method} failed: {ex}");
                Post(new { id = req.Id, ok = false, error = new { code = ErrorCodes.InternalError, message = ex.Message } });
            }
        });
    }

    public void Emit(string evt, object? data) => Post(new { @event = evt, data });

    void Post(object message)
    {
        var json = Json.Serialize(message, indented: false);
        dispatcher.InvokeAsync(() =>
        {
            try { core.PostWebMessageAsJson(json); }
            catch (InvalidOperationException) { /* window closing */ }
        });
    }
}
