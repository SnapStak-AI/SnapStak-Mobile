using SnapStakMobile.Services;

namespace SnapStakMobile.Views;

public partial class AppListPage : ContentPage
{
    private readonly AppDatabaseService _db;
    private readonly SignatureCacheService _sigCache = App.SignatureCache;

    public AppListPage(AppDatabaseService db)
    {
        InitializeComponent();
        _db = db;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await InitialiseAsync();
    }

    private async Task InitialiseAsync()
    {
        ShowLoading();
        try
        {
            if (!_sigCache.IsLoaded)
                await _sigCache.FetchAsync();

            int count = await _db.CountAsync();
            SavedCountLabel.Text = count == 0
                ? "No saved apps yet"
                : $"{count} saved app{(count == 1 ? "" : "s")}";

            ShowMain();
        }
        catch (Exception ex)
        {
            ShowError($"Could not load app signatures:\n{ex.Message}");
        }
    }

    // ── Subscription check ────────────────────────────────────────────────────

    private static async Task<bool> IsSubscribedAsync()
    {
        try
        {
            var (active, _) = await PairingService.ValidateSubscriptionAsync();
            return active;
        }
        catch { return false; }
    }

    // ── Tap animation ─────────────────────────────────────────────────────────

    private static async Task AnimateTap(View view)
    {
        await view.ScaleTo(0.96, 80, Easing.CubicIn);
        await view.ScaleTo(1.0, 120, Easing.SpringOut);
    }

    // ── Spinner helpers ───────────────────────────────────────────────────────

    private void ShowCardSpinner(ActivityIndicator spinner)
    {
        spinner.IsVisible = true;
        spinner.IsRunning = true;
    }

    private void HideCardSpinner(ActivityIndicator spinner)
    {
        spinner.IsRunning = false;
        spinner.IsVisible = false;
    }

    // ── Tap handlers ──────────────────────────────────────────────────────────

    private async void OnScanAppsClicked(object sender, TappedEventArgs e)
    {
        await AnimateTap(ScanAppsBtn);
        ShowCardSpinner(ScanAppsSpinner);
        try
        {
            await Navigation.PushAsync(MauiProgram.Services!.GetRequiredService<ScanPage>());
        }
        finally
        {
            HideCardSpinner(ScanAppsSpinner);
        }
    }

    private async void OnSavedAppsClicked(object sender, TappedEventArgs e)
    {
        await AnimateTap(SavedAppsBtn);
        ShowCardSpinner(SavedAppsSpinner);
        try
        {
            await Navigation.PushAsync(MauiProgram.Services!.GetRequiredService<SavedAppsPage>());
        }
        finally
        {
            HideCardSpinner(SavedAppsSpinner);
        }
    }

    private async void OnResetScanClicked(object sender, TappedEventArgs e)
    {
        await AnimateTap(ResetScanBtn);
        ResetConfirmPopup.IsVisible = true;
    }

    private async void OnSettingsClicked(object sender, TappedEventArgs e)
    {
        await AnimateTap(SettingsBtn);
        await Navigation.PushAsync(
            MauiProgram.Services!.GetRequiredService<SettingsPage>());
    }
    private async void OnRetryClicked(object sender, TappedEventArgs e)
        => await InitialiseAsync();

    // ── Reset popup handlers ──────────────────────────────────────────────────

    private void OnResetCancelClicked(object sender, EventArgs e)
        => ResetConfirmPopup.IsVisible = false;

    private async void OnResetConfirmClicked(object sender, EventArgs e)
    {
        ResetConfirmPopup.IsVisible = false;
        await _db.ClearScannedAsync();
        SavedCountLabel.Text = "No saved apps yet";
        ResetDonePopup.IsVisible = true;
    }

    private void OnResetDoneOkClicked(object sender, EventArgs e)
        => ResetDonePopup.IsVisible = false;
    // ── Paywall popup handlers ────────────────────────────────────────────────

    private async void OnOpenRouterClicked(object sender, EventArgs e)
        => await Launcher.Default.OpenAsync(new Uri("https://openrouter.ai/keys"));

    private async void OnPaystackClicked(object sender, EventArgs e)
        => await Launcher.Default.OpenAsync(new Uri(AppConfig.PaystackUrl));

    private async void OnPaywallSettingsClicked(object sender, EventArgs e)
    {
        PaywallPopup.IsVisible = false;
        await Navigation.PushAsync(
            MauiProgram.Services!.GetRequiredService<SettingsPage>());
    }

    private void OnPaywallCloseClicked(object sender, EventArgs e)
        => PaywallPopup.IsVisible = false;

    // ── State helpers ─────────────────────────────────────────────────────────

    private void ShowLoading()
    {
        LoadingState.IsVisible = true;
        ErrorState.IsVisible = false;
        MainContent.IsVisible = false;
    }

    private void ShowMain()
    {
        LoadingState.IsVisible = false;
        ErrorState.IsVisible = false;
        MainContent.IsVisible = true;
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        LoadingState.IsVisible = false;
        ErrorState.IsVisible = true;
        MainContent.IsVisible = false;
    }
}