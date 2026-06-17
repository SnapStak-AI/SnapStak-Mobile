using Newtonsoft.Json;
using SnapStak.Wasm.Client.Engine.Plugins;
using SnapStak.Wasm.Client.Engine.StructureAgent;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Requests;
using SnapStak.Wasm.Client.Models.Svg;
using SnapStak.Wasm.Client.Storage;
using SnapStakMobile.Services;

namespace SnapStakMobile.Pipeline;

/// <summary>
/// Mobile CON10X pipeline orchestrator.
/// Replaces the remote server (POST /api/contex + SSE + download) with a
/// fully local pipeline:
///
///   Stage 1 — Structure:  DOM snapshot → SVG + pillar files (on-device)
///   Stage 2 — Translate:  SVG tree → plugin outputs (.penpot, .figma, .svg, .canva)
///   Stage 3 — Export:     write outputs to AppDataDirectory, return file paths
///
/// Called by DeconstructPage after extraction and by TransformPage for export.
/// </summary>
public sealed class MobileConteXPipelineService
{
    private readonly IPillarStorage _storage;
    private readonly TranslatorPluginHost _translators;
    private readonly MobilePluginSettingsService _pluginSettings;
    private readonly HttpClient _http;

    public MobileConteXPipelineService(
        IPillarStorage storage,
        TranslatorPluginHost translators,
        MobilePluginSettingsService pluginSettings)
    {
        _storage = storage;
        _translators = translators;
        _pluginSettings = pluginSettings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ── Stage 1: Transform DOM snapshot → Structure SVG + pillar files ────────

    /// <summary>
    /// Receives the extracted DOM JSON from the WebView and runs Stage 1:
    /// builds the Structure SVG and writes all pillar files on-device.
    /// Returns a result with the componentId and object count.
    /// </summary>
    public async Task<MobilePipelineResult> TransformAsync(
        string extractedJson,
        string userUuid,
        string appName,
        IProgress<MobilePipelineProgress>? progress = null)
    {
        progress?.Report(new MobilePipelineProgress("Parsing extraction…", 5));

        // Parse the extracted JSON from content.mobile.js
        TransformRequest request;
        try
        {
            request = ParseExtractionJson(extractedJson, userUuid, appName);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to parse extraction: {ex.Message}");
        }

        if (request.DomSnapshot == null || request.DomSnapshot.Elements.Count == 0)
            return Fail("Extraction produced no DOM elements.");

        progress?.Report(new MobilePipelineProgress("Building Structure pillar…", 20));

        try
        {
            // Build SVG tree from the DOM elements
            var styleMap = StructureService.BuildStyleMap(request.DomSnapshot.Elements);
            var tree = StructureService.BuildSVGTree(request.DomSnapshot.Elements);
            StructureService.ApplyCssProps(tree, styleMap);

            var uri = Uri.TryCreate(request.Url, UriKind.Absolute, out var parsed)
                ? parsed.Host : request.Url ?? request.ComponentId!;

            var svgOptions = new SvgTreeOptions
            {
                Width = request.DomSnapshot.PageWidth > 0 ? request.DomSnapshot.PageWidth : 390,
                Height = request.DomSnapshot.PageHeight > 0 ? request.DomSnapshot.PageHeight : 844,
                SourceUrl = request.Url ?? string.Empty,
                Title = uri ?? appName,
                PageMap = request.DomSnapshot.PageMap,
            };

            var svgString = SvgSerializer.SerializeTreeSVG(tree, svgOptions);
            var componentDir = _storage.ResolveComponentDir(userUuid, request.ComponentId!);
            _storage.WriteSvg(componentDir, request.ComponentId!, svgString);

            progress?.Report(new MobilePipelineProgress("Structure (S) pillar complete", 40));

            // Write Behaviour source data
            WriteBehaviourSources(request, componentDir);

            // Write Influence and Objective pillars
            if (request.Influence != null)
                _storage.WriteInfluence(componentDir, request.ComponentId!, request.Influence);
            if (request.Objective != null)
                _storage.WriteObjective(componentDir, request.ComponentId!, request.Objective);

            // Register in session manifest
            var zone = IPillarStorage.InferSectionTag(request.ComponentId!) ?? "page";
            _storage.RegisterComponentInManifest(userUuid, request.ComponentId!,
                componentDir, zone, uri ?? appName);

            progress?.Report(new MobilePipelineProgress("Running translator plugins…", 60));

            // Run translator plugins — one output file per enabled plugin
            var pluginOutputs = await RunEnabledTranslatorsAsync(
                tree, svgOptions, request.ComponentId!, componentDir,
                request.Influence, request.Objective, request.HiddenComponents);

            progress?.Report(new MobilePipelineProgress("Export files ready", 100));

            return new MobilePipelineResult
            {
                Success = true,
                ComponentId = request.ComponentId!,
                ObjectCount = request.DomSnapshot.Elements.Count,
                Width = svgOptions.Width,
                Height = svgOptions.Height,
                PluginOutputs = pluginOutputs,
                ComponentDir = componentDir,
            };
        }
        catch (Exception ex)
        {
            return Fail($"Pipeline error: {ex.Message}");
        }
    }

    // ── Stage 2: Run only enabled translator plugins ───────────────────────────

    private async Task<List<MobilePluginOutput>> RunEnabledTranslatorsAsync(
        List<SvgNode> tree,
        SvgTreeOptions options,
        string componentId,
        string componentDir,
        InfluenceData? influence,
        ObjectiveData? objective,
        List<HiddenComponent> hiddenComponents)
    {
        var outputs = new List<MobilePluginOutput>();
        if (_translators.Plugins.Count == 0) return outputs;

        // Filter to only enabled plugins
        var enabledPlugins = new List<IConteXTranslatorPlugin>();
        foreach (var plugin in _translators.Plugins)
        {
            if (await _pluginSettings.IsPluginEnabledAsync(plugin.Key))
                enabledPlugins.Add(plugin);
        }

        if (enabledPlugins.Count == 0) return outputs;

        var bundle = TranslatorPluginHost.BuildBundle(
            tree, options, componentId, influence, objective, hiddenComponents);

        // Phase 1: collect all URLs across enabled plugins
        var allUrls = new HashSet<string>(StringComparer.Ordinal);
        var pluginUrls = new Dictionary<IConteXTranslatorPlugin, IReadOnlyList<string>>();

        foreach (var plugin in enabledPlugins)
        {
            IReadOnlyList<string> urls;
            try { urls = plugin.DeclareResources(bundle); }
            catch { urls = Array.Empty<string>(); }
            pluginUrls[plugin] = urls;
            foreach (var u in urls) allUrls.Add(u);
        }

        // Fetch all declared URLs in parallel
        var fetched = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (allUrls.Count > 0)
        {
            var fetchTasks = allUrls.Select(async url =>
            {
                try
                {
                    var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                    return (url, bytes, ok: true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SnapStak] Fetch failed for '{url}': {ex.Message}");
                    return (url, Array.Empty<byte>(), ok: false);
                }
            });

            foreach (var (url, bytes, ok) in await Task.WhenAll(fetchTasks).ConfigureAwait(false))
                if (ok) fetched[url] = bytes;
        }

        // Phase 2: translate
        foreach (var plugin in enabledPlugins)
        {
            try
            {
                var pluginFetched = pluginUrls[plugin].Count == 0
                    ? (IReadOnlyDictionary<string, byte[]>)new Dictionary<string, byte[]>()
                    : pluginUrls[plugin]
                        .Where(fetched.ContainsKey)
                        .ToDictionary(u => u, u => fetched[u]);

                var bytes = plugin.Translate(bundle, pluginFetched) ?? Array.Empty<byte>();

                if (bytes.Length > 0)
                {
                    // Write to component directory
                    _storage.WriteTranslatorOutput(componentDir, componentId, plugin.FileExtension, bytes);

                    var outputPath = Path.Combine(componentDir, $"{componentId}{plugin.FileExtension}");
                    outputs.Add(new MobilePluginOutput
                    {
                        PluginKey = plugin.Key,
                        DisplayName = plugin.DisplayName,
                        FileExtension = plugin.FileExtension,
                        FilePath = outputPath,
                        FileSizeBytes = bytes.Length,
                    });

                    System.Diagnostics.Debug.WriteLine(
                        $"[SnapStak] Plugin '{plugin.Key}' → {componentId}{plugin.FileExtension} ({bytes.Length} bytes)");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SnapStak] Plugin '{plugin.Key}' failed: {ex.Message}");
            }
        }

        return outputs;
    }

    // ── Parse extraction JSON from content.mobile.js ──────────────────────────

    private static TransformRequest ParseExtractionJson(string json, string userUuid, string appName)
    {
        dynamic? raw = JsonConvert.DeserializeObject(json);
        if (raw == null) throw new InvalidOperationException("Null extraction result");

        long tsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string safeName = System.Text.RegularExpressions.Regex
            .Replace((appName ?? "component").ToLowerInvariant(), @"[^a-z0-9]", "-").Trim('-');

        // componentId may already be set by content.js in the result
        string componentId = raw.componentId?.ToString() ?? $"{safeName}_{tsMs}";
        string url = raw.url?.ToString() ?? raw.meta?.url?.ToString() ?? "unknown";

        // Deserialize the main snapshot
        var snapshot = new DomSnapshot();
        if (raw.mainSnapshot != null)
        {
            var mainRaw = JsonConvert.SerializeObject(raw.mainSnapshot);
            snapshot = JsonConvert.DeserializeObject<DomSnapshot>(mainRaw) ?? snapshot;
        }
        else if (raw.domSnapshot != null)
        {
            var snapRaw = JsonConvert.SerializeObject(raw.domSnapshot);
            snapshot = JsonConvert.DeserializeObject<DomSnapshot>(snapRaw) ?? snapshot;
        }
        else
        {
            // content.mobile.js returns { visible[], invisible[] } at root
            // Wrap into DomSnapshot format
            snapshot.Elements = new List<DomElement>();
            if (raw.visible != null)
            {
                var visRaw = JsonConvert.SerializeObject(raw.visible);
                var visElements = JsonConvert.DeserializeObject<List<DomElement>>(visRaw);
                if (visElements != null) snapshot.Elements.AddRange(visElements);
            }

            // Page dimensions
            if (raw.meta?.viewport?.width != null)
                snapshot.PageWidth = (int)raw.meta.viewport.width;
            if (raw.meta?.viewport?.height != null)
                snapshot.PageHeight = (int)raw.meta.viewport.height;

            if (snapshot.PageWidth <= 0) snapshot.PageWidth = 390;
            if (snapshot.PageHeight <= 0) snapshot.PageHeight = 844;
        }

        // Build CSS from mainCSS if present
        SnapStak.Wasm.Client.Models.Css.CssJson? css = null;
        if (raw.mainCSS != null)
        {
            try
            {
                var cssRaw = JsonConvert.SerializeObject(raw.mainCSS);
                css = JsonConvert.DeserializeObject<SnapStak.Wasm.Client.Models.Css.CssJson>(cssRaw);
            }
            catch { }
        }

        // Build Influence pillar (Pillar 3)
        var influence = new InfluenceData
        {
            ComponentId = componentId,
            BrowserName = "Android WebView",
            BrowserVersion = "Chromium",
            OsName = "Android",
            ScreenWidth = snapshot.PageWidth,
            ScreenHeight = snapshot.PageHeight,
            DevicePixelRatio = 3.0,
            ViewportWidth = snapshot.PageWidth,
            ViewportHeight = snapshot.PageHeight,
            UserAgent = "Mozilla/5.0 (Linux; Android) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36",
            PrefersColorScheme = "light",
            PrefersReducedMotion = "no-preference",
            CapturedAt = DateTime.UtcNow.ToString("O"),
        };

        // Build Objective pillar (Pillar 4)
        var objective = new ObjectiveData
        {
            ComponentId = componentId,
            DeviceType = "mobile",
            ScreenWidthTarget = snapshot.PageWidth,
            ScreenSizeLabel = "mobile",
            AllBreakpoints = 0,
            CapturedBreakpoints = new int[] { snapshot.PageWidth },
            AdditionalIntent = "MAUI Android WebView component",
            ConversionRequestedAt = DateTime.UtcNow.ToString("O"),
        };

        return new TransformRequest
        {
            ComponentId = componentId,
            Url = url,
            DomSnapshot = snapshot,
            ComponentCss = css,
            Influence = influence,
            Objective = objective,
            UserUuid = userUuid,
            Client = "maui",
        };
    }

    // ── Behaviour source data ─────────────────────────────────────────────────

    private void WriteBehaviourSources(TransformRequest request, string componentDir)
    {
        if (request.ComponentCss != null && !request.ComponentCss.IsEmpty)
            _storage.WriteCssJson(componentDir, request.ComponentId!, request.ComponentCss);

        if (request.DomSnapshot?.PageMap != null)
        {
            var mergedCss = new SnapStak.Wasm.Client.Models.Css.CssJson();
            foreach (var s in request.DomSnapshot.PageMap)
            {
                if (string.IsNullOrEmpty(s.CssB64)) continue;
                try
                {
                    var css = JsonConvert.DeserializeObject<SnapStak.Wasm.Client.Models.Css.CssJson>(
                        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s.CssB64)));
                    if (css != null)
                    {
                        mergedCss.Matched.AddRange(css.Matched);
                        mergedCss.Behavior.AddRange(css.Behavior);
                        mergedCss.Media.AddRange(css.Media);
                        mergedCss.Keyframes.AddRange(css.Keyframes);
                    }
                }
                catch { }
            }
            if (!mergedCss.IsEmpty)
                _storage.WriteCssJson(componentDir, request.ComponentId!, mergedCss);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MobilePipelineResult Fail(string error) =>
        new() { Success = false, Error = error };
}

// ── Result types ──────────────────────────────────────────────────────────────

public sealed class MobilePipelineResult
{
    public bool Success { get; set; }
    public string ComponentId { get; set; } = string.Empty;
    public int ObjectCount { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<MobilePluginOutput> PluginOutputs { get; set; } = new();
    public string ComponentDir { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public sealed class MobilePluginOutput
{
    public string PluginKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
}

public sealed class MobilePipelineProgress
{
    public string Message { get; }
    public int Percentage { get; }
    public MobilePipelineProgress(string message, int percentage)
    { Message = message; Percentage = percentage; }
}
