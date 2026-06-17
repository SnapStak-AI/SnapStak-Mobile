#if ENTERPRISE
using SnapStakMobile.Services;
#else
using SnapStakMobile.Pipeline;
#endif

namespace SnapStakMobile.Views;

public partial class TransformPage : ContentPage
{
    private readonly string _componentId;
    private readonly string _appName;

#if ENTERPRISE
    private readonly DeconstructPage _deconstructPage;
    private readonly EnterpriseConteXService _enterprise;
    private CancellationTokenSource? _sseCts;
#else
    private readonly MobilePipelineResult? _pipelineResult;
#endif

    private string? _selectedCategory;
    private string? _selectedFramework;

    private readonly List<Border> _webCards = new();
    private readonly List<Border> _nativeCards = new();

#if ENTERPRISE
    public TransformPage(string componentId, string appName, DeconstructPage deconstructPage)
    {
        _componentId     = componentId;
        _appName         = appName;
        _deconstructPage = deconstructPage;
        _enterprise      = MauiProgram.Services!.GetRequiredService<EnterpriseConteXService>();
        InitializeComponent();
        AppNameLabel.Text = appName;
        InitCards();
    }
#else
    public TransformPage(string componentId, string appName, MobilePipelineResult? pipelineResult = null)
    {
        _componentId = componentId;
        _appName = appName;
        _pipelineResult = pipelineResult;
        InitializeComponent();
        AppNameLabel.Text = appName;
        InitCards();
        if (pipelineResult?.PluginOutputs?.Count > 0)
            ShowPluginExportSection(pipelineResult.PluginOutputs);
    }
#endif

    private void InitCards()
    {
        _webCards.AddRange(new[] { FwReact, FwNextjs, FwVue, FwNuxt, FwAngular, FwSvelte, FwTailwind, FwQwik, FwAstro, FwEmber, FwSolid });
        _nativeCards.AddRange(new[] { FwReactNative, FwFlutter, FwSwiftUI, FwJetpackCompose, FwNETMAUI });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ENTERPRISE
        _sseCts?.Cancel();
        _sseCts?.Dispose();
        _sseCts = null;
#endif
    }

    // ── Category / framework selection (identical in both builds) ─────────────

    private void OnWebViewTapped(object sender, TappedEventArgs e) => SelectCategory("web");
    private void OnNativeTapped(object sender, TappedEventArgs e) => SelectCategory("native");

    private void SelectCategory(string cat)
    {
        _selectedCategory = cat; _selectedFramework = null;
        var isWeb = cat == "web";
        SetCategoryActive(BtnWebView, WebViewTitle, WebViewChevron, isWeb);
        SetCategoryActive(BtnNative, NativeTitle, NativeChevron, !isWeb);
        WebFrameworks.IsVisible = isWeb;
        NativeFrameworks.IsVisible = !isWeb;
        SectionLabel.Text = "SELECT FRAMEWORK"; SectionLabel.IsVisible = true;
        foreach (var c in _webCards.Concat(_nativeCards)) SetCardSelected(c, false);
        ResetGenerateButton();
    }

    private static void SetCategoryActive(Border btn, Label title, Label chevron, bool active)
    {
        btn.BackgroundColor = active ? Color.FromArgb("#1A1A2E") : Color.FromArgb("#13131A");
        btn.Stroke = active ? new SolidColorBrush(Color.FromArgb("#F5A623")) : new SolidColorBrush(Color.FromArgb("#2A2A3D"));
        btn.StrokeThickness = active ? 1.5 : 1;
        title.TextColor = active ? Color.FromArgb("#F5A623") : Color.FromArgb("#F0F0FF");
        chevron.TextColor = active ? Color.FromArgb("#F5A623") : Color.FromArgb("#7878A0");
    }

    private void OnFrameworkTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not string fw) return;
        _selectedFramework = fw;
        foreach (var c in _webCards.Concat(_nativeCards)) SetCardSelected(c, false);
        if (sender is Border tapped) SetCardSelected(tapped, true);
        BtnGenerate.Text = $"Generate with {fw}";
        BtnGenerate.BackgroundColor = Color.FromArgb("#F5A623");
        BtnGenerate.TextColor = Color.FromArgb("#060709");
        BtnGenerate.IsEnabled = true;
    }

    private static void SetCardSelected(Border card, bool selected)
    {
        card.BackgroundColor = selected ? Color.FromArgb("#1E1500") : Color.FromArgb("#13131A");
        card.Stroke = selected ? new SolidColorBrush(Color.FromArgb("#F5A623")) : new SolidColorBrush(Color.FromArgb("#2A2D35"));
        card.StrokeThickness = selected ? 1.5 : 1;
    }

    private void ResetGenerateButton()
    {
        BtnGenerate.Text = "Select a framework to generate";
        BtnGenerate.BackgroundColor = Color.FromArgb("#2A2A3D");
        BtnGenerate.TextColor = Color.FromArgb("#4A4A6A");
        BtnGenerate.IsEnabled = false;
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    private async void OnGenerateClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedFramework) || string.IsNullOrEmpty(_selectedCategory)) return;
        BtnGenerate.IsEnabled = false;
        BtnGenerate.Text = "Starting...";

        try
        {
            var userUuid = await SecureStorage.Default.GetAsync("snapstakUserUID") ?? "anonymous";

#if ENTERPRISE

            _sseCts?.Cancel();
            _sseCts = new CancellationTokenSource();
            _ = Task.Run(() => ListenToSessionSseAsync(userUuid, _sseCts.Token), _sseCts.Token);
            await Task.Delay(300);

            ShowProcessingState(0, 0, "Starting Enterprise pipeline...");

            int total = await _enterprise.StartProcessSessionAsync(
                userUuid, NormaliseFrameworkKey(_selectedFramework), _selectedCategory);

            MainThread.BeginInvokeOnMainThread(() =>
                ShowProcessingState(0, total, $"Processing {total} component{(total == 1 ? "" : "s")}..."));

#else

            // Standalone: OpenRouter key comes from the pairing token — never entered manually
            var apiKey = await SnapStakMobile.Services.PairingService.GetOpenRouterKeyAsync();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ResetToGenerateReady();
                await DisplayAlert("Not Paired",
                    "Pair this device with SnapStak Desktop to enable code generation.", "OK");
                return;
            }

            // ── OpenRouter credit check ───────────────────────────────────────
            BtnGenerate.Text = "Checking credits...";
            var (credits, creditError) = await CheckOpenRouterCreditsAsync(apiKey);

            if (creditError != null)
            {
                ResetToGenerateReady();
                await DisplayAlert("API Key Invalid",
                    "Your OpenRouter API key was rejected. Check it in Settings.", "OK");
                return;
            }

            if (credits <= 0)
            {
                ResetToGenerateReady();
                bool topUp = await DisplayAlert("No Credits Remaining",
                    "You have no OpenRouter credits left. Top up your balance to continue generating code.",
                    "Top up ↗", "Cancel");
                if (topUp)
                    await Launcher.Default.OpenAsync(new Uri("https://openrouter.ai/credits"));
                return;
            }

            if (credits < 1.0)
            {
                bool proceed = await DisplayAlert("Low Balance",
                    $"Your OpenRouter balance is ${credits:F2}. A complex page costs ~$0.30. Continue?",
                    "Continue", "Top up ↗");
                if (!proceed)
                {
                    await Launcher.Default.OpenAsync(new Uri("https://openrouter.ai/credits"));
                    ResetToGenerateReady();
                    return;
                }
            }

            ShowProcessingState(0, 0, "Connecting...");

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                componentId = _componentId,
                framework = NormaliseFrameworkKey(_selectedFramework),
                platformType = _selectedCategory,
                styleOutput = "css",
                language = "js",
                apiKey = apiKey,
            });

            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                    await SecureStorage.Default.GetAsync("snapstakApiKey") ?? string.Empty);
            client.DefaultRequestHeaders.Add("X-User-UUID", userUuid);
            client.DefaultRequestHeaders.Add("X-ConteX-Client", "maui-contex");

            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://subscriptions.snapstak.ai/api/generate", content);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                ResetToGenerateReady();
                await DisplayAlert("Generation Failed", $"Server returned {(int)response.StatusCode}: {body}", "OK");
                return;
            }

            var resultJson = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
            var downloadToken = doc.RootElement.TryGetProperty("downloadToken", out var dt) ? dt.GetString() ?? "" : "";
            var count = doc.RootElement.TryGetProperty("componentCount", out var cc) ? cc.GetInt32() : 1;

            ShowCompleteState(count, _selectedFramework, downloadToken);

#endif
        }
        catch (Exception ex)
        {
            ResetToGenerateReady();
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

#if ENTERPRISE

    // ── Enterprise SSE listener ───────────────────────────────────────────────

    private async Task ListenToSessionSseAsync(string userUuid, CancellationToken ct)
    {
        await _enterprise.ListenToSessionSseAsync(userUuid, evt =>
        {
            switch (evt.Type)
            {
                case "COMPONENT_COMPLETE":
                    MainThread.BeginInvokeOnMainThread(() =>
                        ShowProcessingState(evt.Index, evt.Total, $"Generated: {evt.Label}"));
                    break;
                case "SESSION_COMPLETE":
                    _sseCts?.Cancel();
                    MainThread.BeginInvokeOnMainThread(() =>
                        ShowCompleteState(evt.ComponentCount, evt.Framework ?? _selectedFramework ?? "", evt.DownloadToken ?? ""));
                    break;
                case "COMPONENT_ERROR":
                    System.Diagnostics.Debug.WriteLine($"[Enterprise] Error [{evt.Label}]: {evt.Error}");
                    break;
                case "SESSION_ERROR":
                    _sseCts?.Cancel();
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        ResetToGenerateReady();
                        _ = DisplayAlert("Enterprise Error", evt.Error ?? "Pipeline failed.", "OK");
                    });
                    break;
            }
        }, ct);
    }

#else

    // ── Standalone: plugin export share sheet ─────────────────────────────────

    private void ShowPluginExportSection(List<MobilePluginOutput> outputs)
    {
        // The SnapStak SVG is the internal CON10X Structure pillar — core IP.
        // It is generated and stored on-device for the pipeline but must never
        // be exposed to the user via the share sheet.
        var visibleOutputs = outputs
            .Where(o => o.PluginKey != "snapstak-svg")
            .ToList();

        if (visibleOutputs.Count == 0) return;

        // Encrypt and enqueue all plugin outputs so Desktop can pull via Sync.
        _ = Task.Run(async () =>
        {
            foreach (var output in visibleOutputs)
            {
                try
                {
                    if (!System.IO.File.Exists(output.FilePath)) continue;
                    var plainBytes = await System.IO.File.ReadAllBytesAsync(output.FilePath);
                    var encryptedBytes = await SnapStakMobile.Services.PairingService.EncryptFileAsync(plainBytes);
                    var syncFilename = System.IO.Path.GetFileName(output.FilePath);
                    SnapStakMobile.Services.PairingService.EnqueueForSync(syncFilename, encryptedBytes);
                    System.Diagnostics.Debug.WriteLine($"[Sync] Queued '{syncFilename}' ({encryptedBytes.Length} bytes)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Sync] Failed to queue '{output.FilePath}': {ex.Message}");
                }
            }
        });
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CompletePanel.IsVisible = true;
            CompleteTitle.Text = $"✓ {visibleOutputs.Count} export file{(visibleOutputs.Count == 1 ? "" : "s")} ready";
            CompleteDetail.Text = $"{_appName} · {visibleOutputs.Count} plugin{(visibleOutputs.Count == 1 ? "" : "s")}";
            BtnDownload.IsVisible = false;

            foreach (var output in visibleOutputs)
            {
                var fileSizeKb = output.FileSizeBytes / 1024.0;
                var btn = new Button
                {
                    Text = $"Share {output.DisplayName} ({fileSizeKb:F0} KB)",
                    BackgroundColor = Color.FromArgb("#1A2A3A"),
                    TextColor = Color.FromArgb("#38A3F8"),
                    BorderColor = Color.FromArgb("#2A3A4A"),
                    BorderWidth = 0.5,
                    FontSize = 13,
                    CornerRadius = 10,
                    HeightRequest = 46,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                var capturedOutput = output;
                btn.Clicked += async (s, e) => await SharePluginOutputAsync(capturedOutput, btn);
                var panel = (VerticalStackLayout)CompletePanel.Content!;
                var idx = panel.Children.IndexOf(BtnDownload);
                panel.Children.Insert(idx, btn);
            }
        });
    }

    private async Task SharePluginOutputAsync(MobilePluginOutput output, Button btn)
    {
        if (!System.IO.File.Exists(output.FilePath))
        {
            await DisplayAlert("File not found", output.FilePath, "OK");
            return;
        }
        try
        {
            btn.IsEnabled = false;
            btn.Text = "Sharing...";
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = $"SnapStak — {_appName}{output.FileExtension}",
                File = new ShareFile(output.FilePath),
            });
            btn.Text = $"✓ {output.DisplayName}";
            btn.BackgroundColor = Color.FromArgb("#0A2010");
            btn.BorderColor = Color.FromArgb("#1A4020");
            btn.TextColor = Color.FromArgb("#44FF88");
        }
        catch (Exception ex)
        {
            btn.IsEnabled = true;
            btn.Text = $"Share {output.DisplayName}";
            await DisplayAlert("Share failed", ex.Message, "OK");
        }
    }

#endif

    // ── Download (both builds) ────────────────────────────────────────────────

    private async void OnDownloadClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn) return;
        string? token = btn.CommandParameter as string;
        if (string.IsNullOrEmpty(token)) return;

        btn.IsEnabled = false;
        btn.Text = "Downloading...";

        try
        {
            var userUuid = await SecureStorage.Default.GetAsync("snapstakUserUID") ?? "anonymous";

#if ENTERPRISE
            var bytes    = await _enterprise.DownloadPackageAsync(token, userUuid);
#else
            var apiKey = await SecureStorage.Default.GetAsync("snapstakApiKey");
            using var cl = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            if (!string.IsNullOrEmpty(apiKey))
                cl.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            cl.DefaultRequestHeaders.Add("X-User-UUID", userUuid);
            var resp = await cl.GetAsync($"https://subscriptions.snapstak.ai/api/download/session?token={token}");
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Download failed ({(int)resp.StatusCode})");
            var bytes = await resp.Content.ReadAsByteArrayAsync();
#endif
            var fileName = $"{_appName.ToLowerInvariant().Replace(" ", "-")}-package.zip";
#if ANDROID
            var destPath = System.IO.Path.Combine(
                Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath,
                fileName);
            await System.IO.File.WriteAllBytesAsync(destPath, bytes);
            btn.Text = "✓ Saved to Downloads";
            await DisplayAlert("Download Complete", $"Saved as {fileName}", "OK");
#else
            btn.Text = "✓ Downloaded";
#endif
        }
        catch (Exception ex)
        {
            btn.IsEnabled = true;
            btn.Text = "Download package.zip";
            await DisplayAlert("Download Failed", ex.Message, "OK");
        }
    }

    // ── UI state helpers ──────────────────────────────────────────────────────

    private void ShowProcessingState(int done, int total, string detail)
    {
        BtnGenerate.IsEnabled = false;
        BtnGenerate.Text = total > 0 ? $"Processing {done}/{total}..." : "Processing...";
        ProcessingPanel.IsVisible = true;
        ProcessingDetail.Text = detail;
        ProcessingProgress.Progress = total > 0 ? (double)done / total : 0;
        CompletePanel.IsVisible = false;
    }

    private void ShowCompleteState(int count, string framework, string downloadToken)
    {
        ProcessingPanel.IsVisible = false;
        CompletePanel.IsVisible = true;
        CompleteTitle.Text = "✓ Ready to download";
        CompleteDetail.Text = $"{count} component{(count == 1 ? "" : "s")} · {framework}";
        BtnGenerate.Text = "✓ Complete";
        BtnGenerate.BackgroundColor = Color.FromArgb("#34C759");
        BtnGenerate.TextColor = Colors.White;
        BtnDownload.CommandParameter = downloadToken;
        BtnDownload.IsVisible = true;
    }

    private void ResetToGenerateReady()
    {
        ProcessingPanel.IsVisible = false;
        if (_selectedFramework is not null)
        {
            BtnGenerate.Text = $"Generate with {_selectedFramework}";
            BtnGenerate.BackgroundColor = Color.FromArgb("#F5A623");
            BtnGenerate.TextColor = Color.FromArgb("#060709");
            BtnGenerate.IsEnabled = true;
        }
        else ResetGenerateButton();
    }

    // ── OpenRouter credit check ──────────────────────────────────────────────

    private static readonly System.Net.Http.HttpClient _creditClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>
    /// Checks the OpenRouter credit balance for the given API key.
    /// Returns (remaining, null) on success or (0, errorMessage) on failure.
    /// </summary>
    private static async Task<(double credits, string? error)> CheckOpenRouterCreditsAsync(string apiKey)
    {
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get,
                "https://openrouter.ai/api/v1/credits");
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var resp = await _creditClient.SendAsync(req);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (0, "Invalid API key.");

            if (!resp.IsSuccessStatusCode)
                return (0, $"OpenRouter returned {(int)resp.StatusCode}.");

            var json = await resp.Content.ReadAsStringAsync();
            dynamic? obj = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
            double purchased = (double)(obj?.data?.total_credits ?? 0);
            double used = (double)(obj?.data?.total_usage ?? 0);
            double remaining = Math.Max(0, purchased - used);

            return (remaining, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak] Credit check failed: {ex.Message}");
            // Network failure — don't block the user, let the generation attempt proceed
            return (double.MaxValue, null);
        }
    }

    private static string NormaliseFrameworkKey(string framework) =>
        framework.ToLowerInvariant() switch
        {
            "react" => "react",
            "next.js" => "next",
            "vue" => "vue",
            "nuxt" => "nuxt",
            "angular" => "angular",
            "svelte" => "svelte",
            "tailwind" => "tailwind",
            "qwik" => "qwik",
            "astro" => "astro",
            "ember" => "ember",
            "solid" => "solid",
            "react native" => "react-native",
            "flutter" => "flutter",
            "swiftui" => "swiftui",
            "jetpack compose" => "jetpack-compose",
            ".net maui" => "maui",
            _ => framework.ToLowerInvariant(),
        };
}