using System.Text.Json;
using SnapStakMobile.Models;

namespace SnapStakMobile.Services;

/// <summary>
/// Loads WebView scanner signatures from a bundled local asset.
///
/// CON10X Standalone — no server required. Signatures are bundled in
/// Resources/Raw/signatures.json and loaded via FileSystem.OpenAppPackageFileAsync.
///
/// The signature list covers the major WebView frameworks: Capacitor, Cordova,
/// Median, Ionic, React Native WebView, Flutter WebView, and generic WebView apps.
/// Updates ship with new app versions — no server fetch needed.
/// </summary>
public class SignatureCacheService
{
    private const string AssetPath = "signatures.json";

    private List<SignatureDefinition>? _signatures;
    private List<AppIdLabel>? _appIdLabels;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsLoaded => _signatures != null;

    public List<SignatureDefinition> Signatures
    {
        get
        {
            if (_signatures == null)
                throw new InvalidOperationException(
                    "Signatures not loaded. Call FetchAsync() first.");
            return _signatures;
        }
    }

    public List<AppIdLabel> AppIdLabels => _appIdLabels ?? new();

    /// <summary>
    /// Loads signatures from the bundled local asset.
    /// No server, no auth, no network required.
    /// </summary>
    public async Task FetchAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_signatures != null && _appIdLabels != null) return;

            using var stream = await FileSystem.OpenAppPackageFileAsync(AssetPath);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            var manifest = JsonSerializer.Deserialize<SignatureManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest?.Signatures == null || manifest.Signatures.Count == 0)
                throw new Exception("Bundled signature list is empty.");

            _signatures = manifest.Signatures;
            _appIdLabels = manifest.AppIdLabels ?? new();

            System.Diagnostics.Debug.WriteLine(
                $"[SnapStak] Loaded {_signatures.Count} signatures from bundled asset.");
        }
        finally
        {
            _lock.Release();
        }
    }
}