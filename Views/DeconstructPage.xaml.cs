using Microsoft.Maui.Controls.Shapes;
using SnapStakMobile.Services;
#if !ENTERPRISE
using SnapStakMobile.Pipeline;
#endif

namespace SnapStakMobile.Views;

public partial class DeconstructPage : ContentPage
{
    private readonly string _appName;
    private readonly string _framework;
    private bool _pageLoaded = false;
    internal string? _engineScript = null;
    private string? _localFilePath;
    private string? _assetUrl;
    private string _componentId = string.Empty;

#if ENTERPRISE
    private EnterpriseConteXService? _enterprise;
    internal string? ExtractedJson { get; private set; }
#else
    private MobileConteXPipelineService? _pipeline;
    private MobilePipelineResult? _pipelineResult;
#endif

    private DeconstructPage(string url, string appName, string framework)
    {
        _appName   = appName;
        _framework = framework;

        if (url.StartsWith("file://", StringComparison.Ordinal))
        {
            _localFilePath = url.Substring(7);
            PrepareAssetLoader();
        }
        else
        {
            _localFilePath = null;
        }

        InitializeComponent();
        Title = appName;

        if (_localFilePath == null)
            TargetWebView.Source = url;
    }

    public static async Task<DeconstructPage> CreateAsync(
        string url, string appName, string framework = "WebView")
    {
        var engineService = MauiProgram.Services!.GetRequiredService<LocalEngineService>();
        string? engineScript = null;
        try
        {
            await engineService.EnsureLoadedAsync();
            engineScript = engineService.Read();
#if ANDROID
            SnapStakMobile.Platforms.Android.LocalAssetWebViewHandler.PendingEngineScript = engineScript;
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak] Engine load failed: {ex.Message}");
        }

        var page = new DeconstructPage(url, appName, framework);
        page._engineScript = engineScript;

#if ENTERPRISE
        page._enterprise = MauiProgram.Services!.GetRequiredService<EnterpriseConteXService>();
#else
        page._pipeline   = MauiProgram.Services!.GetRequiredService<MobileConteXPipelineService>();
#endif
        return page;
    }

    private void PrepareAssetLoader()
    {
        if (_localFilePath == null) return;
#if ANDROID
        string indexDir  = System.IO.Path.GetDirectoryName(_localFilePath)!;
        string assetsDir = System.IO.Path.GetDirectoryName(indexDir)!;
        _assetUrl = "https://appassets.androidplatform.net/index.html";
        SnapStakMobile.Platforms.Android.LocalAssetWebViewHandler.PendingAssetLoader =
            new AndroidX.WebKit.WebViewAssetLoader.Builder()
                .SetDomain("appassets.androidplatform.net")!
                .AddPathHandler("/assets/", new SnapStakMobile.Platforms.Android.CachePathHandler(assetsDir))!
                .AddPathHandler("/",        new SnapStakMobile.Platforms.Android.CachePathHandler(indexDir))!
                .Build()!;
        SnapStakMobile.Platforms.Android.LocalAssetWebViewHandler.PendingAssetUrl = _assetUrl;
#endif
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void OnNavigating(object sender, WebNavigatingEventArgs e)
    {
        _pageLoaded = false;
        BtnDeconstruct.IsEnabled  = false;
        LoadingBar.IsVisible      = true;
        BuildingOverlay.IsVisible = true;
#if ANDROID
        if (_engineScript != null)
            SnapStakMobile.Platforms.Android.LocalAssetWebViewHandler.PendingEngineScript = _engineScript;
#endif
    }

    private void OnNavigated(object sender, WebNavigatedEventArgs e)
    {
        if (e.Url == "about:blank") return;
        LoadingBar.IsVisible      = false;
        BuildingOverlay.IsVisible = false;
        _pageLoaded = true;
        BtnDeconstruct.IsEnabled  = true;
    }

    private void OnReloadClicked(object sender, EventArgs e) => TargetWebView.Reload();

    // ── Popups ────────────────────────────────────────────────────────────────

    private void ShowError(string message)
    {
        BtnDeconstruct.IsEnabled = _pageLoaded;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ErrorMessage.Text    = message;
            ErrorPopup.IsVisible = true;
        });
    }

    private void ShowSuccess(string message)
    {
        BtnDeconstruct.IsEnabled = _pageLoaded;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            SuccessMessage.Text    = message;
            SuccessPopup.IsVisible = true;
        });
    }

    private void OnErrorPopupOkClicked(object sender, EventArgs e)
        => ErrorPopup.IsVisible = false;

    private void OnSuccessPopupDoneClicked(object sender, EventArgs e)
        => SuccessPopup.IsVisible = false;

    private async void OnSuccessTransformClicked(object sender, EventArgs e)
    {
        SuccessPopup.IsVisible = false;
#if ENTERPRISE
        var transformPage = new TransformPage(_componentId, _appName, this);
#else
        var transformPage = new TransformPage(_componentId, _appName, _pipelineResult);
#endif
        await Navigation.PushAsync(transformPage);
    }

    private void OnAuthPopupSignInClicked(object sender, EventArgs e) { }

    // ── Deconstruct ───────────────────────────────────────────────────────────

    private async void OnDeconstructClicked(object sender, EventArgs e)
    {
        if (!_pageLoaded) return;
        BtnDeconstruct.IsEnabled = false;

        string? jsonResult = null;
        try
        {
            long tsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string safeName = System.Text.RegularExpressions.Regex
                .Replace((_appName ?? "component").ToLowerInvariant(), @"[^a-z0-9]", "-").Trim('-');
            _componentId = $"{safeName}_{tsMs}";
            string componentIdJson = System.Text.Json.JsonSerializer.Serialize(_componentId);

            await TargetWebView.EvaluateJavaScriptAsync(
                "(function(){if(typeof chrome==='undefined'||!chrome.runtime){" +
                "window.chrome=window.chrome||{};" +
                "window.chrome.runtime={onMessage:{addListener:function(){},removeListener:function(){}},sendMessage:function(){}};" +
                "}})();");

            await TargetWebView.EvaluateJavaScriptAsync(
                "window.__snapstak_result__=null;" +
                ";(async function(){try{" +
                "if(typeof extractMobile==='undefined')throw new Error('Engine not injected');" +
                "var result=await extractMobile('visible');" +
                "result.componentId=" + componentIdJson + ";" +
                "result.url=window.location.href;" +
                "if(!result.meta)result.meta={};" +
                "result.meta.url=window.location.href;" +
                "result.meta.title=document.title;" +
                "result.meta.viewport={width:window.innerWidth,height:window.innerHeight};" +
                "window.__snapstak_result__=JSON.stringify(result);" +
                "}catch(err){window.__snapstak_result__=JSON.stringify({success:false,error:err.message});}" +
                "})();");

            int elapsed = 0;
            while (elapsed < 120_000)
            {
                await Task.Delay(500);
                elapsed += 500;
                string? polled = await TargetWebView.EvaluateJavaScriptAsync("window.__snapstak_result__");
                if (!string.IsNullOrWhiteSpace(polled) && polled != "null")
                {
                    jsonResult = polled;
                    await TargetWebView.EvaluateJavaScriptAsync("window.__snapstak_result__=null;");
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        ProgressOverlay.IsVisible = true;
#if ENTERPRISE
                        SetStep(2, "Uploading to Enterprise server...");
#else
                        SetStep(2, "Running CON10X pipeline...");
#endif
                    });
                    break;
                }
            }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
        catch (Exception ex)
        {
            ProgressOverlay.IsVisible = false;
            ShowError($"Extraction failed: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(jsonResult) || jsonResult == "null")
        {
            ProgressOverlay.IsVisible = false;
            ShowError("Extraction timed out. The page may be blocking script injection via CSP.");
            return;
        }

        string cleanJson = UnescapeJsString(jsonResult);
        jsonResult = null;

        if (cleanJson.Contains("\"success\":false"))
        {
            ProgressOverlay.IsVisible = false;
            ShowError($"Extraction failed in JavaScript: {cleanJson}");
            return;
        }

        // ── Post-extraction pipeline ──────────────────────────────────────────

        try
        {
            string userUuid = await GetUserUuidAsync();

#if ENTERPRISE

            var progress = new Progress<string>(msg =>
                MainThread.BeginInvokeOnMainThread(() => SetStep(2, msg)));

            var result = await _enterprise!.TransformAsync(cleanJson, userUuid, _framework, progress);
            ProgressOverlay.IsVisible = false;

            if (!result.Success)
            {
                ShowError($"Enterprise server error: {result.Error}");
                return;
            }

            ExtractedJson = cleanJson;
            SetStep(3, "Complete");
            ShowSuccess($"{_appName} deconstructed. Ready to generate.");

#else

            var progress = new Progress<MobilePipelineProgress>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SetStep(2, p.Message);
                    if (p.Percentage >= 100) SetStep(3, "Complete");
                });
            });

            _pipelineResult = await _pipeline!.TransformAsync(
                cleanJson, userUuid, _appName ?? string.Empty, progress);

            ProgressOverlay.IsVisible = false;

            if (!_pipelineResult.Success)
            {
                ShowError($"Pipeline error: {_pipelineResult.Error}");
                return;
            }

            _componentId = _pipelineResult.ComponentId;
            ShowSuccess($"{_appName} deconstructed. {_pipelineResult.ObjectCount} elements captured.");

#endif
        }
        catch (UnauthorizedAccessException ex)
        {
            ProgressOverlay.IsVisible = false;
            ShowError($"Authentication error: {ex.Message}");
        }
        catch (Exception ex)
        {
            ProgressOverlay.IsVisible = false;
            ShowError($"Pipeline error: {ex.Message}");
        }
    }

    // ── User UUID ─────────────────────────────────────────────────────────────

    private static async Task<string> GetUserUuidAsync()
    {
        try
        {
            var stored = await SecureStorage.Default.GetAsync("snapstakUserUID");
            if (!string.IsNullOrEmpty(stored)) return stored;
        }
        catch { }

        var deviceId = DeviceInfo.Current.Idiom.ToString()
                     + DeviceInfo.Current.Platform.ToString()
                     + DeviceInfo.Current.Name;
        using var sha  = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(deviceId));
        var uuid = new Guid(hash.Take(16).ToArray()).ToString();
        try { await SecureStorage.Default.SetAsync("snapstakUserUID", uuid); } catch { }
        return uuid;
    }

    // ── Step dots ─────────────────────────────────────────────────────────────

    private void SetStep(int step, string detail)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ProgressDetail.Text = detail;
            SetDot(Step1Dot, step >= 1);
            SetDot(Step2Dot, step >= 2);
            SetDot(Step3Dot, step >= 3);
        });
    }

    private static void SetDot(Ellipse dot, bool active)
    {
        dot.Fill            = active ? new SolidColorBrush(Color.FromArgb("#F5A623")) : new SolidColorBrush(Colors.Transparent);
        dot.Stroke          = new SolidColorBrush(Color.FromArgb(active ? "#F5A623" : "#2A2A3D"));
        dot.StrokeThickness = 1.5;
    }

    private static string UnescapeJsString(string input)
    {
        if (input.StartsWith('"') && input.EndsWith('"')) input = input[1..^1];
        return System.Text.RegularExpressions.Regex.Unescape(input);
    }
}
