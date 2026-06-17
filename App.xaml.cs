using SnapStakMobile.Services;

namespace SnapStakMobile;

public partial class App : Application
{
    public static SignatureCacheService SignatureCache { get; } = new();

    // CON10X: Engine is now a local asset reader, not a remote HTTP fetcher.
    // Accessed via DI (MauiProgram.Services) in DeconstructPage.CreateAsync.
    // Kept here as a convenience accessor for LocalAssetWebViewHandler.
    public static LocalEngineService Engine => MauiProgram.Services!
        .GetRequiredService<LocalEngineService>();

    public App()
    {
        InitializeComponent();
        AppConfig.Load();
        PairingService.StartUnpairListener();
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new Views.SplashPage());

    protected override void OnSleep()
    {
        base.OnSleep();
        PairingService.StopUnpairListener();
        try { MauiProgram.Services?.GetService<LocalEngineService>()?.Destroy(); }
        catch { }
    }

    protected override void OnResume()
    {
        base.OnResume();
        PairingService.StartUnpairListener();
    }
}