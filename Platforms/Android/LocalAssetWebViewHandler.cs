#if ANDROID
using Android.Runtime;
using Android.Webkit;
using AndroidX.WebKit;
using Java.Interop;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace SnapStakMobile.Platforms.Android;

// ── SnapStakJsInterface ──────────────────────────────────────────────────
// Registered once via addJavascriptInterface in ConnectHandler.
// Persists across ALL page navigations for the WebView's lifetime.
// Exposes getScript() to JavaScript so onPageFinished can bootstrap
// content.js into every new page context via eval().
[Register("SnapStakJsInterface")]
public class SnapStakJsInterface : Java.Lang.Object
{
    [JavascriptInterface]
    [Export("getScript")]
    public string GetScript()
    {
        return LocalAssetWebViewHandler.PendingEngineScript ?? string.Empty;
    }
}

public class LocalAssetWebViewHandler : WebViewHandler
{
    // ── Statics set by DeconstructPage before handler is created ─────────────
    public static WebViewAssetLoader? PendingAssetLoader { get; set; }
    public static string? PendingAssetUrl { get; set; }

    // ── Engine script injected on every page load ────────────────────────────
    // Set by DeconstructPage.OnNavigating before each page load.
    // LocalAssetWebViewClient.OnPageFinished injects it via evaluateJavascript()
    // which runs natively in the page JS context, bypassing CSP entirely.
    public static string? PendingEngineScript { get; set; }

    // ── Custom PropertyMapper ────────────────────────────────────────────────
    //
    // MAUI's SetVirtualView lifecycle (confirmed from source):
    //
    //   SetVirtualView():
    //     _firstRun = true
    //     base.SetVirtualView()        ← fires ALL mapper entries in order
    //       MapSource → no-op          (_firstRun guard makes ProcessSourceWhenReady return early)
    //       MapUserAgent
    //       MapWebViewClient           ← our override installs LocalAssetWebViewClient HERE
    //       MapWebChromeClient
    //       MapWebViewSettings
    //     _firstRun = false
    //     ProcessSourceWhenReady()     ← calls UpdateSource() DIRECTLY, bypassing the mapper
    //                                     → LoadUrl(about:blank) for null Source
    //
    // Our strategy:
    //   1. MapLocalWebViewClient   — installs the right WebViewClient during the mapper pass
    //   2. SetVirtualView override — after base completes, calls LoadUrl(pendingUrl) to
    //                                override the about:blank that UpdateSource just issued

    public static readonly IPropertyMapper<Microsoft.Maui.IWebView, IWebViewHandler> LocalMapper =
        new PropertyMapper<Microsoft.Maui.IWebView, IWebViewHandler>(Mapper)
        {
            // Override MapWebViewClient to install LocalAssetWebViewClient (or fall back
            // to MauiWebViewClient for remote-URL WebViews). This runs during the mapper
            // pass, so the client is wired BEFORE ProcessSourceWhenReady fires.
            [nameof(WebViewClient)] = MapLocalWebViewClient,
        };

    public LocalAssetWebViewHandler() : base(LocalMapper, CommandMapper) { }

    // ── MapLocalWebViewClient ────────────────────────────────────────────────
    private static void MapLocalWebViewClient(IWebViewHandler handler, Microsoft.Maui.IWebView webView)
    {
        if (handler is not LocalAssetWebViewHandler h || h.PlatformView == null)
            return;

        if (PendingAssetLoader != null)
        {
            h.PlatformView.SetWebViewClient(new LocalAssetWebViewClient(h, PendingAssetLoader));
            System.Diagnostics.Debug.WriteLine("[SnapStak] MapLocalWebViewClient: LocalAssetWebViewClient installed");
        }
        else
        {
            // Remote URL app — use SnapStakWebViewClient which extends MauiWebViewClient
            // and adds OnPageFinished injection of content.js, exactly like
            // LocalAssetWebViewClient does for local asset apps.
            h.PlatformView.SetWebViewClient(new SnapStakWebViewClient(h));
            System.Diagnostics.Debug.WriteLine("[SnapStak] MapLocalWebViewClient: SnapStakWebViewClient installed (remote)");
        }
    }

    // ── SetVirtualView ───────────────────────────────────────────────────────
    // After base.SetVirtualView runs all mappers and fires ProcessSourceWhenReady
    // (which calls UpdateSource → LoadUrl(about:blank) for null Source), we
    // immediately call LoadUrl with our asset URL. Both calls are synchronous on
    // the main thread; Android cancels the first navigation and honours the second.
    // The LocalAssetWebViewClient is already installed at this point, so
    // ShouldInterceptRequest fires correctly for all requests including the first.
    public override void SetVirtualView(Microsoft.Maui.IView view)
    {
        // Snapshot before base call — base may indirectly touch statics
        var pendingUrl = PendingAssetUrl;
        var pendingLoader = PendingAssetLoader;
        var isLocalAsset = pendingUrl != null && pendingLoader != null;

        base.SetVirtualView(view);
        // At this point: mapper ran (client installed), ProcessSourceWhenReady ran
        // (issued about:blank for local-asset apps). Now override with our URL.

        if (isLocalAsset && PlatformView != null)
        {
            // Consume statics — one-shot only
            PendingAssetUrl = null;
            PendingAssetLoader = null;
            System.Diagnostics.Debug.WriteLine($"[SnapStak] SetVirtualView: LoadUrl {pendingUrl}");
            PlatformView.LoadUrl(pendingUrl!);
        }
    }

    // ── ConnectHandler ───────────────────────────────────────────────────────
    protected override void ConnectHandler(global::Android.Webkit.WebView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.Settings.DomStorageEnabled = true;
        platformView.Settings.JavaScriptEnabled = true;
        platformView.Settings.MediaPlaybackRequiresUserGesture = false;
        platformView.Settings.CacheMode = CacheModes.NoCache;
        global::Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true);

        // Add the SnapStak JavascriptInterface once — persists across all navigations.
        platformView.AddJavascriptInterface(new SnapStakJsInterface(), "__SnapStak");

        // Install our WebViewClient HERE in ConnectHandler — this fires only once
        // and cannot be overwritten by MAUI's internal mapper re-invocations.
        // MapLocalWebViewClient fires during the mapper pass but MAUI can replace
        // the client again on subsequent navigations. ConnectHandler is permanent.
        if (PendingAssetLoader != null)
            platformView.SetWebViewClient(new LocalAssetWebViewClient(this, PendingAssetLoader));
        else
            platformView.SetWebViewClient(new SnapStakWebViewClient(this));
    }
}

// ── SnapStakWebViewClient ─────────────────────────────────────────────────
// Used for remote URL apps. Extends MauiWebViewClient and adds OnPageFinished
// injection of content.js via native evaluateJavascript().
public class SnapStakWebViewClient : MauiWebViewClient
{
    public SnapStakWebViewClient(WebViewHandler handler) : base(handler) { }

    public override void OnPageFinished(
        global::Android.Webkit.WebView? view, string? url)
    {
        base.OnPageFinished(view, url);
    }

    // ── ShouldInterceptRequest ────────────────────────────────────────────
    // For remote URL apps: intercept the main-frame HTML request, fetch it
    // ourselves, STRIP the Content-Security-Policy header (which blocks both
    // inline scripts and eval()), inject content.js as a <script> tag, and
    // return the modified response. Without CSP the script executes freely.
    //
    // This is the definitive solution — every other approach (evaluateJavascript,
    // loadUrl javascript:, eval via addJavascriptInterface) is blocked by CSP.
    // Stripping CSP from the response is the only reliable method.
    public override WebResourceResponse? ShouldInterceptRequest(
        global::Android.Webkit.WebView? view, IWebResourceRequest? request)
    {
        if (request == null) return base.ShouldInterceptRequest(view, request);

        // Only intercept main-frame GET requests
        if (!request.IsForMainFrame ||
            !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return base.ShouldInterceptRequest(view, request);

        // Read PendingEngineScript — if null, try reading directly from
        // EngineService. This handles the race where ShouldInterceptRequest
        // fires before OnNavigating has had a chance to set PendingEngineScript.
        string? engineScript = null;
        try { engineScript = App.Engine.Read(); }
        catch (Exception _engEx)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak] SnapStakWebViewClient Engine.Read() failed: {_engEx.Message}");
        }
        if (string.IsNullOrEmpty(engineScript))
            engineScript = LocalAssetWebViewHandler.PendingEngineScript;
        if (string.IsNullOrEmpty(engineScript))
        {
            System.Diagnostics.Debug.WriteLine("[SnapStak] SnapStakWebViewClient: no engine script — skipping injection");
            return base.ShouldInterceptRequest(view, request);
        }

        try
        {
            // AutomaticDecompression ensures gzip/brotli/deflate responses are
            // fully decompressed BEFORE we read the body as a string.
            // Without this, ReadAsStringAsync() returns corrupt compressed bytes
            // and the <head> search fails, breaking the injected HTML.
            using var client = new System.Net.Http.HttpClient(
                new System.Net.Http.HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 10,
                    AutomaticDecompression =
                        System.Net.DecompressionMethods.GZip |
                        System.Net.DecompressionMethods.Deflate |
                        System.Net.DecompressionMethods.Brotli,
                });
            client.Timeout = System.TimeSpan.FromSeconds(30);

            // Forward original request headers EXCEPT Accept-Encoding —
            // HttpClientHandler with AutomaticDecompression sets this itself.
            // Also forward cookies so the page loads in the correct auth state.
            foreach (var h in request.RequestHeaders ?? new Dictionary<string, string>())
            {
                if (h.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;
                try { client.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value); }
                catch { }
            }

            var response = client
                .GetAsync(request.Url!.ToString())
                .GetAwaiter().GetResult();

            // Only modify HTML responses
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return base.ShouldInterceptRequest(view, request);

            // ReadAsStringAsync now returns clean decompressed UTF-8 text
            string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            // Inject content.js as first script after <head>
            int headIdx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            int insertAt = headIdx >= 0
                ? html.IndexOf('>', headIdx) + 1
                : 0;
            html = html.Insert(insertAt,
                $"\n<script>\n{engineScript}\n</script>");

            System.Diagnostics.Debug.WriteLine(
                "[SnapStak] Engine injected + CSP stripped for: " + request.Url);

            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            var stream = new System.IO.MemoryStream(bytes);

            // Build response headers — copy all EXCEPT Content-Security-Policy.
            // Stripping CSP is what allows our injected script to execute.
            var headers = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var h in response.Headers)
            {
                if (h.Key.Equals("Content-Security-Policy",
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (h.Key.Equals("Content-Security-Policy-Report-Only",
                        StringComparison.OrdinalIgnoreCase)) continue;
                headers[h.Key] = string.Join(", ", h.Value);
            }
            // Ensure correct content type for the modified response
            headers["Content-Type"] = "text/html; charset=utf-8";

            return new WebResourceResponse(
                "text/html", "UTF-8",
                (int)response.StatusCode, "OK",
                headers, stream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SnapStak] ShouldInterceptRequest failed: {ex.Message}");
            return base.ShouldInterceptRequest(view, request);
        }
    }
}

public class LocalAssetWebViewClient : MauiWebViewClient
{
    private readonly WebViewAssetLoader _loader;

    // ── JS blocking ───────────────────────────────────────────────────────────
    // We only need HTML + CSS to render the UI for extraction.
    // Framework JS (React/Capacitor/Cordova bundles, polyfills, runtime loaders)
    // causes fetch() failures, API calls, auth redirects, and crashes that prevent
    // the DOM reaching a usable state. Block it entirely.
    //
    // Strategy:
    //   - Framework/vendor bundles  → empty JS stub (so the browser doesn't error)
    //   - App page JS               → allow (handles component rendering & interactivity)
    //   - Native bridge JS          → empty stub (no native APIs available anyway)
    //   - Everything else           → allow (HTML, CSS, images, fonts, maps)
    //
    // "Empty JS stub" = a valid JS response with no code, not a 404.
    // This prevents module-not-found errors from blocking the HTML parse.

    // Exact filenames that are always framework infrastructure — stub regardless of path
    private static readonly HashSet<string> FrameworkFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Capacitor / Cordova bridge — stubbed because we inject our own stub via index.html
        "native-bridge.js",
        "capacitor.js",
        "cordova.js",
        "cordova_plugins.js",

        // Unresolvable React standalone builds (not needed when vendor.js has React)
        "react.js",
        "react.development.js",
        "react.production.min.js",
        "react-dom.js",
        "react-dom.development.js",
        "react-dom.production.min.js",
    };

    // Filename prefix patterns for hashed build output — matched against filename only
    // e.g. vendor.abc123.js, vendor.abc123.chunk.js, main.abc123.js
    private static readonly string[] FrameworkPrefixes =
    {
        "polyfill",       // browser polyfills — safe to stub
        "framework.",     // Ionic framework bundle
        "chunk-vendors.", // Vue CLI vendor chunk
    };

    public LocalAssetWebViewClient(WebViewHandler handler, WebViewAssetLoader loader)
        : base(handler)
    {
        _loader = loader;
    }

    public override WebResourceResponse? ShouldInterceptRequest(
        global::Android.Webkit.WebView? view,
        IWebResourceRequest? request)
    {
        if (request?.Url == null)
            return base.ShouldInterceptRequest(view, request);

        var url = request.Url;

        // Only intercept requests to our asset domain — pass everything else
        // (remote URLs, data: URIs, etc.) to the default handler unchanged.
        if (!string.Equals(url.Host, "appassets.androidplatform.net",
                StringComparison.OrdinalIgnoreCase))
        {
            return base.ShouldInterceptRequest(view, request);
        }

        // We own this domain. Never let it reach the network.

        // Check if this JS file should be stubbed
        if (ShouldStubJs(url.Path ?? ""))
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak] Stubbed JS: {url.Path}");
            return EmptyJsResponse();
        }

        // Ask the asset loader to serve from disk
        var response = _loader.ShouldInterceptRequest(url);
        if (response != null)
        {
            var path = url.Path ?? "";

            // Inject Capacitor stub + engine before any app JS runs.
            // Match /index.html AND root path / (served as index.html).
            if (path.EndsWith("index.html", StringComparison.OrdinalIgnoreCase)
                || path == "/" || string.IsNullOrEmpty(path))
                return InjectCapacitorStubAndEngine(response);

            // Patch vendor.js at serve time — replace Capacitor's internal
            // unimplemented() throw with a silent Promise.resolve({}).
            // File-level patching via regex failed because the method body
            // contains nested braces. Simple string replacement on the known
            // error message is reliable and version-independent.
            if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                return PatchJsResponse(response);

            return response;
        }

        // File not found on disk — return 404, never fall through to network
        System.Diagnostics.Debug.WriteLine($"[SnapStak] Asset not found: {url.Path}");
        return NotFoundResponse();
    }

    private static bool ShouldStubJs(string urlPath)
    {
        // Only act on .js files
        if (!urlPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            return false;

        // Extract just the filename from the path
        string fileName = urlPath.Contains('/')
            ? urlPath[(urlPath.LastIndexOf('/') + 1)..]
            : urlPath;

        // Exact filename match
        if (FrameworkFileNames.Contains(fileName))
            return true;

        // Prefix match against filename
        foreach (var prefix in FrameworkPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static WebResourceResponse PatchJsResponse(WebResourceResponse original)
    {
        try
        {
            string js;
            using (var reader = new System.IO.StreamReader(original.Data!))
                js = reader.ReadToEnd();

            // Capacitor's WebPlugin.unimplemented() throws with this exact message.
            // Replace the throw statement that contains it with a silent return.
            // This is a simple string search — no regex, no brace counting.
            // The throw appears as:  throw this.cap.Exception.fromCodes(...,"Not implemented on web.")
            // or:                    throw new X(...,"Not implemented on web.")
            // We replace the entire statement up to the semicolon or closing brace.
            bool patched = false;

            const string marker = "Not implemented on web.";
            if (js.Contains(marker))
            {
                // Walk backwards from each occurrence of the marker to find
                // the start of the throw statement, then replace to the end.
                var sb = new System.Text.StringBuilder();
                int searchFrom = 0;
                int markerIdx;

                while ((markerIdx = js.IndexOf(marker, searchFrom, StringComparison.Ordinal)) >= 0)
                {
                    // Find the start of the throw keyword before this marker
                    int throwIdx = js.LastIndexOf("throw", markerIdx, StringComparison.Ordinal);
                    if (throwIdx < 0 || markerIdx - throwIdx > 200)
                    {
                        // No nearby throw — skip
                        sb.Append(js[searchFrom..(markerIdx + marker.Length)]);
                        searchFrom = markerIdx + marker.Length;
                        continue;
                    }

                    // Find end of statement: closing " or ' after marker, then ; or }
                    int quoteEnd = js.IndexOfAny(new[] { '"', '\'' }, markerIdx + marker.Length);
                    if (quoteEnd < 0) { searchFrom = markerIdx + marker.Length; continue; }

                    // Find the closing paren+semicolon or just the next } after the throw
                    int stmtEnd = js.IndexOfAny(new[] { ';', '}' }, quoteEnd);
                    if (stmtEnd < 0) { searchFrom = markerIdx + marker.Length; continue; }

                    // Replace: everything from throwIdx to stmtEnd (exclusive of terminator)
                    sb.Append(js[searchFrom..throwIdx]);
                    sb.Append("return Promise.resolve({})");
                    searchFrom = stmtEnd; // keep the ; or }
                    patched = true;
                }

                sb.Append(js[searchFrom..]);
                if (patched)
                {
                    js = sb.ToString();
                    System.Diagnostics.Debug.WriteLine("[SnapStak] Patched: unimplemented throw replaced");
                }
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(js);
            var stream = new System.IO.MemoryStream(bytes);
            return new WebResourceResponse("application/javascript", "UTF-8", stream);
        }
        catch
        {
            return original;
        }
    }

    private static WebResourceResponse InjectCapacitorStubAndEngine(WebResourceResponse original)
    {
        try
        {
            string html;
            using (var reader = new System.IO.StreamReader(original.Data!))
                html = reader.ReadToEnd();

            // Inject both stubs as the very first script inside <head>.
            // Each stub guards with if(window.X) return — completely harmless
            // if the app does not use that runtime.
            // Covers: Capacitor (v3/v4/v5), Ionic+Capacitor, Cordova, PhoneGap, Ionic+Cordova
            // PWA/TWA apps need no stubs — they are pure web with no native bridge.
            // Consume the pending engine script (one-shot per page load).
            // Injecting content.js here — directly into the HTML byte stream —
            // is the ONLY reliable method. evaluateJavascript() runs after page
            // context is established and has CSP/timing issues on remote pages.
            // Injecting into the stream means the browser executes it as part
            // of the page itself: same origin, no CSP, no race conditions.
            string engineInjection = string.Empty;
            // Read engine from PendingEngineScript first, fall back to
            // App.Engine.Read() directly — this eliminates the timing race
            // where ShouldInterceptRequest fires before FetchEngineAsync
            // has set PendingEngineScript.
            // Always prefer App.Engine.Read() directly — it is the single
            // source of truth. PendingEngineScript is a convenience copy
            // but timing races mean it may be null when ShouldInterceptRequest
            // fires. App.Engine.Read() is always available once FetchAsync
            // has completed in CreateAsync.
            string? engineScript = null;
            try { engineScript = App.Engine.Read(); }
            catch (Exception _engEx)
            {
                System.Diagnostics.Debug.WriteLine($"[SnapStak] Engine.Read() failed: {_engEx.Message}");
            }
            if (string.IsNullOrEmpty(engineScript))
                engineScript = LocalAssetWebViewHandler.PendingEngineScript;
            LocalAssetWebViewHandler.PendingEngineScript = null;
            if (!string.IsNullOrEmpty(engineScript))
            {
                engineInjection = $"\n<script>\n{engineScript}\n</script>";
                System.Diagnostics.Debug.WriteLine("[SnapStak] Engine injected into HTML stream");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[SnapStak] WARNING: Engine not ready — App.Engine.IsLoaded=" + App.Engine.IsLoaded);
            }

            // Strip CSP meta tags — they block inline script execution
            // just like the CSP HTTP header does.
            html = System.Text.RegularExpressions.Regex.Replace(
                html,
                @"<meta[^>]*http-equiv\s*=\s*[""']?Content-Security-Policy[""']?[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            const string headTag = "<head>";
            int headIdx = html.IndexOf(headTag, StringComparison.OrdinalIgnoreCase);
            if (headIdx >= 0)
            {
                int insertAt = headIdx + headTag.Length;
                string injection = $"\n<script>{CapacitorStubScript}</script>"
                                 + $"\n<script>{CordovaStubScript}</script>"
                                 + engineInjection;
                html = html.Insert(insertAt, injection);
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            var stream = new System.IO.MemoryStream(bytes);
            return new WebResourceResponse("text/html", "UTF-8", stream);
        }
        catch
        {
            return original;
        }
    }

    // Cordova / PhoneGap stub — prevents "cordova is not defined" and fires
    // the deviceready event that all Cordova apps wait for before calling APIs.
    // cordova.exec() stubs the plugin bridge — all plugin calls succeed silently.
    // window.PhoneGap alias covers legacy apps built with Adobe PhoneGap.
    private const string CordovaStubScript = @"(function(){
if(window.cordova)return;
window.cordova={
version:'12.0.0',
platformId:'android',
platformVersion:'12.0.0',
require:function(){return{};},
define:function(){},
exec:function(success,fail,svc,action,args){if(typeof success==='function')success({});return true;},
fireDocumentEvent:function(type,data){
var e=document.createEvent('Events');
e.initEvent(type,false,false);
if(data)Object.assign(e,data);
document.dispatchEvent(e);
}
};
window.PhoneGap=window.cordova;
function fireDeviceReady(){
console.log('[SnapStak] Cordova stub: firing deviceready');
window.cordova.fireDocumentEvent('deviceready',{});
}
if(document.readyState==='complete'||document.readyState==='interactive'){
setTimeout(fireDeviceReady,0);
}else{
document.addEventListener('DOMContentLoaded',function(){setTimeout(fireDeviceReady,0);});
}
console.log('[SnapStak] Cordova stub installed');
})();";

    // Minimal window.Capacitor stub — prevents "Capacitor is not defined" errors
    // from rollback-fix.js and any other app code that references the Capacitor
    // runtime before checking if it exists. All plugin calls return Promise.resolve({})
    // so the app's async init chain completes without throwing.
    private const string CapacitorStubScript = @"(function(){
if(window.Capacitor)return;
window.Capacitor={
isNativePlatform:function(){return false;},
isPluginAvailable:function(){return false;},
getPlatform:function(){return 'web';},
platform:'web',
Plugins:new Proxy({},{get:function(_,p){return new Proxy({},{get:function(_,m){return function(){return Promise.resolve({});};}}); }}),
convertFileSrc:function(s){return s;},
registerPlugin:function(){return {};},
Exception:{fromCodes:function(){return new Error('Capacitor unavailable');}}
};
console.log('[SnapStak] Capacitor stub installed');
})();";

    private static WebResourceResponse EmptyJsResponse()
    {
        var emptyBytes = System.Text.Encoding.UTF8.GetBytes("/* blocked */");
        var stream = new System.IO.MemoryStream(emptyBytes);
        return new WebResourceResponse("application/javascript", "UTF-8", stream);
    }

    private static WebResourceResponse NotFoundResponse()
    {
        // Return an explicit 404 so the browser knows the file doesn't exist
        // rather than hanging waiting for a DNS resolution that will never happen.
        var emptyStream = new System.IO.MemoryStream(System.Array.Empty<byte>());
        return new WebResourceResponse("text/plain", "UTF-8", 404, "Not Found",
            new System.Collections.Generic.Dictionary<string, string>(), emptyStream);
    }

    // ── OnPageFinished: inject content.js natively after every page load ──────────
    //
    // evaluateJavascript() on the native android.webkit.WebView runs in the
    // page's JS context at the native layer — it completely bypasses Content
    // Security Policy. This is the correct injection point: the page has
    // finished loading so the DOM is ready, and the script is guaranteed to
    // execute before the user can tap Deconstruct.
    //
    // This mirrors exactly how the Chrome extension's content_scripts manifest
    // injects content.js at document_idle — after DOM ready, before user action.
    public override void OnPageFinished(
        global::Android.Webkit.WebView? view,
        string? url)
    {
        base.OnPageFinished(view, url);
        // Delegate to shared injection logic used by both client types.
        // Engine is injected via ShouldInterceptRequest HTML stream injection.
    }
}

public class CachePathHandler : Java.Lang.Object,
    WebViewAssetLoader.IPathHandler
{
    private readonly string _rootDir;

    public CachePathHandler(string rootDir)
    {
        _rootDir = rootDir;
    }

    public new WebResourceResponse? Handle(string? path)
    {
        if (path == null) return null;
        try
        {
            string relativePath = path.TrimStart('/');
            string fullPath = System.IO.Path.Combine(
                _rootDir,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

            if (!System.IO.File.Exists(fullPath)) return null;

            string mimeType = GetMimeType(fullPath);
            var stream = System.IO.File.OpenRead(fullPath);
            return new WebResourceResponse(mimeType, "UTF-8", stream);
        }
        catch { return null; }
    }

    private static string GetMimeType(string path) =>
        System.IO.Path.GetExtension(path).ToLower() switch
        {
            ".html" => "text/html",
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".map" => "application/json",
            _ => "application/octet-stream"
        };
}
#endif