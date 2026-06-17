using SQLite;

namespace SnapStakMobile.Models;

/// <summary>
/// Lightweight record of every app that has been inspected by the scanner,
/// regardless of whether it was detected as a WebView app.
///
/// Used to skip apps on subsequent scans when their APK has not changed,
/// making repeat scans near-instant.
/// </summary>
[Table("scanned_apps")]
public class ScannedApp
{
    [PrimaryKey]
    public string PackageName { get; set; } = "";

    /// <summary>UTC last-modified time of the APK when it was last inspected.</summary>
    public DateTime ApkLastModified { get; set; } = DateTime.MinValue;

    /// <summary>When this record was last updated.</summary>
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
}
