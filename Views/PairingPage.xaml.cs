using SnapStakMobile.Services;

namespace SnapStakMobile.Views;

public partial class PairingPage : ContentPage
{
    private CancellationTokenSource? _cts;

    public PairingPage()
    {
        InitializeComponent();
    }

    private async void OnPairClicked(object sender, EventArgs e)
    {
        var pin = (PinEntry.Text ?? string.Empty).Trim();
        if (pin.Length != 6 || !pin.All(char.IsDigit))
        {
            ShowStatus("Please enter the 6-digit PIN shown on SnapStak Desktop.", "#FF8888");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        PairBtn.IsEnabled  = false;
        PairBtn.Text       = "Pairing...";
        PinEntry.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(msg =>
                MainThread.BeginInvokeOnMainThread(() => ShowStatus(msg, "#7878A0")));

            await PairingService.PairWithDesktopAsync(pin, progress, _cts.Token);

            ShowStatus("✓ Paired successfully. Validating subscription...", "#44FF88");

            // Validate subscription before entering the app
            var (active, error) = await PairingService.ValidateSubscriptionAsync();

            if (!active)
            {
                ShowStatus($"Paired but subscription inactive: {error}", "#FF8888");
                PairBtn.Text       = "Try again";
                PairBtn.IsEnabled  = true;
                PinEntry.IsEnabled = true;
                PinEntry.Text      = string.Empty;
                return;
            }

            // Paired and subscribed — enter the app
            ShowStatus("✓ Subscription active. Loading SnapStak...", "#44FF88");
            await Task.Delay(800);
            Application.Current!.Windows[0].Page = new AppShell();
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Pairing cancelled.", "#7878A0");
            ResetForm();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, "#FF8888");
            ResetForm();
        }
    }

    private void ShowStatus(string message, string colorHex)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusBorder.IsVisible = true;
            StatusLabel.Text       = message;
            StatusLabel.TextColor  = Color.FromArgb(colorHex);
        });
    }

    private void ResetForm()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PairBtn.Text       = "Pair with SnapStak Desktop";
            PairBtn.IsEnabled  = true;
            PinEntry.IsEnabled = true;
            PinEntry.Text      = string.Empty;
        });
    }
}
