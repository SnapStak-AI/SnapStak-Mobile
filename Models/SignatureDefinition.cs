namespace SnapStakMobile.Models;

/// <summary>
/// Represents one WebView framework signature entry fetched from the server.
/// The server owns this list — no fallback, no hardcoded values.
///
/// Detection is two-layer:
///   Layer 1 — classMarkers: strings to find in classes.dex (primary gate, eliminates false positives)
///   Layer 2 — file + urlKeys: config file scan to extract remote URL
///   Layer 3 — localIndexPaths: if no remote URL, where to find the local index.html
/// </summary>
public class SignatureDefinition
{
    /// <summary>Framework name to display e.g. "Capacitor", "Cordova", "Median"</summary>
    public string Framework { get; set; } = "";

    /// <summary>Badge color hex e.g. "#4F8EF7" — comes from server, no hardcoding in app</summary>
    public string Color { get; set; } = "#7878A0";

    /// <summary>
    /// Strings to search for in classes.dex to confirm this is a true WebView app.
    /// e.g. ["com.getcapacitor"] for Capacitor — eliminates native apps that bundle HTML.
    /// If empty, class scanning is skipped and file presence alone confirms the framework.
    /// </summary>
    public List<string> ClassMarkers { get; set; } = new();

    /// <summary>APK entry path of the config file e.g. "assets/capacitor.config.json"</summary>
    public string File { get; set; } = "";

    /// <summary>
    /// Dot-notation keys to extract the remote URL from the config file.
    /// e.g. ["server.url", "server.hostname"] for Capacitor JSON
    /// e.g. ["content.src"] for Cordova XML
    /// e.g. ["initialUrl"] for Median appConfig.json
    /// </summary>
    public List<string> UrlKeys { get; set; } = new();

    /// <summary>
    /// Ordered list of APK asset paths where index.html may live for local apps.
    /// Tried in order — first match wins.
    /// e.g. ["assets/public/index.html", "assets/www/index.html", "assets/index.html"]
    /// </summary>
    public List<string> LocalIndexPaths { get; set; } = new();

    /// <summary>
    /// Optional: appId prefix that uniquely identifies this framework within a shared runtime.
    /// e.g. "app.lovable." to distinguish Lovable apps from generic Capacitor apps.
    /// </summary>
    public string AppIdPrefix { get; set; } = "";
}

/// <summary>
/// Maps an appId prefix to a display label and colour.
/// Used to relabel a detected framework (e.g. Capacitor → Lovable)
/// based on the appId found inside the config file.
/// </summary>
public class AppIdLabel
{
    public string AppIdPrefix { get; set; } = "";
    public string Framework { get; set; } = "";
    public string Color { get; set; } = "#7878A0";
}

/// <summary>Root object of the bundled signatures.json asset.</summary>
public class SignatureManifest
{
    public List<SignatureDefinition> Signatures { get; set; } = new();
    public List<AppIdLabel> AppIdLabels { get; set; } = new();
}