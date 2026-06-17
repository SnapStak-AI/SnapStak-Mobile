namespace SnapStakMobile;

/// <summary>
/// Unified configuration for both Standalone and Enterprise builds.
///
/// Standalone  — CON10X engine runs fully on-device. Subscription via Paystack.
/// Enterprise  — Extraction runs on-device; Structure + Behaviour + Constructor
///               pipeline runs on the configured Enterprise server.
///
/// Build configuration controls which variant compiles:
///   Debug / Release          → Standalone
///   Enterprise               → Enterprise  (define: ENTERPRISE)
/// </summary>
public class AppConfig
{
    private static AppConfig? _instance;
    public static AppConfig Current => _instance ?? (_instance = new AppConfig());

#if ENTERPRISE

    // ── Enterprise: server URL from SecureStorage ─────────────────────────────

    public const string DefaultEnterpriseServerUrl = "https://enterprise.snapstak.ai";

    public string ServerUrl
    {
        get
        {
            try
            {
                var stored = SecureStorage.Default.GetAsync("enterpriseServerUrl")
                    .GetAwaiter().GetResult();
                return string.IsNullOrWhiteSpace(stored)
                    ? DefaultEnterpriseServerUrl
                    : stored.TrimEnd('/');
            }
            catch { return DefaultEnterpriseServerUrl; }
        }
    }

    public string ApiContex            => $"{ServerUrl}/web-to-structure/transform";
    public string ApiProcessSession    => $"{ServerUrl}/structure-to-code/process-session";
    public string ApiSessionEvents(string userUuid)
                                       => $"{ServerUrl}/structure-to-code/events/session/{userUuid}";
    public string ApiDownload(string token)
                                       => $"{ServerUrl}/structure-to-code/download/{token}";

#else

    // ── Standalone: Paystack subscription ────────────────────────────────────

    public string SubscriptionServerUrl => "https://subscriptions.snapstak.ai";

    // ServerUrl alias — SignatureCacheService uses this to fetch app signatures
    public string ServerUrl => SubscriptionServerUrl;

    public string ApiValidate => $"{SubscriptionServerUrl}/api/validate";
    public string ApiCancel   => $"{SubscriptionServerUrl}/api/cancel";

    public const string PaystackUrl = "https://paystack.shop/pay/tdwg1-8fa5";

#endif

    // ── Shared ────────────────────────────────────────────────────────────────

    public const string EngineAssetPath = "engine/content.mobile.js";

    public static void Load() => _instance = new AppConfig();
}
