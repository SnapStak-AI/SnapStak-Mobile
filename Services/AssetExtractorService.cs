using System.IO.Compression;

namespace SnapStakMobile.Services;

/// <summary>
/// Extracts web assets from a locally-bundled WebView APK to a cache folder
/// so they can be loaded via file:// in DeconstructPage.
///
/// Cache key: snapstak_assets/{packageName}_{apkLastModifiedTicks}_v{ExtractorVersion}/
///   - Survives app restarts
///   - Automatically invalidates when the app is updated (modified timestamp changes)
///   - Automatically invalidates when ExtractorVersion is bumped (e.g. new files added)
///   - Android can clean the cache directory under storage pressure
/// </summary>
public class AssetExtractorService
{
    private const string CacheRoot = "snapstak_assets";

    // Bump this version whenever the set of extracted files changes.
    // This forces a re-extraction for all cached apps on next launch,
    // picking up any new files (e.g. native-bridge.js added in v2).
    private const int ExtractorVersion = 26;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the file:// path to the index.html for the given app.
    /// Extracts assets from the APK on first call; uses cache on subsequent calls.
    /// </summary>
    public async Task<string> GetLocalIndexUrlAsync(
        string packageName,
        string apkPath,
        string localIndexPath)
    {
        string cacheDir = GetCacheDir(packageName, apkPath);
        string indexFile = Path.Combine(cacheDir, NormaliseEntryPath(localIndexPath));

        if (!File.Exists(indexFile))
            await ExtractAssetsAsync(apkPath, localIndexPath, cacheDir);

        return $"file://{indexFile}";
    }

    /// <summary>
    /// Returns true if a valid cache already exists for this app version.
    /// </summary>
    public bool IsCached(string packageName, string apkPath, string localIndexPath)
    {
        string cacheDir = GetCacheDir(packageName, apkPath);
        string indexFile = Path.Combine(cacheDir, NormaliseEntryPath(localIndexPath));
        return File.Exists(indexFile);
    }

    // ── Cache directory ───────────────────────────────────────────────────────

    private static string GetCacheDir(string packageName, string apkPath)
    {
        long ticks = File.GetLastWriteTimeUtc(apkPath).Ticks;
        string key = $"{packageName}_{ticks}_v{ExtractorVersion}";
        return Path.Combine(FileSystem.CacheDirectory, CacheRoot, key);
    }

    // ── Extraction ────────────────────────────────────────────────────────────

    private static async Task ExtractAssetsAsync(
        string apkPath,
        string localIndexPath,
        string cacheDir)
    {
        // Extract everything under the top-level assets/ folder.
        //
        // We previously only extracted the sub-folder containing index.html
        // (e.g. "assets/public/"), but apps routinely reference sibling directories —
        // e.g. BK's index.html at "assets/public/index.html" links to CSS and fonts
        // under "assets/bk/". Extracting only the prefix folder missed those files.
        //
        // Extracting all of assets/ is the correct approach: it's still only the web
        // layer of the APK (native code lives in lib/, res/, etc.) and ensures all
        // cross-references between asset sub-directories are resolved correctly.
        const string assetsPrefix = "assets/";

        Directory.CreateDirectory(cacheDir);

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(apkPath);

            foreach (var entry in archive.Entries)
            {
                // Extract everything under assets/ — this covers the main web bundle,
                // any sibling asset directories it references, and Capacitor/Cordova
                // bridge files (native-bridge.js, capacitor.plugins.json, etc.)
                if (!entry.FullName.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip directory entries
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // Build the output path relative to cacheDir
                string relativePath = NormaliseEntryPath(entry.FullName);
                string outputPath = Path.Combine(cacheDir, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

                using var input = entry.Open();
                using var output = File.Create(outputPath);
                input.CopyTo(output);
            }
        });

        // ── Patch vendor JS to silence Capacitor "Not implemented on web" ─────
        // The vendor bundle contains Capacitor's WebPlugin base class with an
        // `unimplemented` method that throws. We patch the extracted file
        // directly so plugin calls resolve silently instead of crashing the
        // app init chain before React can mount.
        await PatchVendorJsAsync(cacheDir);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Patches any vendor JS files in the cache to replace Capacitor's
    /// `unimplemented` method body with one that resolves silently.
    /// This prevents the app init chain from crashing before React mounts.
    ///
    /// The Capacitor WebPlugin base class defines:
    ///   unimplemented(msg){throw this.cap.Exception.fromCodes(...)}
    /// We replace it with:
    ///   unimplemented(){return Promise.resolve({})}
    /// </summary>
    private static async Task PatchVendorJsAsync(string cacheDir)
    {
        try
        {
            var jsDir = Path.Combine(cacheDir, "assets", "public", "static", "js");
            if (!Directory.Exists(jsDir)) return;

            foreach (var jsFile in Directory.GetFiles(jsDir, "*.js"))
            {
                string content = await File.ReadAllTextAsync(jsFile);
                bool patched = false;

                // Pattern 1: method form — unimplemented(...){...throw...}
                // Covers: unimplemented(e){throw...}, unimplemented(e="msg"){throw...},
                //         unimplemented(t){let e=...; throw...}
                // Simulation verified against all known Capacitor 3/4 minified forms.
                var result = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"unimplemented\([^)]*\)\{[^{}]*throw[^{}]*\}",
                    "unimplemented(){return Promise.resolve({})}");
                if (result != content) { content = result; patched = true; }

                // Pattern 2: arrow form — unimplemented:e=>{throw...}
                // Covers single-char param and no-param () arrow forms
                result = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"unimplemented\s*:\s*(?:[a-z]\s*|\(\s*\)\s*)=>\s*\{[^{}]*throw[^{}]*\}",
                    "unimplemented:()=>Promise.resolve({})");
                if (result != content) { content = result; patched = true; }

                // Pattern 3: unavailable — same two forms
                result = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"unavailable\([^)]*\)\{[^{}]*throw[^{}]*\}",
                    "unavailable(){return Promise.resolve({})}");
                if (result != content) { content = result; patched = true; }

                result = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"unavailable\s*:\s*(?:[a-z]\s*|\(\s*\)\s*)=>\s*\{[^{}]*throw[^{}]*\}",
                    "unavailable:()=>Promise.resolve({})");
                if (result != content) { content = result; patched = true; }

                if (patched)
                    System.Diagnostics.Debug.WriteLine($"[SnapStak] Patched: {Path.GetFileName(jsFile)}");
                else
                    System.Diagnostics.Debug.WriteLine($"[SnapStak] No patch needed: {Path.GetFileName(jsFile)}");

                if (patched)
                    await File.WriteAllTextAsync(jsFile, content);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak] Patch error: {ex.Message}");
        }
    }

    /// <summary>Converts APK entry path to a filesystem-safe relative path.</summary>
    private static string NormaliseEntryPath(string entryPath)
        => entryPath.Replace('/', Path.DirectorySeparatorChar);
}