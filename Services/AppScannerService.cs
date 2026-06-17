using SnapStakMobile.Models;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

#if ANDROID
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Graphics;
#endif

namespace SnapStakMobile.Services;

// ── Scan result types ─────────────────────────────────────────────────────────

public enum ScanPhase { Inspecting, Skipping, Triaging, Found }

public class ScanProgress
{
    public ScanPhase Phase { get; set; }
    public int Total { get; set; }
    public int Inspected { get; set; }
    public int Skipped { get; set; }
    public int Triaged { get; set; }   // eliminated by native triage before DEX scan
    public int Found { get; set; }
    public string CurrentApp { get; set; } = "";
    public AppItem? DetectedItem { get; set; }
}

public class ScanStats
{
    public int Total { get; set; }
    public int Inspected { get; set; }
    public int Skipped { get; set; }
    public int Triaged { get; set; }   // eliminated by native triage before DEX scan
    public int Found { get; set; }
    public List<(string packageName, DateTime apkLastModified)> ToRecord { get; set; } = new();
}

// ── Scanner ───────────────────────────────────────────────────────────────────

public class AppScannerService
{
    private readonly SignatureCacheService _sigCache;

    public AppScannerService(SignatureCacheService sigCache)
    {
        _sigCache = sigCache;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Scan installed apps
    // ──────────────────────────────────────────────────────────────────────────
    public async Task<ScanStats> GetWebViewAppsAsync(
        Dictionary<string, DateTime> scannedTimestamps,
        IProgress<ScanProgress>? progress = null,
        Action<AppItem>? onDetected = null)
    {
        var stats = new ScanStats();

#if ANDROID
        var context = Microsoft.Maui.ApplicationModel.Platform.AppContext;
        var pm = context.PackageManager;
        if (pm == null) return stats;

        var signatures = _sigCache.Signatures;
        var packages = pm.GetInstalledApplications(PackageInfoFlags.MetaData)
                           .Where(p => (p.Flags & ApplicationInfoFlags.System) == 0)
                           .ToList();

        stats.Total = packages.Count;

        // ── Pre-compute native triage set ─────────────────────────────────────
        // Build a set of all config file paths that uniquely identify a WebView
        // framework (e.g. "assets/capacitor.config.json", "res/xml/config.xml").
        // Derived entirely from the server-fetched signatures — nothing hardcoded.
        //
        // Note: some signatures (React Native WebView, Flutter WebView, generic
        // WebView App) have no config file marker and can only be detected by DEX
        // scanning. We do NOT disable triage for these — we simply cannot use
        // triage to rule them out. Instead the triage set covers only the
        // file-bearing signatures. An APK that passes triage (no file found)
        // still proceeds to the DEX scan so fileless signatures get their chance.
        //
        // Triage is only skipped entirely if there are zero file-bearing signatures
        // at all (degenerate case — should never happen in practice).
        var triageFiles = signatures.Any(s => !string.IsNullOrEmpty(s.File))
            ? new HashSet<string>(
                signatures
                    .Where(s => !string.IsNullOrEmpty(s.File))
                    .Select(s => s.File),
                StringComparer.OrdinalIgnoreCase)
            : null;

        // Collect all (packageName, apkLastModified) to batch-save after scan
        var toRecord = new List<(string, DateTime)>();

        await Task.Run(() =>
        {
            foreach (var appInfo in packages)
            {
                string? packageName = appInfo.PackageName;
                string? apkPath = appInfo.PublicSourceDir;
                if (string.IsNullOrEmpty(apkPath) || string.IsNullOrEmpty(packageName)) continue;

                DateTime apkLastModified = File.GetLastWriteTimeUtc(apkPath);

                // Skip if already scanned AND APK unchanged
                if (scannedTimestamps.TryGetValue(packageName, out DateTime lastScanned) &&
                    lastScanned != DateTime.MinValue &&
                    Math.Abs((apkLastModified - lastScanned).TotalSeconds) < 2)
                {
                    stats.Skipped++;
                    progress?.Report(new ScanProgress
                    {
                        Phase = ScanPhase.Skipping,
                        Inspected = stats.Inspected,
                        Skipped = stats.Skipped,
                        Triaged = stats.Triaged,
                        Found = stats.Found,
                        Total = stats.Total,
                        CurrentApp = ""
                    });
                    continue;
                }

                // ── Phase 1: native triage ────────────────────────────────────
                // Read only the ZIP central directory (2–5 ms, no DEX bytes).
                // If none of the known WebView config files exist in this APK
                // it is definitively native — skip the full DEX scan entirely.
                if (triageFiles != null && !CanBeWebViewApp(apkPath, triageFiles))
                {
                    stats.Triaged++;
                    progress?.Report(new ScanProgress
                    {
                        Phase = ScanPhase.Triaging,
                        Inspected = stats.Inspected,
                        Skipped = stats.Skipped,
                        Triaged = stats.Triaged,
                        Found = stats.Found,
                        Total = stats.Total,
                        CurrentApp = ""
                    });
                    continue;
                }

                // ── Phase 2: full DEX scan ────────────────────────────────────
                // Only apps that reach here are actually inspected.
                stats.Inspected++;
                string appLabel = appInfo.LoadLabel(pm)?.ToString() ?? packageName;
                progress?.Report(new ScanProgress
                {
                    Phase = ScanPhase.Inspecting,
                    Inspected = stats.Inspected,
                    Skipped = stats.Skipped,
                    Triaged = stats.Triaged,
                    Found = stats.Found,
                    Total = stats.Total,
                    CurrentApp = appLabel
                });

                var result = InspectApk(apkPath, signatures);
                if (result == null) continue;

                ImageSource? iconSource = null;
                try { iconSource = DrawableToImageSource(appInfo.LoadIcon(pm)); }
                catch { }

                var sig = signatures.FirstOrDefault(s => s.Framework == result.Framework);
                var item = new AppItem
                {
                    Name = appLabel,
                    PackageName = packageName,
                    ApkPath = apkPath,
                    ApkLastModified = apkLastModified,
                    FrameworkBadge = result.Framework,
                    FrameworkBadgeColor = sig?.Color ?? "#7878A0",
                    ExtractedUrl = result.RemoteUrl,
                    IsLocalAsset = result.IsLocal,
                    LocalIndexPath = result.LocalIndexPath,
                    Icon = iconSource
                };

                // Only count and surface apps that SnapStak can actually open.
                // A confirmed WebView app with no extractable URL is not useful —
                // it must not appear in the Found count or the results list.
                bool hasUsableUrl = !string.IsNullOrEmpty(result.RemoteUrl) ||
                                    !string.IsNullOrEmpty(result.LocalIndexPath);

                // Always record the scan timestamp so the app is not re-inspected
                // on the next scan unless the APK has changed.
                toRecord.Add((packageName, apkLastModified));

                if (!hasUsableUrl) continue;

                stats.Found++;
                progress?.Report(new ScanProgress
                {
                    Phase = ScanPhase.Found,
                    Inspected = stats.Inspected,
                    Skipped = stats.Skipped,
                    Triaged = stats.Triaged,
                    Found = stats.Found,
                    Total = stats.Total,
                    CurrentApp = appLabel,
                    DetectedItem = item
                });
                onDetected?.Invoke(item);
            }
        });

        stats.ToRecord = toRecord;
#endif
        return stats;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Native triage — ZIP central directory only, no DEX reads
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fast native-app pre-filter. Opens the APK ZIP and reads only the
    /// central directory (filename list) — no DEX bytes are ever read.
    ///
    /// Returns false if none of the known WebView config files exist in
    /// this APK, meaning it is definitively a native app that can be
    /// skipped before the expensive DEX scan runs.
    ///
    /// Returns true if any config file is present, meaning the APK is a
    /// candidate and should proceed to the full InspectApk DEX scan.
    ///
    /// On any read error, returns true conservatively so InspectApk can
    /// handle the failure in its own try/catch.
    ///
    /// Typical cost: 2–5 ms regardless of APK size.
    /// </summary>
    private static bool CanBeWebViewApp(
        string apkPath,
        HashSet<string> triageFiles)
    {
        try
        {
            using var archive = ZipFile.OpenRead(apkPath);

            // Config file presence is the sole triage gate (2-5 ms, central directory only).
            // If none of the known WebView config files exist in this APK's file list it is
            // definitively native — skip the DEX scan entirely.
            //
            // A config file match is sufficient to pass through to InspectApk. The
            // authoritative DEX scan (across all classesN.dex files) happens there.
            // No secondary DEX pre-check is performed here — scanning only classes.dex
            // causes false negatives because R8/ProGuard can place framework marker
            // classes in classes2.dex or later, not always in the primary DEX.
            return archive.Entries.Any(e => triageFiles.Contains(e.FullName));
        }
        catch
        {
            // Cannot read ZIP — pass through to InspectApk which handles errors.
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Two-layer APK inspection
    // ──────────────────────────────────────────────────────────────────────────

    private InspectResult? InspectApk(string apkPath, List<SignatureDefinition> signatures)
    {
        try
        {
            using var archive = ZipFile.OpenRead(apkPath);


            // ── Layer 1: stream all DEX files for class markers ───────────────
            // Modern APKs use multidex — classes.dex, classes2.dex, classes3.dex etc.
            // The framework classes may be in any of them.
            //
            // IMPORTANT: Capacitor apps embed org.apache.cordova as a compatibility
            // bridge. A chunk-based scan that stops at the first match can therefore
            // incorrectly classify a Capacitor app as Cordova if org.apache.cordova
            // appears in an earlier DEX chunk than com.getcapacitor.
            //
            // Fix: scan ALL DEX content first, accumulate every marker string found,
            // then pick the most specific matching signature (most class markers).
            // This guarantees that com.getcapacitor is found even if it lives in a
            // later chunk or a later DEX file, so Capacitor always wins over Cordova.
            var dexEntries = archive.Entries
                .Where(e => System.Text.RegularExpressions.Regex.IsMatch(
                    e.FullName, @"^classes\d*\.dex$"))
                .ToList();

            if (dexEntries.Count == 0) return null;

            // Collect every unique marker string that appears anywhere across all DEX files.
            // We only search for markers that are actually declared in our signatures — no
            // need to scan for anything else.
            var allMarkers = signatures
                .SelectMany(s => s.ClassMarkers)
                .Where(m => !string.IsNullOrEmpty(m))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var foundMarkers = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dexEntry in dexEntries)
            {
                // Stop early only if every possible marker has already been found
                if (foundMarkers.Count == allMarkers.Count) break;

                using var dexStream = dexEntry.Open();
                const int chunkSize = 65536;
                const int overlap = 256;
                var buffer = new byte[chunkSize + overlap];
                var prevTail = new byte[overlap];
                int prevTailLen = 0;
                bool firstChunk = true;

                while (true)
                {
                    int bytesRead = 0;
                    int offset = 0;

                    if (!firstChunk && prevTailLen > 0)
                    {
                        Buffer.BlockCopy(prevTail, 0, buffer, 0, prevTailLen);
                        offset = prevTailLen;
                    }
                    firstChunk = false;

                    int read;
                    while (bytesRead < chunkSize &&
                           (read = dexStream.Read(buffer, offset + bytesRead,
                               chunkSize - bytesRead)) > 0)
                        bytesRead += read;

                    if (bytesRead == 0) break;

                    int totalLen = offset + bytesRead;
                    string chunk = Encoding.Latin1.GetString(buffer, 0, totalLen);

                    foreach (var marker in allMarkers)
                        if (!foundMarkers.Contains(marker) &&
                            chunk.Contains(marker, StringComparison.Ordinal))
                            foundMarkers.Add(marker);

                    int tailStart = Math.Max(0, totalLen - overlap);
                    prevTailLen = totalLen - tailStart;
                    Buffer.BlockCopy(buffer, tailStart, prevTail, 0, prevTailLen);
                }
            }

            if (foundMarkers.Count == 0) return null;

            // Pick the most specific matching signature — the one with the most
            // class markers ALL of which were found in the DEX. More markers = more
            // specific = higher priority. This ensures Capacitor (com.getcapacitor)
            // wins over Cordova (org.apache.cordova) even though Capacitor apps also
            // contain Cordova strings.
            var matchedSig = signatures
                .Where(s => s.ClassMarkers.Count > 0 &&
                            s.ClassMarkers.All(m => foundMarkers.Contains(m)))
                .OrderByDescending(s => s.ClassMarkers.Count)
                .FirstOrDefault();

            if (matchedSig == null) return null;

            // ── Confirm: at least one config file for this framework group
            // must exist in the APK.
            // We group by class markers — all signatures sharing the same
            // markers are variants of the same framework (e.g. Capacitor has
            // capacitor.config.json AND capacitor.config.ts). If ANY of those
            // variant files is present the app is confirmed as a true WebView app.
            // This eliminates native apps that happen to embed the framework
            // library without being primarily WebView-based (e.g. DHL).
            var frameworkVariants = signatures
                .Where(s => s.ClassMarkers.Count > 0 &&
                            s.ClassMarkers.All(m => matchedSig.ClassMarkers.Contains(m)) &&
                            !string.IsNullOrEmpty(s.File))
                .ToList();

            SignatureDefinition? confirmedSig = null;
            foreach (var variant in frameworkVariants)
            {
                if (archive.GetEntry(variant.File) != null)
                {
                    confirmedSig = variant;
                    break;
                }
            }

            // No config file found for the matched variant.
            // Special case: Capacitor release builds have com.getcapacitor
            // obfuscated by R8 — DEX scan finds org.apache.cordova (Cordova
            // compat bridge) instead. If capacitor.config.json exists in the
            // APK alongside the Cordova DEX markers, it is obfuscated Capacitor.
            if (confirmedSig == null)
            {
                var capacitorSig = signatures.FirstOrDefault(s => s.Framework == "Capacitor");
                if (capacitorSig != null &&
                    !string.IsNullOrEmpty(capacitorSig.File) &&
                    archive.GetEntry(capacitorSig.File) != null)
                {
                    confirmedSig = capacitorSig;
                }
                else
                {
                    return null;
                }
            }

            // Use the confirmed variant as the matched signature going forward
            matchedSig = confirmedSig;

            // ── Lovable refinement: check appId prefix ─────────────────────────
            string detectedFramework = matchedSig.Framework;
            if (detectedFramework == "Capacitor")
            {
                var lovableSig = signatures.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.AppIdPrefix) && s.Framework == "Lovable");

                if (lovableSig != null)
                {
                    var cfgEntry = archive.GetEntry(matchedSig.File);
                    if (cfgEntry != null)
                    {
                        using var cfgStream = cfgEntry.Open();
                        using var cfgReader = new StreamReader(cfgStream);
                        if (cfgReader.ReadToEnd().Contains(
                            lovableSig.AppIdPrefix, StringComparison.OrdinalIgnoreCase))
                            detectedFramework = "Lovable";
                    }
                }
            }

            var finalSig = signatures.FirstOrDefault(s => s.Framework == detectedFramework)
                           ?? matchedSig;

            // ── Layer 2: config file scan for remote URL ───────────────────────
            string? remoteUrl = null;
            if (!string.IsNullOrEmpty(finalSig.File) && finalSig.UrlKeys.Count > 0)
            {
                var cfgEntry = archive.GetEntry(finalSig.File);
                if (cfgEntry != null)
                {
                    using var cfgStream = cfgEntry.Open();
                    using var cfgReader = new StreamReader(cfgStream);
                    remoteUrl = ExtractUrl(cfgReader.ReadToEnd(), finalSig.File, finalSig.UrlKeys);
                }
            }

            remoteUrl ??= ScanStringsXml(archive);

            // ── Layer 3: local index.html ──────────────────────────────────────
            string? localIndexPath = null;
            if (remoteUrl == null && finalSig.LocalIndexPaths.Count > 0)
            {
                foreach (var indexPath in finalSig.LocalIndexPaths)
                {
                    if (archive.GetEntry(indexPath) != null)
                    {
                        localIndexPath = indexPath;
                        break;
                    }
                }
            }

            // Return the result even when no URL or local index was found.
            // Class markers + config file already confirmed this is a WebView app.
            // The URL is optional — the app is still worth surfacing to the user.
            return new InspectResult
            {
                Framework = detectedFramework,
                RemoteUrl = remoteUrl,
                IsLocal = remoteUrl == null,
                LocalIndexPath = localIndexPath
            };
        }
        catch
        {
            return null;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // URL extraction
    // ──────────────────────────────────────────────────────────────────────────

    private string? ExtractUrl(string content, string filePath, List<string> urlKeys)
    {
        foreach (var key in urlKeys)
        {
            string? url = null;

            if (key == "content.src")
            {
                var m = Regex.Match(content, @"<content\s+src=""(https?://[^""]+)""");
                if (m.Success) url = m.Groups[1].Value;
            }
            else if (key.Contains('.'))
            {
                var parts = key.Split('.', 2);
                var nested = Regex.Match(content,
                    $@"""{parts[0]}""\s*:\s*\{{[^}}]*""{parts[1]}""\s*:\s*""(https?://[^""]+)""");
                if (nested.Success) url = nested.Groups[1].Value;
            }
            else
            {
                var m = Regex.Match(content, $@"""{key}""\s*:\s*""(https?://[^""]+)""");
                if (m.Success) url = m.Groups[1].Value;
            }

            if (!string.IsNullOrEmpty(url)) return url;
        }

        return null;
    }

    private string? ScanStringsXml(ZipArchive archive)
    {
        var entry = archive.GetEntry("res/values/strings.xml");
        if (entry == null) return null;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var match = Regex.Match(content,
            @"<string name=""(?:server_url|base_url|api_url|initial_url)"">([^<]+)</string>");
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── Internal result ───────────────────────────────────────────────────────

    private sealed class InspectResult
    {
        public string Framework { get; set; } = "";
        public string? RemoteUrl { get; set; }
        public bool IsLocal { get; set; }
        public string? LocalIndexPath { get; set; }
    }

#if ANDROID
    private static ImageSource? DrawableToImageSource(Android.Graphics.Drawables.Drawable? drawable)
    {
        if (drawable == null) return null;
        Bitmap? bitmap = null;

        if (drawable is BitmapDrawable bd && bd.Bitmap != null)
            bitmap = bd.Bitmap;
        else if (OperatingSystem.IsAndroidVersionAtLeast(26) && drawable is AdaptiveIconDrawable aid)
        {
            bitmap = Bitmap.CreateBitmap(108, 108, Bitmap.Config.Argb8888!);
            if (bitmap != null)
            {
                var canvas = new Canvas(bitmap);
                aid.SetBounds(0, 0, canvas.Width, canvas.Height);
                aid.Draw(canvas);
            }
        }
        else
        {
            int w = drawable.IntrinsicWidth > 0 ? drawable.IntrinsicWidth : 48;
            int h = drawable.IntrinsicHeight > 0 ? drawable.IntrinsicHeight : 48;
            bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!);
            if (bitmap != null)
            {
                var canvas = new Canvas(bitmap);
                drawable.SetBounds(0, 0, w, h);
                drawable.Draw(canvas);
            }
        }

        if (bitmap == null) return null;
        var ms = new MemoryStream();
        bitmap.Compress(Bitmap.CompressFormat.Png!, 90, ms);
        byte[] bytes = ms.ToArray();
        return ImageSource.FromStream(() => new MemoryStream(bytes));
    }
#endif
}