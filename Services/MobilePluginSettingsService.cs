using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapStakMobile.Services;

// ─────────────────────────────────────────────────────────────────────────────
// MobilePluginSettingsService
//
// Stores translator plugin preferences in MAUI Preferences (key-value store
// backed by Android SharedPreferences). Mirrors the Desktop PluginSettingsService
// which uses browser localStorage.
//
// Mobile differences from Desktop:
//   - No output path fields — exports go to Android Downloads / share sheet
//   - No Framer integration — cloud-based, not relevant for mobile capture
//   - SnapStak SVG plugin added (Desktop does not expose this as a toggle)
//   - Preferences instead of localStorage
// ─────────────────────────────────────────────────────────────────────────────

public sealed class MobilePluginSettingsService
{
    private const string PrefsKey = "snapstak_mobile_plugin_settings";

    private MobilePluginSettings? _cache;

    // ── Load ──────────────────────────────────────────────────────────────────

    public Task<MobilePluginSettings> LoadAsync()
    {
        if (_cache != null) return Task.FromResult(_cache);

        try
        {
            var json = Preferences.Default.Get(PrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                _cache = JsonSerializer.Deserialize<MobilePluginSettings>(json)
                      ?? MobilePluginSettings.Defaults();
                return Task.FromResult(_cache);
            }
        }
        catch
        {
            // First run or corrupted — fall through to defaults
        }

        _cache = MobilePluginSettings.Defaults();
        return Task.FromResult(_cache);
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    public Task SaveAsync(MobilePluginSettings settings)
    {
        _cache = settings;
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = false,
        });
        Preferences.Default.Set(PrefsKey, json);
        return Task.CompletedTask;
    }

    // ── Convenience ───────────────────────────────────────────────────────────

    public async Task<bool> IsPluginEnabledAsync(string pluginKey)
    {
        var s = await LoadAsync();
        return pluginKey switch
        {
            "penpot"      => s.PenpotEnabled,
            "figma"       => s.FigmaEnabled,
            "snapstak-svg"=> s.SnapStakSvgEnabled,
            "canva"       => s.CanvaEnabled,
            _             => true,
        };
    }

    // ── OpenRouter API key ────────────────────────────────────────────────────

    private const string ApiKeyPref = "snapstak_openrouter_api_key";

    public Task<string?> GetApiKeyAsync()
    {
        var val = Preferences.Default.Get(ApiKeyPref, string.Empty);
        return Task.FromResult(string.IsNullOrEmpty(val) ? null : val);
    }

    public Task SetApiKeyAsync(string key)
    {
        Preferences.Default.Set(ApiKeyPref, key);
        return Task.CompletedTask;
    }

    public Task ClearApiKeyAsync()
    {
        Preferences.Default.Remove(ApiKeyPref);
        return Task.CompletedTask;
    }

    // ── Clear all data ────────────────────────────────────────────────────────

    public Task ClearAllSessionDataAsync()
    {
        try
        {
            var dataDir = FileSystem.AppDataDirectory;
            if (Directory.Exists(dataDir))
            {
                // Remove only SnapStak component directories
                var snapstakDir = Path.Combine(dataDir, "snapstak");
                if (Directory.Exists(snapstakDir))
                    Directory.Delete(snapstakDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak] ClearAllSessionData failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}

// ── Settings model ────────────────────────────────────────────────────────────

public sealed class MobilePluginSettings
{
    // Penpot
    [JsonPropertyName("penpotEnabled")]
    public bool PenpotEnabled { get; set; } = true;

    // Figma
    [JsonPropertyName("figmaEnabled")]
    public bool FigmaEnabled { get; set; } = true;

    // SnapStak SVG (native .svg output)
    // SnapStak SVG always runs — it is the source of truth, not a user toggle
    [JsonPropertyName("snapstakSvgEnabled")]
    public bool SnapStakSvgEnabled => true;

    // Canva
    [JsonPropertyName("canvaEnabled")]
    public bool CanvaEnabled { get; set; } = false;

    public static MobilePluginSettings Defaults() => new();
}
