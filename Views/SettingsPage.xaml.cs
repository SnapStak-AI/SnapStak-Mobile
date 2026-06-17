using Microsoft.Maui.Controls.Shapes;
using SnapStakMobile.Services;

namespace SnapStakMobile.Views;

public partial class SettingsPage : ContentPage
{
    private readonly MobilePluginSettingsService _pluginSettings;
    private MobilePluginSettings _settings = MobilePluginSettings.Defaults();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public SettingsPage(MobilePluginSettingsService pluginSettings)
    {
        _pluginSettings = pluginSettings;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        // Show/hide build-specific UI sections
#if ENTERPRISE
        ServerUrlPanel.IsVisible = true;
#else
        ServerUrlPanel.IsVisible = false;
#endif

        _settings = await _pluginSettings.LoadAsync();

#if ENTERPRISE
        try
        {
            var serverUrl = await SecureStorage.Default.GetAsync("enterpriseServerUrl");
            ServerUrlEntry.Text = serverUrl ?? AppConfig.DefaultEnterpriseServerUrl;
        }
        catch { ServerUrlEntry.Text = AppConfig.DefaultEnterpriseServerUrl; }
#endif

        PenpotSwitch.Toggled -= OnPenpotToggled;
        FigmaSwitch.Toggled -= OnFigmaToggled;
        CanvaSwitch.Toggled -= OnCanvaToggled;

        PenpotSwitch.IsToggled = _settings.PenpotEnabled;
        FigmaSwitch.IsToggled = _settings.FigmaEnabled;
        CanvaSwitch.IsToggled = _settings.CanvaEnabled;

        UpdatePluginDot(PenpotDot, PenpotPluginRow, _settings.PenpotEnabled);
        UpdatePluginDot(FigmaDot, FigmaPluginRow, _settings.FigmaEnabled);
        UpdatePluginDot(CanvaDot, CanvaPluginRow, _settings.CanvaEnabled);

        PenpotSwitch.Toggled += OnPenpotToggled;
        FigmaSwitch.Toggled += OnFigmaToggled;
        CanvaSwitch.Toggled += OnCanvaToggled;

        await RefreshSubscriptionStatusAsync();
        await FetchCreditsFromPairedKeyAsync();
    }

    // ── Subscription / server section ─────────────────────────────────────────

    // Always compiled — XAML references this handler regardless of build config.
    // Only has effect in the Enterprise build; no-op in Standalone.
    private async void OnSaveServerUrlClicked(object sender, EventArgs e)
    {
#if ENTERPRISE
        var url = (ServerUrlEntry.Text ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url)) url = AppConfig.DefaultEnterpriseServerUrl;
        try
        {
            await SecureStorage.Default.SetAsync("enterpriseServerUrl", url);
            ServerUrlSavedBanner.IsVisible = true;
            _ = HideBannerAfterDelayAsync(ServerUrlSavedBanner);
            await RefreshSubscriptionStatusAsync();
        }
        catch (Exception ex) { await DisplayAlert("Error", ex.Message, "OK"); }
#else
        await Task.CompletedTask; // no-op in Standalone build
#endif
    }

#if ENTERPRISE

    private async Task RefreshSubscriptionStatusAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{AppConfig.Current.ServerUrl}/health");
            req.Headers.Add("X-ConteX-Client", "maui-enterprise");
            using var resp = await _http.SendAsync(req);
            SubActiveBorder.IsVisible   = resp.IsSuccessStatusCode;
            SubInactiveBorder.IsVisible = !resp.IsSuccessStatusCode;
        }
        catch
        {
            SubActiveBorder.IsVisible   = false;
            SubInactiveBorder.IsVisible = true;
        }
    }

#else

    private async Task RefreshSubscriptionStatusAsync()
    {
        // Check pairing status first
        bool paired = await PairingService.IsPairedAsync();
        UnpairBtn.IsVisible = paired;

        // When paired, hide the PIN entry and pairing instructions entirely —
        // the user only needs to see their status and the unpair option.
        PinEntry.IsVisible = !paired;
        PairBtn.IsVisible = !paired;
        PairingStatusBorder.IsVisible = false;
        SubscribeRedirectBorder.IsVisible = !paired;

        if (!paired)
        {
            SubActiveBorder.IsVisible = false;
            SubInactiveBorder.IsVisible = true;
            SubInactiveLabel.Text = "Not paired — connect to SnapStak Desktop";
            return;
        }

        // Validate subscription against server
        var (active, error) = await PairingService.ValidateSubscriptionAsync();

        SubActiveBorder.IsVisible = active;
        SubInactiveBorder.IsVisible = !active;

        if (!active)
            SubInactiveLabel.Text = error ?? "Subscription not active.";
        else
            SubActiveLabel.Text = "✓ Paired with SnapStak Desktop";
    }

    // ── Pairing handlers ──────────────────────────────────────────────────────

    private CancellationTokenSource? _pairingCts;

    private async void OnPairClicked(object sender, EventArgs e)
    {
        var pin = (PinEntry.Text ?? string.Empty).Trim();
        if (pin.Length != 6 || !pin.All(char.IsDigit))
        {
            PairingStatusBorder.IsVisible = true;
            PairingStatusLabel.TextColor = Color.FromArgb("#FF8888");
            PairingStatusLabel.Text = "Please enter the 6-digit PIN shown on SnapStak Desktop.";
            return;
        }

        _pairingCts?.Cancel();
        _pairingCts = new CancellationTokenSource();

        PairBtn.IsEnabled = false;
        PairBtn.Text = "Pairing...";
        PinEntry.IsEnabled = false;
        PairingStatusBorder.IsVisible = true;
        PairingStatusLabel.TextColor = Color.FromArgb("#7878A0");
        PairingStatusLabel.Text = "Scanning for SnapStak Desktop on your network...";

        try
        {
            var progress = new Progress<string>(msg =>
                MainThread.BeginInvokeOnMainThread(() =>
                    PairingStatusLabel.Text = msg));

            await PairingService.PairWithDesktopAsync(pin, progress, _pairingCts.Token);

            // Success
            PairingStatusLabel.TextColor = Color.FromArgb("#44FF88");
            PairingStatusLabel.Text = "✓ Paired successfully. Validating subscription...";
            PairBtn.Text = "✓ Paired";

            await RefreshSubscriptionStatusAsync();
        }
        catch (OperationCanceledException)
        {
            PairingStatusLabel.TextColor = Color.FromArgb("#7878A0");
            PairingStatusLabel.Text = "Pairing cancelled.";
            PairBtn.Text = "Pair with SnapStak Desktop";
            PairBtn.IsEnabled = true;
            PinEntry.IsEnabled = true;
            PinEntry.Text = string.Empty;
        }
        catch (Exception ex)
        {
            PairingStatusLabel.TextColor = Color.FromArgb("#FF8888");
            PairingStatusLabel.Text = ex.Message;
            PairBtn.Text = "Pair with SnapStak Desktop";
            PairBtn.IsEnabled = true;
            PinEntry.IsEnabled = true;
            PinEntry.Text = string.Empty;
        }
    }

    private async void OnUnpairClicked(object sender, EventArgs e)
    {
        UnpairConfirmPopup.IsVisible = true;
    }

    private void OnUnpairCancelClicked(object sender, EventArgs e)
    {
        UnpairConfirmPopup.IsVisible = false;
    }

    private async void OnUnpairConfirmClicked(object sender, EventArgs e)
    {
        UnpairConfirmPopup.IsVisible = false;

        try
        {
            var (success, error) = await PairingService.UnpairAsync();

            if (!success)
            {
                UnpairErrorLabel.Text = error ?? "Unknown error.";
                UnpairErrorPopup.IsVisible = true;
                return;
            }

            // Both sides confirmed — update UI
            UnpairBtn.IsVisible = false;
            PinEntry.IsVisible = true;
            PairBtn.IsVisible = true;
            PairBtn.IsEnabled = true;
            PairBtn.Text = "Pair with SnapStak Desktop";
            PairingStatusBorder.IsVisible = false;
            SubscribeRedirectBorder.IsVisible = true;
            SubActiveBorder.IsVisible = false;
            SubInactiveBorder.IsVisible = true;
            SubInactiveLabel.Text = "Not paired — connect to SnapStak Desktop";

            UnpairSuccessPopup.IsVisible = true;
        }
        catch (Exception ex)
        {
            UnpairErrorLabel.Text = ex.Message;
            UnpairErrorPopup.IsVisible = true;
        }
    }

    private void OnUnpairErrorOkClicked(object sender, EventArgs e)
        => UnpairErrorPopup.IsVisible = false;

    private void OnUnpairSuccessOkClicked(object sender, EventArgs e)
        => UnpairSuccessPopup.IsVisible = false;

#endif
    private async Task FetchCreditsFromPairedKeyAsync()
    {
        var key = await PairingService.GetOpenRouterKeyAsync();
        if (string.IsNullOrWhiteSpace(key)) { CreditsPanel.IsVisible = false; return; }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/credits");
            req.Headers.Add("Authorization", $"Bearer {key!}");
            using var resp = await _http.SendAsync(req);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            { ShowCreditsPanel("#1A0A0A", "#440000", "Invalid API key.", "#FF8888"); return; }

            var json = await resp.Content.ReadAsStringAsync();
            dynamic? obj = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
            double purchased = (double)(obj?.data?.total_credits ?? 0);
            double used = (double)(obj?.data?.total_usage ?? 0);
            double remaining = Math.Max(0, purchased - used);

            if (remaining <= 0)
                ShowCreditsPanel("#1A0A0A", "#440000", "⛔ No credits remaining", "#FF8888");
            else if (remaining < 5)
                ShowCreditsPanel("#1A130A", "#443300", $"⚠ Low balance: ${remaining:F2}", "#CCAA88");
            else
                ShowCreditsPanel("#0A140A", "#1A3A1A", $"✓ Balance: ${remaining:F2} remaining", "#44FF88");
        }
        catch { ShowCreditsPanel("#1A0A0A", "#440000", "Could not reach OpenRouter.", "#FF8888"); }
    }

    private void ShowCreditsPanel(string bg, string border, string text, string textColor)
    {
        CreditsPanel.IsVisible = true;
        CreditsPanel.BackgroundColor = Color.FromArgb(bg);
        ((Border)CreditsPanel).Stroke = new SolidColorBrush(Color.FromArgb(border));
        CreditsLabel.Text = text;
        CreditsLabel.TextColor = Color.FromArgb(textColor);
    }

    // ── Plugin toggles ────────────────────────────────────────────────────────

    private async void OnPenpotToggled(object? sender, ToggledEventArgs e)
    { _settings.PenpotEnabled = e.Value; UpdatePluginDot(PenpotDot, PenpotPluginRow, e.Value); await SavePluginSettingsAsync(); }

    private async void OnFigmaToggled(object? sender, ToggledEventArgs e)
    { _settings.FigmaEnabled = e.Value; UpdatePluginDot(FigmaDot, FigmaPluginRow, e.Value); await SavePluginSettingsAsync(); }
    private async void OnCanvaToggled(object? sender, ToggledEventArgs e)
    { _settings.CanvaEnabled = e.Value; UpdatePluginDot(CanvaDot, CanvaPluginRow, e.Value); await SavePluginSettingsAsync(); }

    private async Task SavePluginSettingsAsync()
    {
        await _pluginSettings.SaveAsync(_settings);
        PluginSavedBanner.IsVisible = true;
        _ = HideBannerAfterDelayAsync(PluginSavedBanner);
    }

    private static void UpdatePluginDot(Ellipse dot, Border row, bool enabled)
    {
        dot.Fill = enabled ? new SolidColorBrush(Color.FromArgb("#44FF88")) : new SolidColorBrush(Color.FromArgb("#333333"));
        row.Opacity = enabled ? 1.0 : 0.45;
    }

    // ── Danger Zone ───────────────────────────────────────────────────────────

    private async void OnClearAllDataClicked(object sender, EventArgs e)
    {
        ClearDataConfirmPopup.IsVisible = true;
    }

    private void OnClearDataCancelClicked(object sender, EventArgs e)
        => ClearDataConfirmPopup.IsVisible = false;

    private async void OnClearDataConfirmClicked(object sender, EventArgs e)
    {
        ClearDataConfirmPopup.IsVisible = false;
        await _pluginSettings.ClearAllSessionDataAsync();
        ClearDataDonePopup.IsVisible = true;
    }

    private void OnClearDataDoneOkClicked(object sender, EventArgs e)
        => ClearDataDonePopup.IsVisible = false;

    private static async Task HideBannerAfterDelayAsync(VisualElement banner)
    {
        await Task.Delay(2500);
        MainThread.BeginInvokeOnMainThread(() => banner.IsVisible = false);
    }
}