namespace SnapStakMobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        Routing.RegisterRoute(nameof(Views.ScanPage), typeof(Views.ScanPage));
        Routing.RegisterRoute(nameof(Views.SavedAppsPage), typeof(Views.SavedAppsPage));
        Routing.RegisterRoute(nameof(Views.DeconstructPage), typeof(Views.DeconstructPage));
        Routing.RegisterRoute(nameof(Views.TransformPage), typeof(Views.TransformPage));
        Routing.RegisterRoute(nameof(Views.SettingsPage), typeof(Views.SettingsPage));
    }
}
