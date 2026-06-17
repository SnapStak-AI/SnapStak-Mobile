using SnapStakMobile.Services;

namespace SnapStakMobile.Views;

public partial class SplashPage : ContentPage
{
    public SplashPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Task.Delay(2000);

        // Check pairing status on every launch.
        // Not paired → mandatory PairingPage.
        // Paired → validate subscription → AppShell.
        bool paired = await PairingService.IsPairedAsync();

        if (!paired)
        {
            Application.Current!.Windows[0].Page = new PairingPage();
            return;
        }

        // Paired — validate subscription silently
        var (active, _) = await PairingService.ValidateSubscriptionAsync();

        if (!active)
        {
            // Subscription lapsed — send back to PairingPage
            Application.Current!.Windows[0].Page = new PairingPage();
            return;
        }

        Application.Current!.Windows[0].Page = new AppShell();
    }
}
