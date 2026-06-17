namespace SnapStakMobile.Services;

/// <summary>
/// Provides the mobile extraction engine (content.mobile.js) from the app's
/// embedded raw assets. Replaces EngineService which fetched from a remote server.
///
/// content.mobile.js is bundled as a MauiAsset at:
///   Resources/Raw/engine/content.mobile.js
///
/// The script is read once and cached for the session. It is never written
/// to disk and never transmitted over the network.
/// </summary>
public sealed class LocalEngineService
{
    private const string AssetPath = "engine/content.mobile.js";

    private string? _cached;
    private readonly object _lock = new();

    public bool IsLoaded
    {
        get { lock (_lock) { return _cached != null; } }
    }

    /// <summary>
    /// Loads the engine script from the embedded asset if not already loaded.
    /// Safe to call multiple times — returns immediately if already loaded.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        lock (_lock)
        {
            if (_cached != null) return;
        }

        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync(AssetPath);
            using var reader = new StreamReader(stream);
            var script = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(script))
                throw new InvalidOperationException($"Engine asset is empty: {AssetPath}");

            lock (_lock)
            {
                _cached = script;
            }

            System.Diagnostics.Debug.WriteLine($"[SnapStak] LocalEngineService: loaded {script.Length:N0} chars from {AssetPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak] LocalEngineService: failed to load asset — {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Returns the engine script. Throws if not yet loaded.
    /// </summary>
    public string Read()
    {
        lock (_lock)
        {
            if (_cached == null)
                throw new InvalidOperationException("Engine not loaded. Call EnsureLoadedAsync() first.");
            return _cached;
        }
    }

    /// <summary>
    /// Clears the cached script from memory.
    /// Called on app suspend to prevent memory dumps exposing the script.
    /// </summary>
    public void Destroy()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }
}
