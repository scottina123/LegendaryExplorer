using System;
using System.IO;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorerCore.Scaleform;

/// <summary>
/// An isolated movie draft. Applying replaces only its embedded data property and refuses
/// to overwrite changes made by another package editor since the draft was opened.
/// Package persistence remains the responsibility of the normal package save workflow.
/// </summary>
public sealed class ScaleformExportSession
{
    private byte[] sourceBytes;
    public ExportEntry Export { get; }
    public ScaleformDocument Document { get; private set; }
    public string DataPropertyName => Export.ClassName == "BioSWF" ? "Data" : "RawData";

    public ScaleformExportSession(ExportEntry export)
    {
        ArgumentNullException.ThrowIfNull(export);
        if (export.IsDefaultObject || export.ClassName is not ("BioSWF" or "GFxMovieInfo"))
            throw new ArgumentException("Select a BioSWF or GFxMovieInfo movie export.", nameof(export));
        Export = export;
        sourceBytes = ReadBytes().ToArray();
        Document = ScaleformDocument.Parse(sourceBytes);
    }

    private ReadOnlySpan<byte> ReadBytes()
    {
        var data = Export.GetProperty<ImmutableByteArrayProperty>(DataPropertyName);
        return data?.Bytes ?? throw new InvalidDataException($"The movie has no {DataPropertyName} byte array.");
    }

    public bool HasExternalChanges => !ReadBytes().SequenceEqual(sourceBytes);

    /// <summary>Compile and reopen before any package property is changed.</summary>
    public byte[] Compile() => Document.Compile();

    /// <summary>Recreate the isolated draft without accepting external package changes.</summary>
    public void ResetDraft() => Document = ScaleformDocument.Parse(sourceBytes);

    public bool Apply()
    {
        byte[] compiled = Compile();
        if (HasExternalChanges)
            throw new InvalidOperationException("This movie changed in another editor. Export the draft if needed, then reload the movie before applying it.");
        if (compiled.AsSpan().SequenceEqual(sourceBytes)) return false;
        var reopened = ScaleformDocument.Parse(compiled);
        var properties = Export.GetProperties();
        var data = properties.GetProp<ImmutableByteArrayProperty>(DataPropertyName)
                   ?? throw new InvalidDataException($"The movie has no {DataPropertyName} byte array.");
        data.Bytes = compiled;
        Export.WriteProperties(properties);
        sourceBytes = compiled;
        Document = reopened;
        return true;
    }
}
