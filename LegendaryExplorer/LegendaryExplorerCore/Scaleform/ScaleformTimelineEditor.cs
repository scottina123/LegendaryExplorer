using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LegendaryExplorerCore.Scaleform;

public sealed partial class ScaleformDocument
{
    /// <summary>
    /// Adds an instance of an existing visual symbol immediately before a frame is shown.
    /// All existing records remain in place; no symbol definitions or scripts are rewritten.
    /// </summary>
    public ScaleformTag AddPlacement(ushort characterId, ScaleformMatrix matrix, ushort? spriteId = null, int frame = 1, ushort? depth = null)
    {
        if (Version < 3) throw new NotSupportedException("Adding placements requires SWF 3 or later.");
        var scene = ScaleformScene.Build(this, spriteId, frame);
        if (!scene.Definitions.TryGetValue(characterId, out var definition) || !IsVisualSymbol(definition.Code))
            throw new ArgumentException("Choose an existing visual symbol from this movie.", nameof(characterId));
        var (timeline, sprite, insertion) = FindInsertion(scene, spriteId, frame);
        // Root-timeline definitions must be encountered before their first use.
        if (!spriteId.HasValue && definition.OriginalOffset > timeline[insertion].OriginalOffset)
            throw new InvalidOperationException("This symbol is defined after the selected frame. Choose a later frame.");
        if (spriteId.HasValue && ReferencesSymbol(characterId, spriteId.Value, scene.Definitions, new HashSet<ushort>()))
            throw new InvalidOperationException("A movie clip cannot contain itself, directly or through another clip.");
        int highestDepth = 0;
        var usedDepths = new HashSet<ushort>();
        foreach (var tag in timeline.Where(t => t.Code is 4 or 26 or 70))
        {
            var placement = ScaleformPlacement.Read(tag);
            usedDepths.Add(placement.Depth);
            highestDepth = Math.Max(highestDepth, Math.Max(placement.Depth, placement.ClipDepth ?? 0));
        }
        int selectedDepth = depth ?? highestDepth + 1;
        if (selectedDepth is < 1 or > 16383)
            throw new ArgumentOutOfRangeException(nameof(depth), "Timeline depths must be between 1 and 16383.");
        if (usedDepths.Contains((ushort)selectedDepth))
            throw new InvalidOperationException("This depth is already used in the timeline. Choose an unused layer.");
        byte[] encoded = ScaleformPlacement.EncodeMatrix(matrix);
        byte[] payload = new byte[5 + encoded.Length];
        payload[0] = 6; // HasCharacter | HasMatrix, no event handlers or effects.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)selectedDepth);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3), characterId);
        encoded.CopyTo(payload, 5);
        var placed = CreateTimelineRecord(26, payload);
        InsertRecord(sprite, insertion, placed);
        return placed;
    }

    /// <summary>Hides the instance at a depth from the chosen frame onward by adding RemoveObject2.</summary>
    public ScaleformTag RemovePlacement(ushort depth, ushort? spriteId = null, int frame = 1)
    {
        if (Version < 3) throw new NotSupportedException("Removing placements requires SWF 3 or later.");
        var scene = ScaleformScene.Build(this, spriteId, frame);
        if (!scene.Items.Any(item => item.Depth == depth))
            throw new ArgumentException("There is no instance at this depth in the chosen frame.", nameof(depth));
        var (_, sprite, insertion) = FindInsertion(scene, spriteId, frame);
        byte[] payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, depth);
        var removal = CreateTimelineRecord(28, payload);
        InsertRecord(sprite, insertion, removal);
        return removal;
    }

    /// <summary>
    /// Moves an existing instance at the chosen frame, retaining its character, name,
    /// effects and event handlers. Earlier frames keep their original transforms.
    /// </summary>
    public ScaleformTag AddTransformKeyframe(ushort depth, ScaleformMatrix matrix, ushort? spriteId = null, int frame = 1)
    {
        var scene = ScaleformScene.Build(this, spriteId, frame);
        var item = scene.Items.FirstOrDefault(candidate => candidate.Depth == depth) ??
            throw new ArgumentException("There is no instance at this depth in the chosen frame.", nameof(depth));
        // Updating a keyframe that already exists keeps its opaque attributes together.
        if (item.PlacementFrame == frame)
        {
            item.PlacementTag.SetPlacementMatrix(matrix);
            return item.PlacementTag;
        }
        if (Version < 3) throw new NotSupportedException("Transform keyframes require SWF 3 or later.");
        var (_, sprite, insertion) = FindInsertion(scene, spriteId, frame);
        byte[] encoded = ScaleformPlacement.EncodeMatrix(matrix);
        byte[] payload = new byte[3 + encoded.Length];
        payload[0] = 5; // Move | HasMatrix. Every omitted property is inherited.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), depth);
        encoded.CopyTo(payload, 3);
        var keyframe = CreateTimelineRecord(26, payload);
        InsertRecord(sprite, insertion, keyframe);
        return keyframe;
    }

    private (IReadOnlyList<ScaleformTag> Timeline, ScaleformTag Sprite, int Insertion) FindInsertion(ScaleformScene scene, ushort? spriteId, int frame)
    {
        ScaleformTag sprite = spriteId.HasValue ? scene.Definitions[spriteId.Value] : null;
        var timeline = sprite?.Children ?? Tags;
        int current = 1;
        for (int i = 0; i < timeline.Count; i++)
            if (timeline[i].Code == 1 && current++ == frame) return (timeline, sprite, i);
        throw new InvalidDataException("The chosen timeline does not contain the selected ShowFrame record.");
    }

    private ScaleformTag CreateTimelineRecord(ushort code, byte[] payload)
    {
        if (CountRecords(Tags) >= MaximumTagCount)
            throw new InvalidOperationException("The movie has reached the maximum tag count.");
        bool longHeader = payload.Length >= 63;
        int headerLength = longHeader ? 6 : 2;
        byte[] record = new byte[payload.Length + headerLength];
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)((code << 6) | (longHeader ? 63 : payload.Length)));
        if (longHeader) BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(record, headerLength);
        int count = 0;
        return new ScaleformTag(code, record, record.AsMemory(headerLength), -1, headerLength, Version, 0, ref count);
    }

    private void InsertRecord(ScaleformTag sprite, int index, ScaleformTag record)
    {
        if (sprite == null)
        {
            timelineTags.Insert(index, record);
            timelineModified = true;
        }
        else sprite.InsertTimelineRecord(index, record);
    }

    private static int CountRecords(IReadOnlyList<ScaleformTag> tags) => tags.Sum(tag => 1 + CountRecords(tag.Children));

    private static bool IsVisualSymbol(ushort code) => code is 2 or 6 or 7 or 11 or 20 or 21 or 22 or 32 or 33 or 34
        or 35 or 36 or 37 or 39 or 46 or 60 or 83 or 84 or 90 or 1001 or 1008 or 1009;

    private static bool ReferencesSymbol(ushort characterId, ushort targetId, IReadOnlyDictionary<ushort, ScaleformTag> definitions, HashSet<ushort> visited)
    {
        if (characterId == targetId) return true;
        if (!visited.Add(characterId) || !definitions.TryGetValue(characterId, out var tag) || tag.Code != 39) return false;
        foreach (var child in tag.Children.Where(t => t.Code is 4 or 26 or 70))
        {
            ushort? reference = ScaleformPlacement.Read(child).CharacterId;
            if (reference.HasValue && ReferencesSymbol(reference.Value, targetId, definitions, visited)) return true;
        }
        return false;
    }
}
