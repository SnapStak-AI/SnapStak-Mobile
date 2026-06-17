namespace SnapStakMobile.Models;

public class AppItem
{
    public string? Name { get; set; }
    public string? PackageName { get; set; }
    public string? ApkPath { get; set; }
    public ImageSource? Icon { get; set; }

    /// <summary>Framework name — set from signature definition e.g. "Capacitor"</summary>
    public string FrameworkBadge { get; set; } = "WebView";

    /// <summary>Badge color hex — set from signature definition, no hardcoding</summary>
    public string FrameworkBadgeColor { get; set; } = "#7878A0";

    /// <summary>Remote https:// URL extracted from config file. Null if app is local.</summary>
    public string? ExtractedUrl { get; set; }

    /// <summary>UTC last-modified time of the APK — stored in DB to detect app updates.</summary>
    public DateTime ApkLastModified { get; set; } = DateTime.MinValue;

    /// <summary>
    /// True when the app serves its UI from local bundled assets rather than a remote URL.
    /// DeconstructPage will extract APK assets to cache and load via file://.
    /// </summary>
    public bool IsLocalAsset { get; set; }

    /// <summary>
    /// APK entry path of the local index.html e.g. "assets/public/index.html".
    /// Only set when IsLocalAsset is true.
    /// </summary>
    public string? LocalIndexPath { get; set; }

    /// <summary>The URL or path to load in DeconstructPage — remote URL or extracted cache path.</summary>
    public string? LoadUrl => IsLocalAsset ? null : ExtractedUrl;
}