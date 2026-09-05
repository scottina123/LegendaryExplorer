using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed class MorphFaceFxLine : FaceFXLineEntry
{
    public ExportEntry SourceExport { get; }
    public FaceFXAsset Asset { get; }
    public FaceFXAnimSet AnimSet { get; }
    public string SourceLabel => $"#{SourceExport.UIndex} {SourceExport.InstancedFullPath}";
    public string DisplayLabel => TLKID > 0 ? $"{TLKID} · {Name}" : Name;

    internal MorphFaceFxLine(ExportEntry source, FaceFXAsset asset, FaceFXAnimSet animSet, FaceFXLine line)
        : base(line)
    {
        SourceExport = source;
        Asset = asset;
        AnimSet = animSet;
        TLKID = GetTlkId(line);
        IsMale = !(line.ID?.EndsWith("_F", StringComparison.OrdinalIgnoreCase) == true
                   || line.NameAsString?.EndsWith("_F", StringComparison.OrdinalIgnoreCase) == true);
        TLKString = TLKID > 0 ? TLKManagerWPF.GlobalFindStrRefbyID(TLKID, source.FileRef) : null;
    }

    public bool Matches(string search)
    {
        search = search?.Trim();
        return string.IsNullOrEmpty(search)
               || TLKID > 0 && TLKID.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase)
               || TLKString?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
               || Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
               || Line.ID?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
               || SourceLabel.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    internal ExportEntry FindVoiceExport()
    {
        ExportEntry referenced = string.IsNullOrWhiteSpace(Line.Path) ? null : SourceExport.FileRef.FindExport(Line.Path);
        if (referenced?.ClassName is "WwiseStream" or "WwiseEvent" or "SoundNodeWave") return referenced;
        return FaceFXAnimSetEditorControl.FindVoiceStreamFromExport(SourceExport, this);
    }

    internal static int GetTlkId(FaceFXLine line)
    {
        foreach (string value in new[] { line.ID, line.NameAsString })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            // Accept numeric IDs, VO_123_M (including a package prefix), and ME1 line-name suffixes.
            Match match = Regex.Match(value, @"(?:^|VO_|[_:])(\d+)(?:_[MF])?$", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int id) && id > 0)
                return id;
        }
        return 0;
    }

    internal static List<MorphFaceFxLine> ReadPackage(IMEPackage package, List<MorphFaceFxRig> rigs,
        List<string> errors)
    {
        var lines = new List<MorphFaceFxLine>();
        foreach (ExportEntry export in package.Exports.Where(export => !export.IsDefaultObject
                     && export.ClassName is "FaceFXAsset" or "FaceFXAnimSet"))
        {
            try
            {
                FaceFXAsset asset = export.ClassName == "FaceFXAsset" ? export.GetBinaryData<FaceFXAsset>() : null;
                FaceFXAnimSet animSet = asset == null ? export.GetBinaryData<FaceFXAnimSet>() : null;
                if (asset?.CompiledFaceGraph.Count > 0 && asset.RefBones.Count > 0)
                    rigs.Add(new MorphFaceFxRig(export.ObjectName.Instanced, export.FileRef.FilePath, asset));
                foreach (FaceFXLine line in asset?.Lines ?? animSet.Lines)
                {
                    try { lines.Add(new MorphFaceFxLine(export, asset, animSet, line)); }
                    catch (Exception ex) { errors.Add($"#{export.UIndex} {line.NameAsString}: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"#{export.UIndex} {export.ObjectName.Instanced}: {ex.Message}");
            }
        }
        return lines;
    }
}

public sealed record MorphFaceFxRig(string Name, string PackagePath, FaceFXAsset Asset)
{
    public string Label => $"{Name} ({System.IO.Path.GetFileName(PackagePath)})";
}
