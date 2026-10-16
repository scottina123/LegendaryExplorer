using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace LegendaryExplorerCore.Scaleform;

/// <summary>An effective display-list instance at one frame; edits target that frame's latest placement.</summary>
public sealed record ScaleformSceneItem(ushort CharacterId, ushort Depth, string Name, ScaleformMatrix Matrix,
    ScaleformTag PlacementTag, string PlacementPath, ScaleformTag Definition, ushort? ClipDepth, bool HasEffects)
{
    public int PlacementFrame { get; init; }
    public ScaleformColorTransform ColorTransform { get; init; } = ScaleformColorTransform.Identity;
    public bool Visible { get; init; } = true;
}

/// <summary>
/// Evaluates SWF timeline placements without running ActionScript. Sprites retain their
/// own local timelines, so callers can enter a symbol and edit it in local coordinates.
/// </summary>
public sealed class ScaleformScene
{
    public IReadOnlyList<ScaleformSceneItem> Items { get; private init; }
    public IReadOnlyDictionary<ushort, ScaleformTag> Definitions { get; private init; }
    public IReadOnlyDictionary<ushort, string> SymbolNames { get; private init; }
    public IReadOnlyList<string> Warnings { get; private init; }
    public int FrameCount { get; private init; }
    public int Frame { get; private init; }

    public static ScaleformScene Build(ScaleformDocument document, ushort? spriteId = null, int frame = 1)
    {
        ArgumentNullException.ThrowIfNull(document);
        var definitions = new Dictionary<ushort, ScaleformTag>();
        var paths = new Dictionary<ScaleformTag, string>();
        var names = new Dictionary<ushort, string>();
        var warnings = new List<string>();
        Index(document.Tags, string.Empty, definitions, paths, names, warnings);
        IReadOnlyList<ScaleformTag> timeline = document.Tags;
        int frameCount = Math.Max(1, (int)document.FrameCount);
        if (spriteId.HasValue)
        {
            if (!definitions.TryGetValue(spriteId.Value, out var sprite) || sprite.Code != 39)
                throw new ArgumentException($"Character {spriteId} is not a movie clip.", nameof(spriteId));
            timeline = sprite.Children;
            frameCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(sprite.Payload.Span[2..]));
        }
        if (frame < 1 || frame > frameCount)
            throw new ArgumentOutOfRangeException(nameof(frame), $"The frame must be between 1 and {frameCount}.");
        var display = new SortedDictionary<ushort, ScaleformSceneItem>();
        int currentFrame = 1;
        foreach (var tag in timeline)
        {
            if (tag.Code == 1)
            {
                if (currentFrame++ >= frame) break;
                continue;
            }
            if (tag.Code == 0) break;
            if (tag.Code is 5 or 28)
            {
                int offset = tag.Code == 5 ? 2 : 0;
                if (tag.Payload.Length < offset + 2)
                {
                    warnings.Add($"{tag.Name} at {paths[tag]} is truncated.");
                    continue;
                }
                ushort depth = BinaryPrimitives.ReadUInt16LittleEndian(tag.Payload.Span[offset..]);
                // RemoveObject also names the character, unlike RemoveObject2.
                if (tag.Code == 28 || display.TryGetValue(depth, out var removed) &&
                    removed.CharacterId == BinaryPrimitives.ReadUInt16LittleEndian(tag.Payload.Span))
                    display.Remove(depth);
                continue;
            }
            if (tag.Code is not (4 or 26 or 70)) continue;
            ScaleformPlacement placement;
            try { placement = ScaleformPlacement.Read(tag); }
            catch (InvalidDataException e)
            {
                warnings.Add($"{tag.Name} at {paths[tag]} cannot be previewed: {e.Message}");
                continue;
            }
            display.TryGetValue(placement.Depth, out var previous);
            if (!placement.IsMove) previous = null;
            ushort? character = placement.CharacterId ?? previous?.CharacterId;
            if (!character.HasValue)
            {
                warnings.Add($"Depth {placement.Depth} has a placement without a known character.");
                continue;
            }
            definitions.TryGetValue(character.Value, out var definition);
            string name = placement.InstanceName ?? previous?.Name;
            if (string.IsNullOrEmpty(name))
                name = names.GetValueOrDefault(character.Value) ?? $"{definition?.Name ?? "Character"} #{character.Value}";
            ushort? clipDepth = placement.ClipDepth ?? previous?.ClipDepth;
            if (clipDepth.HasValue && clipDepth <= placement.Depth)
                warnings.Add($"{name} has a clipping depth that does not extend above its own depth.");
            var color = placement.ColorTransform ?? previous?.ColorTransform ?? ScaleformColorTransform.Identity;
            display[placement.Depth] = new ScaleformSceneItem(character.Value, placement.Depth, name,
                placement.Matrix ?? previous?.Matrix ?? ScaleformMatrix.Identity, tag, paths[tag], definition,
                clipDepth, color != ScaleformColorTransform.Identity || placement.HasOtherEffects || previous?.HasEffects == true)
            {
                PlacementFrame = currentFrame, ColorTransform = color, Visible = placement.Visible ?? previous?.Visible ?? true
            };
        }
        return new ScaleformScene
        {
            Items = display.Values.ToList().AsReadOnly(), Definitions = new ReadOnlyDictionary<ushort, ScaleformTag>(definitions),
            SymbolNames = new ReadOnlyDictionary<ushort, string>(names), Warnings = warnings.AsReadOnly(),
            Frame = frame, FrameCount = frameCount
        };
    }

    /// <summary>Resolves the stable index path used by a scene item, including nested sprite records.</summary>
    public static ScaleformTag FindTag(ScaleformDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        IReadOnlyList<ScaleformTag> tags = document.Tags;
        ScaleformTag result = null;
        foreach (string part in path.Split('/'))
        {
            if (!int.TryParse(part, out int index) || index < 0 || index >= tags.Count)
                throw new ArgumentException("The tag path does not exist in this movie.", nameof(path));
            result = tags[index];
            tags = result.Children;
        }
        return result;
    }

    private static void Index(IReadOnlyList<ScaleformTag> tags, string prefix,
        Dictionary<ushort, ScaleformTag> definitions, Dictionary<ScaleformTag, string> paths,
        Dictionary<ushort, string> names, List<string> warnings)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            string path = prefix + i;
            paths.Add(tag, path);
            if (IsDefinition(tag.Code) && tag.Payload.Length >= 2)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(tag.Payload.Span);
                definitions.TryAdd(id, tag);
            }
            if (tag.Code is 56 or 76)
            {
                var data = tag.Payload.Span;
                if (data.Length < 2) { warnings.Add($"{tag.Name} at {path} is truncated."); continue; }
                int count = BinaryPrimitives.ReadUInt16LittleEndian(data), offset = 2;
                for (int j = 0; j < count; j++)
                {
                    if (data.Length - offset < 3) { warnings.Add($"{tag.Name} at {path} is truncated."); break; }
                    ushort id = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                    offset += 2;
                    int length = data[offset..].IndexOf((byte)0);
                    if (length < 0) { warnings.Add($"{tag.Name} at {path} contains an unterminated name."); break; }
                    names.TryAdd(id, Encoding.UTF8.GetString(data.Slice(offset, length)));
                    offset += length + 1;
                }
            }
            if (tag.Children.Count != 0) Index(tag.Children, path + "/", definitions, paths, names, warnings);
        }
    }

    private static bool IsDefinition(ushort code) => code is 2 or 6 or 7 or 10 or 11 or 14 or 20 or 21 or 22 or 32 or 33 or 34
        or 35 or 36 or 37 or 39 or 46 or 48 or 60 or 75 or 83 or 84 or 87 or 90 or 91 or 1001 or 1005 or 1008 or 1009;
}
