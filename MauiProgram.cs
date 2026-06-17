using Microsoft.Extensions.Logging;
using SnapStak.Wasm.Client.Engine.Plugins;
using SnapStak.Wasm.Client.Storage;
using SnapStakMobile.Pipeline;
using SnapStakMobile.Services;
using SnapStakMobile.Storage;
using SnapStakMobile.Views;

namespace SnapStakMobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        AppConfig.Load();

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Poppins-Bold.ttf", "PoppinsBold");
            })
            .ConfigureMauiHandlers(handlers =>
            {
#if ANDROID
                handlers.AddHandler<Microsoft.Maui.Controls.WebView,
                    SnapStakMobile.Platforms.Android.LocalAssetWebViewHandler>();
#endif
            });

        // ── Shared services (both builds) ─────────────────────────────────────
        builder.Services.AddSingleton<AppDatabaseService>();
        builder.Services.AddSingleton<AppScannerService>();
        builder.Services.AddTransient<ScanPage>();
        builder.Services.AddTransient<SavedAppsPage>();
        builder.Services.AddTransient<AppListPage>();
        builder.Services.AddTransient<PairingPage>();
        builder.Services.AddSingleton<LocalEngineService>();
        builder.Services.AddSingleton<MobilePluginSettingsService>();
        builder.Services.AddSingleton<IPillarStorage, FilePillarStorage>();
        builder.Services.AddTransient<SettingsPage>();

#if ENTERPRISE

        // ── Enterprise pipeline ───────────────────────────────────────────────
        // Extraction runs on-device via content.mobile.js (LocalEngineService).
        // Everything after extraction — Structure, Behaviour, Constructor —
        // runs on the configured Enterprise CON10X server.
        builder.Services.AddSingleton<EnterpriseConteXService>();

#else

        // ── Standalone pipeline ───────────────────────────────────────────────
        // Full CON10X engine runs on-device. No server required.
        // Translator plugins (Penpot, Figma, SnapStak SVG, Canva) run locally.
        builder.Services.AddSingleton<TranslatorPluginHost>();
        builder.Services.AddSingleton<MobileConteXPipelineService>();

#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        Services = app.Services;
        return app;
    }

    public static IServiceProvider? Services { get; private set; }
}