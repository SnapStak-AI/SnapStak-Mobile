// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/

using System.Text;
using SnapStak.Wasm.Client.Engine.Plugins;
using SnapStak.Wasm.Client.Engine.StructureAgent;
using SnapStak.Wasm.Client.Models.Svg;

namespace SnapStakMobile.Engine.Plugins.SnapStakSvg;

/// <summary>
/// SnapStak SVG translator plugin.
///
/// Produces the canonical SnapStak CON10X Structure SVG — the same format
/// the Desktop writes as its master .svg file. On mobile this is exposed as
/// a toggleable plugin so the user can choose to receive it via the share sheet.
///
/// The output is identical to the master SVG written by StructureAgentService,
/// serialised by SvgSerializer. This plugin simply re-serialises from the
/// already-built SvgNode tree so it runs without any additional I/O.
///
/// Plugin key: "snapstak-svg"
/// File extension: ".snapstak.svg"
/// </summary>
public sealed class SnapStakSvgTranslatorPlugin : IConteXTranslatorPlugin
{
    public string Key => "snapstak-svg";
    public string DisplayName => "SnapStak SVG (CON10X Structure)";
    public string Version => "1.0.0";
    public string FileExtension => ".snapstak.svg";

    // ── Phase 1: no remote resources needed ───────────────────────────────────

    public IReadOnlyList<string> DeclareResources(TranslatorBundle bundle)
        => Array.Empty<string>();

    // ── Phase 2: serialise the desktop tree to SVG bytes ─────────────────────

    public byte[] Translate(
        TranslatorBundle bundle,
        IReadOnlyDictionary<string, byte[]> fetchedResources)
    {
        if (bundle.Desktop.Tree.Count == 0) return Array.Empty<byte>();

        try
        {
            var options = new SvgTreeOptions
            {
                Width = bundle.Desktop.Width,
                Height = bundle.Desktop.Height,
                SourceUrl = bundle.Desktop.SourceUrl,
                Title = bundle.Desktop.Title,
            };

            // Re-serialise from the in-memory tree — identical output to the
            // master SVG written by StructureAgentService during Transform().
            var svgString = SvgSerializer.SerializeTreeSVG(
                bundle.Desktop.Tree.ToList(), options);

            return Encoding.UTF8.GetBytes(svgString);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SnapStakSvgPlugin] Serialisation failed: {ex.Message}");
            return Array.Empty<byte>();
        }
    }
}
