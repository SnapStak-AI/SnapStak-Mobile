using SQLite;

namespace SnapStakMobile.Models;

[Table("saved_apps")]
public class SavedApp
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string Name { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Framework { get; set; } = "";
    public string? IconBase64 { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC last-modified timestamp of the APK at the time of scanning.
    /// If the APK is updated and this timestamp changes, the app is
    /// re-inspected on the next scan regardless of it being in the database.
    /// </summary>
    public DateTime ApkLastModified { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Filesystem path to the installed APK on this device.
    /// Used by SavedAppsPage to extract local assets when the app is tapped.
    /// </summary>
    public string? ApkPath { get; set; }

    /// <summary>
    /// APK entry path of the web bundle's index.html e.g. "assets/public/index.html".
    /// Only set for local-asset apps (IsLocalAsset = true).
    /// Null for apps that load a remote URL.
    /// </summary>
    public string? LocalIndexPath { get; set; }

    /// <summary>
    /// True when the app serves its UI from local bundled assets rather than a remote URL.
    /// </summary>
    public bool IsLocalAsset => Url.StartsWith("local:", StringComparison.Ordinal);

    [Ignore]
    public ImageSource? Icon => IconBase64 != null
        ? ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(IconBase64)))
        : null;

    /// <summary>
    /// Badge colour hex persisted at scan time from the server's SignatureDefinition.
    /// Populated by AppScannerService when saving — never hardcoded here.
    /// </summary>
    public string Color { get; set; } = "#7878A0";

    [Ignore]
    public string FrameworkBadgeColor => !string.IsNullOrEmpty(Color) ? Color : "#7878A0";
}