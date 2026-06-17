using Newtonsoft.Json;
using SnapStak.Wasm.Client.Models.Css;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Session;
using SnapStak.Wasm.Client.Storage;

namespace SnapStakMobile.Storage;

/// <summary>
/// IPillarStorage implementation for .NET MAUI Android.
/// Uses FileSystem.AppDataDirectory as the storage root — private to the app,
/// not accessible by other apps, cleaned up when the app is uninstalled.
///
/// Key scheme: {AppDataDirectory}/snapstak/{userUuid}/{componentId}/{filename}
///
/// No encryption — data is on-device in private app storage.
/// The Android filesystem sandbox provides the security boundary.
/// </summary>
public sealed class FilePillarStorage : IPillarStorage
{
    private static string Root => Path.Combine(FileSystem.AppDataDirectory, "snapstak");

    // ── Path helpers ──────────────────────────────────────────────────────────

    public string ResolveComponentDir(string userUuid, string componentId)
        => Path.Combine(Root, userUuid, componentId);

    public string ResolveIconsDir(string componentDir)
        => Path.Combine(componentDir, "icons");

    public string ResolveSessionRoot(string userUuid)
        => Path.Combine(Root, userUuid);

    private static string K(string dir, string file) => Path.Combine(dir, file);

    private static void EnsureDir(string path)
        => Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    private static void Write(string path, string content)
    {
        EnsureDir(path);
        File.WriteAllText(path, content);
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        EnsureDir(path);
        File.WriteAllBytes(path, bytes);
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static byte[]? ReadBytes(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    // ── SVG ───────────────────────────────────────────────────────────────────

    public void WriteSvg(string componentDir, string componentId, string svgContent)
        => Write(K(componentDir, $"{componentId}.svg"), svgContent);

    public string? ReadSvg(string userUuid, string componentId, string? pageComponentId = null)
    {
        var dir = ResolveComponentDir(userUuid, componentId);
        var v = Read(K(dir, $"{componentId}.svg"));
        if (v != null) return v;
        if (pageComponentId == null) return null;
        var altDir = Path.Combine(Root, userUuid, pageComponentId, "components", componentId);
        return Read(K(altDir, $"{componentId}.svg"));
    }

    public void WriteViewportSvg(string componentDir, string componentId, int viewportWidth, string svgContent)
        => Write(K(componentDir, $"{componentId}_viewport_{viewportWidth}px.svg"), svgContent);

    // ── Behaviour ─────────────────────────────────────────────────────────────

    public void WriteCssMd(string componentDir, string componentId, string content)
        => Write(K(componentDir, $"{componentId}_css.md"), content);

    public void WriteJsMd(string componentDir, string componentId, string content)
        => Write(K(componentDir, $"{componentId}_js.md"), content);

    public (string? Css, string? Js) ReadBehaviour(string userUuid, string componentId)
    {
        var dir = ResolveComponentDir(userUuid, componentId);
        return (Read(K(dir, $"{componentId}_css.md")), Read(K(dir, $"{componentId}_js.md")));
    }

    public bool BehaviourMdExists(string componentDir, string componentId)
        => File.Exists(K(componentDir, $"{componentId}_css.md"));

    // ── CSS / JS JSON ─────────────────────────────────────────────────────────

    public void WriteCssJson(string componentDir, string componentId, object css)
        => Write(K(componentDir, $"{componentId}_css.json"), JsonConvert.SerializeObject(css));

    public void WriteJsJson(string componentDir, string componentId, object js)
        => Write(K(componentDir, $"{componentId}_js.json"), JsonConvert.SerializeObject(js));

    public CssJson? ReadCssJson(string userUuid, string componentId)
    {
        var dir = ResolveComponentDir(userUuid, componentId);
        var json = Read(K(dir, $"{componentId}_css.json"));
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonConvert.DeserializeObject<CssJson>(json); }
        catch { return null; }
    }

    public void WriteViewportCssJson(string componentDir, string componentId, int viewportWidth, object css)
        => Write(K(componentDir, $"{componentId}_viewport_{viewportWidth}px_css.json"),
                 JsonConvert.SerializeObject(css));

    // ── Influence / Objective ─────────────────────────────────────────────────

    public void WriteInfluence(string componentDir, string componentId, InfluenceData influence)
        => Write(K(componentDir, $"{componentId}_influence.json"), JsonConvert.SerializeObject(influence));

    public InfluenceData? ReadInfluence(string componentDir, string componentId)
    {
        var json = Read(K(componentDir, $"{componentId}_influence.json"));
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonConvert.DeserializeObject<InfluenceData>(json); }
        catch { return null; }
    }

    public void WriteObjective(string componentDir, string componentId, ObjectiveData objective)
        => Write(K(componentDir, $"{componentId}_objective.json"), JsonConvert.SerializeObject(objective));

    public ObjectiveData? ReadObjective(string componentDir, string componentId)
    {
        var json = Read(K(componentDir, $"{componentId}_objective.json"));
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonConvert.DeserializeObject<ObjectiveData>(json); }
        catch { return null; }
    }

    // ── Hidden elements / components ──────────────────────────────────────────

    public void WriteHiddenElements(string componentDir, string componentId, List<DomElement> elements)
        => Write(K(componentDir, $"{componentId}_hidden_elements.json"),
                 JsonConvert.SerializeObject(elements));

    public void WriteSectionHiddenElements(string componentDir, string componentId, string tag, List<DomElement> elements)
        => Write(K(componentDir, $"{componentId}_{tag}_hidden_elements.json"),
                 JsonConvert.SerializeObject(elements));

    public List<DomElement>? ReadHiddenElements(string userUuid, string componentId, string? pageComponentId)
    {
        var dir = ResolveComponentDir(userUuid, componentId);
        var json = Read(K(dir, $"{componentId}_hidden_elements.json"));
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonConvert.DeserializeObject<List<DomElement>>(json); }
        catch { return null; }
    }

    public void WriteHiddenComponents(string componentDir, string componentId, List<HiddenComponent> components)
        => Write(K(componentDir, $"{componentId}_hidden_components.json"),
                 JsonConvert.SerializeObject(components));

    public void WriteHiddenComponentsSvg(string componentDir, string componentId, string svgContent)
        => Write(K(componentDir, $"{componentId}_hidden.svg"), svgContent);

    public void WriteHiddenComponentSvg(string componentDir, string componentId, string hiddenComponentId, string svgContent)
        => Write(K(componentDir, $"{hiddenComponentId}.svg"), svgContent);

    public void WriteHiddenComponentSnapshot(string componentDir, string componentId, string hiddenComponentId, int viewportWidth, object snapshot)
        => Write(K(componentDir, $"{hiddenComponentId}_viewport_{viewportWidth}px.json"),
                 JsonConvert.SerializeObject(snapshot));

    // ── Source HTML ───────────────────────────────────────────────────────────

    public void WriteSourceHtml(string componentDir, string componentId, string tag, string html)
        => Write(K(componentDir, $"{componentId}_{tag}_source.html"), html);

    public string? ReadSourceHtml(string userUuid, string componentId, string? pageComponentId)
    {
        var dir = ResolveComponentDir(userUuid, componentId);
        foreach (var tag in new[] { "main", "header", "footer", "page" })
        {
            var v = Read(K(dir, $"{componentId}_{tag}_source.html"));
            if (v != null) return v;
        }
        return null;
    }

    // ── Viewport snapshots ────────────────────────────────────────────────────

    public void WriteViewportSnapshot(string componentDir, string componentId, int viewportWidth, object snapshot)
        => Write(K(componentDir, $"{componentId}_viewport_{viewportWidth}px.json"),
                 JsonConvert.SerializeObject(snapshot));

    public List<(int Width, string StorageKey)> ListViewportSnapshots(string userUuid, string componentId, string? pageComponentId)
    {
        var dir = ResolveComponentDir(userUuid, componentId);
        var results = new List<(int, string)>();
        if (!Directory.Exists(dir)) return results;
        foreach (var file in Directory.GetFiles(dir, $"{componentId}_viewport_*px.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"_viewport_(\d+)px$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var w))
                results.Add((w, file));
        }
        return results;
    }

    // ── Icons ─────────────────────────────────────────────────────────────────

    public void WriteIcon(string iconsDir, string internalId, string svgContent)
    {
        var path = K(iconsDir, $"{internalId}.svg");
        EnsureDir(path);
        Directory.CreateDirectory(iconsDir);
        File.WriteAllText(path, svgContent);
    }

    public string? ReadIcon(string componentDir, string? pageComponentDir, string internalId)
    {
        var v = Read(K(ResolveIconsDir(componentDir), $"{internalId}.svg"));
        if (v != null) return v;
        if (pageComponentDir == null) return null;
        return Read(K(ResolveIconsDir(pageComponentDir), $"{internalId}.svg"));
    }

    // ── Snapshot (incoming from Chrome / MAUI bridge) ─────────────────────────

    public void WriteSnapshot(string userUuid, object snapshot)
        => Write(K(Path.Combine(Root, userUuid), "incoming_snapshot.json"),
                 JsonConvert.SerializeObject(snapshot));

    public string? ReadSnapshot(string userUuid)
        => Read(K(Path.Combine(Root, userUuid), "incoming_snapshot.json"));

    public string? ReadSnapshotDirect()
    {
        // Mobile has no Chrome extension bridge — not applicable
        return null;
    }

    public void ClearSnapshot(string userUuid)
    {
        var path = K(Path.Combine(Root, userUuid), "incoming_snapshot.json");
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── Manifest ──────────────────────────────────────────────────────────────

    public void WriteManifest(string dir, object manifest)
        => Write(K(dir, "manifest.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented));

    public string? ReadManifestJson(string dir)
        => Read(K(dir, "manifest.json"));

    public SessionManifest? ReadSessionManifest(string userUuid)
    {
        var json = Read(K(Path.Combine(Root, userUuid), "manifest.json"));
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonConvert.DeserializeObject<SessionManifest>(json); }
        catch { return null; }
    }

    public void RegisterComponentInManifest(string userUuid, string componentId, string componentDir,
        string zone, string label, bool isMaster = false)
    {
        var manifest = ReadSessionManifest(userUuid) ?? new SessionManifest { UserUuid = userUuid };
        manifest.UpdatedAt = DateTime.UtcNow.ToString("O");

        if (!manifest.Components.Any(c => c.ComponentId == componentId))
        {
            manifest.Components.Add(new SessionComponent
            {
                ComponentId = componentId,
                Zone = zone,
                Label = label,
                IsMaster = isMaster,
                RegisteredAt = DateTime.UtcNow.ToString("O"),
            });
        }

        WriteManifest(Path.Combine(Root, userUuid), manifest);
    }

    public void UpdateComponentStatus(string userUuid, string componentId, SessionStatus status,
        string? errorMessage = null, string? zipPath = null, string? downloadToken = null)
    {
        var manifest = ReadSessionManifest(userUuid);
        if (manifest == null) return;
        var comp = manifest.Components.FirstOrDefault(c => c.ComponentId == componentId);
        if (comp == null) return;
        comp.Status = status;
        if (errorMessage != null) comp.ErrorMessage = errorMessage;
        manifest.UpdatedAt = DateTime.UtcNow.ToString("O");
        WriteManifest(Path.Combine(Root, userUuid), manifest);
    }

    public void UpdateSessionStatus(string userUuid, SessionStatus processingStatus,
        SessionStatus? assemblyStatus = null, string? outputZip = null,
        string? framework = null, string? platformType = null,
        string? styleOutput = null, string? language = null, string? errorMessage = null)
    {
        var manifest = ReadSessionManifest(userUuid) ?? new SessionManifest { UserUuid = userUuid };
        manifest.ProcessingStatus = processingStatus;
        if (assemblyStatus.HasValue) manifest.AssemblyStatus = assemblyStatus.Value;
        if (outputZip != null) manifest.OutputZip = outputZip;
        if (framework != null) manifest.Framework = framework;
        if (platformType != null) manifest.PlatformType = platformType;
        if (styleOutput != null) manifest.StyleOutput = styleOutput;
        if (language != null) manifest.Language = language;
        if (errorMessage != null) manifest.ErrorMessage = errorMessage;
        manifest.UpdatedAt = DateTime.UtcNow.ToString("O");
        WriteManifest(Path.Combine(Root, userUuid), manifest);
    }

    public void CleanupSessionFiles(string userUuid, SessionManifest manifest)
    {
        // No-op on mobile — files stay on device until cleared by the user
    }

    public void CleanupPillarFiles(string userUuid, string componentId)
    {
        try
        {
            var dir = ResolveComponentDir(userUuid, componentId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnapStak] CleanupPillarFiles failed: {ex.Message}");
        }
    }

    // ── Translator plugin output ──────────────────────────────────────────────

    public void WriteTranslatorOutput(string componentDir, string componentId, string fileExtension, byte[] bytes)
    {
        var path = K(componentDir, $"{componentId}{fileExtension}");
        WriteBytes(path, bytes);
    }
}
