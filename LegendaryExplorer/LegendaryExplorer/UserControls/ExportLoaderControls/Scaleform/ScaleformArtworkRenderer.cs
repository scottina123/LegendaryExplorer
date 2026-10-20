using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LegendaryExplorerCore.Scaleform;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>
/// Read-only native WPF preview of SWF artwork. Coordinates returned by this renderer are pixels.
/// It deliberately never runs ActionScript or alters records. Missing runtime/font/effect support is reported.
/// Drawing layouts follow the Adobe SWF File Format Specification 19; GFX image headers are additionally
/// verified against the embedded Mass Effect movies. No Flash player or Java process is required.
/// </summary>
public sealed class ScaleformArtworkRenderer
{
    private const int MaximumEdges = 250_000;
    private const int MaximumImagePixels = 32 * 1024 * 1024;
    private const int MaximumPreviewTextLength = 16_384;
    private readonly ScaleformDocument document;
    private ScaleformSceneIndex sceneIndex;
    private readonly Func<string, ImageSource> externalImageResolver;
    private readonly Func<string, string, int, Drawing> externalSymbolResolver;
    private readonly Dictionary<ushort, (string Movie, string Symbol)> imports = [];
    private readonly Dictionary<ushort, ScaleformTag> definitions;
    private readonly Dictionary<ushort, ImageData> images = [];
    private readonly Dictionary<ushort, ImageData> gradientImages = [];
    private readonly Dictionary<ushort, ExternalImage> externalImages = [];
    private readonly Dictionary<ushort, ExternalImage> externalGradients = [];
    private readonly Dictionary<(ushort, int), Drawing> drawings = [];
    private readonly Dictionary<(ushort?, int), Drawing> timelines = [];
    private readonly Dictionary<(ushort?, int), ScaleformScene> scenes = [];
    private readonly Dictionary<ushort, Rect> bounds = [];
    private readonly Dictionary<ushort, FontData> fonts = [];
    private Dictionary<ushort, string> fontNames;
    private readonly Dictionary<(ImageSource, ScaleformColorTransform), ImageSource> colorImages = [];
    private readonly Dictionary<(Drawing, ScaleformColorTransform), Drawing> colorDrawings = [];
    private readonly HashSet<ushort> rendering = [];
    private readonly List<string> warnings = [];
    private readonly HashSet<string> warningSet = [];
    public IReadOnlyList<string> Warnings => warnings;

    public ScaleformArtworkRenderer(ScaleformDocument document, Func<string, ImageSource> externalImageResolver = null,
        Func<string, string, int, Drawing> externalSymbolResolver = null, ScaleformArtworkRenderer previousArtwork = null)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.externalImageResolver = externalImageResolver;
        this.externalSymbolResolver = externalSymbolResolver;
        sceneIndex = new ScaleformSceneIndex(document);
        definitions = sceneIndex.Build().Definitions.ToDictionary(p => p.Key, p => p.Value);
        foreach (var tag in Flatten(document.Tags))
        {
            if (tag.Code is 1001 or 1003 or 1009)
            {
                try { ReadExternalImage(tag); }
                catch (Exception e) when (IsArtworkError(e)) { Warn($"{tag.Name}: {e.Message}"); }
            }
            if (tag.Code is 57 or 71)
            {
                try
                {
                    var reader = new ArtworkReader(tag.Payload); string movie = reader.String();
                    if (tag.Code == 71) { reader.U8(); reader.U8(); }
                    int count = reader.U16();
                    for (int i = 0; i < count; i++) { ushort id = reader.U16(); imports[id] = (movie, reader.String()); }
                }
                catch (Exception e) when (IsArtworkError(e)) { Warn($"{tag.Name}: {e.Message}"); }
            }
        }
        if (previousArtwork != null) ReuseArtwork(previousArtwork);
    }

    private void ReuseArtwork(ScaleformArtworkRenderer previous)
    {
        bool Unchanged(ushort id) => definitions.TryGetValue(id, out var tag) && tag.Code is not (39 or 7 or 34) &&
            previous.definitions.TryGetValue(id, out var old) && tag.Code == old.Code && tag.Payload.Span.SequenceEqual(old.Payload.Span);
        bool Resource(ScaleformTag tag) => tag.Code is 6 or 8 or 10 or 20 or 21 or 35 or 36 or 48 or 75 or 90 or 1001 or 1005 or 1008 or 1009;
        bool resourcesUnchanged = definitions.Where(pair => Resource(pair.Value)).Select(pair => pair.Key)
            .Union(previous.definitions.Where(pair => Resource(pair.Value)).Select(pair => pair.Key)).All(Unchanged);
        foreach (var pair in previous.images)
            if (Unchanged(pair.Key) || externalImages.TryGetValue(pair.Key, out var external) && previous.externalImages.GetValueOrDefault(pair.Key) == external)
                images[pair.Key] = pair.Value;
        foreach (var pair in previous.drawings)
            if (resourcesUnchanged && Unchanged(pair.Key.Item1)) drawings[pair.Key] = pair.Value;
        foreach (var pair in previous.bounds)
            if (Unchanged(pair.Key)) bounds[pair.Key] = pair.Value;
        foreach (var pair in previous.fonts)
            if (Unchanged(pair.Key)) fonts[pair.Key] = pair.Value;
        foreach (var pair in previous.colorImages) colorImages[pair.Key] = pair.Value;
        if (resourcesUnchanged)
        {
            var retained = new HashSet<Drawing>();
            void Keep(Drawing drawing)
            {
                if (!retained.Add(drawing)) return;
                if (drawing is DrawingGroup group) foreach (Drawing child in group.Children) Keep(child);
            }
            foreach (Drawing drawing in drawings.Values) Keep(drawing);
            foreach (var pair in previous.colorDrawings)
                if (retained.Contains(pair.Key.Item1)) colorDrawings[pair.Key] = pair.Value;
        }
    }

    /// <summary>Refreshes placement-dependent artwork after a visual edit, retaining parsed static resources.</summary>
    public void InvalidateScene()
    {
        scenes.Clear(); timelines.Clear();
        foreach (var key in drawings.Keys.Where(key => definitions.TryGetValue(key.Item1, out var tag) && tag.Code is 39 or 7 or 34).ToArray()) drawings.Remove(key);
        foreach (ushort key in bounds.Keys.Where(key => definitions.TryGetValue(key, out var tag) && tag.Code is 39 or 7 or 34).ToArray()) bounds.Remove(key);
    }

    /// <summary>A placement move changes its owning timeline and ancestors, not every symbol's artwork.</summary>
    public void InvalidatePlacement(ushort? ownerSpriteId)
    {
        scenes.Clear(); timelines.Clear();
        if (!ownerSpriteId.HasValue) return;
        var affected = new HashSet<ushort> { ownerSpriteId.Value };
        foreach (var pair in definitions.Where(pair => pair.Value.Code is 7 or 34)) affected.Add(pair.Key);
        bool added;
        do
        {
            added = false;
            foreach (var pair in definitions.Where(pair => pair.Value.Code == 39 && !affected.Contains(pair.Key)))
                if (pair.Value.Children.Where(tag => tag.Code is 4 or 26 or 70).Any(tag => DependsOnAffected(tag, affected)))
                    added |= affected.Add(pair.Key);
        } while (added);
        var removed = drawings.Where(pair => affected.Contains(pair.Key.Item1) ||
            definitions.TryGetValue(pair.Key.Item1, out var tag) && tag.Code is 7 or 34).Select(pair => pair.Key).ToArray();
        foreach (var key in removed)
        {
            Drawing old = drawings[key]; drawings.Remove(key);
            foreach (var color in colorDrawings.Keys.Where(key => ReferenceEquals(key.Item1, old)).ToArray()) colorDrawings.Remove(color);
        }
        foreach (ushort id in affected) bounds.Remove(id);
    }

    private static bool DependsOnAffected(ScaleformTag tag, HashSet<ushort> affected)
    {
        try { return ScaleformPlacement.Read(tag).CharacterId is ushort child && affected.Contains(child); }
        catch (InvalidDataException) { return false; }
    }

    public void InvalidateCharacter(ushort characterId)
    {
        foreach (var key in drawings.Keys.Where(key => key.Item1 == characterId).ToArray()) drawings.Remove(key);
        bounds.Remove(characterId);
        InvalidateScene();
    }

    /// <summary>Updates paths after inserting timeline records, retaining immutable artwork and decoded images.</summary>
    public void RefreshTimelineIndex(ushort? ownerSpriteId = null)
    {
        sceneIndex = new ScaleformSceneIndex(document);
        InvalidatePlacement(ownerSpriteId);
    }

    public ScaleformScene GetScene(ushort? spriteId = null, int frame = 1)
    {
        if (scenes.TryGetValue((spriteId, frame), out var cached)) return cached;
        return scenes[(spriteId, frame)] = sceneIndex.Build(spriteId, frame);
    }

    /// <summary>Draws a root or symbol timeline in its own pixel coordinates, without executing scripts.</summary>
    public Drawing RenderTimeline(ushort? spriteId = null, int frame = 1)
    {
        if (spriteId.HasValue) return RenderCharacter(spriteId.Value, frame);
        frame = Math.Clamp(frame, 1, Math.Max(1, (int)document.FrameCount));
        if (timelines.TryGetValue((spriteId, frame), out var cached)) return cached;
        Drawing drawing = ReadTimeline(spriteId, Math.Max(1, frame));
        if (drawing.CanFreeze) drawing.Freeze();
        return timelines[(spriteId, frame)] = drawing;
    }

    public Drawing RenderCharacter(ushort characterId, int frame = 1)
    {
        frame = Math.Max(1, frame);
        if (definitions.TryGetValue(characterId, out var definition))
            frame = definition.Code == 39 ? Math.Clamp(frame, 1, Math.Max(1, (int)definition.SpriteFrameCount)) : 1;
        if (drawings.TryGetValue((characterId, frame), out var cached)) return cached;
        if (rendering.Count >= 48 || !rendering.Add(characterId))
        {
            Warn($"Symbol {characterId} contains a recursive or excessively nested display list; its repeated contents are omitted.");
            return new DrawingGroup();
        }
        try
        {
            Drawing drawing;
            if (definitions.TryGetValue(characterId, out var tag))
            {
                drawing = tag.Code switch
                {
                    2 or 22 or 32 or 83 => ReadShape(tag),
                    39 => ReadSprite(characterId, frame),
                    11 or 33 => ReadStaticText(tag),
                    37 => ReadEditText(tag),
                    6 or 20 or 21 or 35 or 36 or 90 or 1001 or 1008 or 1009 => DrawImage(characterId),
                    7 or 34 => ReadButton(tag),
                    _ => Unsupported(characterId, tag.Name)
                };
            }
            else if (images.ContainsKey(characterId) || externalImages.ContainsKey(characterId)) drawing = DrawImage(characterId);
            else if (imports.TryGetValue(characterId, out var import) && externalSymbolResolver != null)
                drawing = externalSymbolResolver(import.Movie, import.Symbol, frame) ?? Unsupported(characterId, $"shared symbol '{import.Symbol}' from '{import.Movie}'");
            else drawing = Unsupported(characterId, "missing definition");
            if (drawing.CanFreeze) drawing.Freeze();
            drawings[(characterId, frame)] = drawing;
            return drawing;
        }
        catch (Exception e) when (IsArtworkError(e))
        {
            Warn($"Symbol {characterId} cannot be previewed: {e.Message}");
            var fallback = Outline(GetStoredBounds(characterId));
            drawings[(characterId, frame)] = fallback;
            return fallback;
        }
        finally { rendering.Remove(characterId); }
    }

    public Rect GetCharacterBounds(ushort characterId)
    {
        if (bounds.TryGetValue(characterId, out var result)) return result;
        result = GetStoredBounds(characterId);
        if (result.IsEmpty)
        {
            var drawing = RenderCharacter(characterId);
            result = drawing.Bounds;
        }
        if (!result.IsEmpty && (!double.IsFinite(result.Width) || !double.IsFinite(result.Height))) result = Rect.Empty;
        bounds[characterId] = result;
        return result;
    }

    private Rect GetStoredBounds(ushort id)
    {
        if (images.TryGetValue(id, out var image) && image != null) return new Rect(0, 0, image.Width, image.Height);
        if (externalImages.TryGetValue(id, out var external)) return new Rect(0, 0, external.Width, external.Height);
        if (!definitions.TryGetValue(id, out var tag)) return Rect.Empty;
        try
        {
            var reader = new ArtworkReader(tag.Payload); reader.U16();
            if (tag.Code is 2 or 22 or 32 or 83 or 11 or 33 or 37 or 46 or 84) return reader.Rectangle();
            if (tag.Code is 6 or 20 or 21 or 35 or 36 or 90)
            {
                var embedded = GetImage(id);
                return embedded == null ? Rect.Empty : new Rect(0, 0, embedded.Width, embedded.Height);
            }
        }
        catch (Exception e) when (IsArtworkError(e)) { Warn($"Symbol {id} bounds: {e.Message}"); }
        return Rect.Empty;
    }

    private Drawing ReadShape(ScaleformTag tag)
    {
        var r = new ArtworkReader(tag.Payload); r.U16(); r.Rectangle();
        bool winding = false;
        if (tag.Code == 83) { r.Rectangle(); winding = (r.U8() & 4) != 0; }
        int version = tag.Code switch { 2 => 1, 22 => 2, 32 => 3, _ => 4 };
        return ReadShapeRecords(r, version, true, winding);
    }

    private Drawing ReadShapeRecords(ArtworkReader r, int version, bool hasStyles, bool winding = false)
    {
        var fills = new List<Brush> { null };
        var lines = new List<Pen> { null };
        if (hasStyles) { ReadFills(r, version, fills); ReadLines(r, version, lines); }
        else fills.Add(Brushes.Black); // A font glyph has implicit fill style 1.
        int fillBits = (int)r.Bits(4), lineBits = (int)r.Bits(4), fillBase = 0, lineBase = 0;
        int fill0 = 0, fill1 = 0, line = 0, x = 0, y = 0, edgeCount = 0;
        var fillEdges = new Dictionary<int, List<Edge>>();
        var lineEdges = new Dictionary<int, List<Edge>>();
        var drawing = new DrawingGroup();
        while (true)
        {
            if (r.Bits(1) == 0)
            {
                int flags = (int)r.Bits(5);
                if (flags == 0) break;
                if ((flags & 1) != 0) { int n = (int)r.Bits(5); x = r.Signed(n); y = r.Signed(n); }
                if ((flags & 2) != 0) { int index = (int)r.Bits(fillBits); fill0 = index == 0 ? 0 : fillBase + index; }
                if ((flags & 4) != 0) { int index = (int)r.Bits(fillBits); fill1 = index == 0 ? 0 : fillBase + index; }
                if ((flags & 8) != 0) { int index = (int)r.Bits(lineBits); line = index == 0 ? 0 : lineBase + index; }
                if ((flags & 16) != 0)
                {
                    if (!hasStyles || version == 1) throw new InvalidDataException("Unexpected new styles in this shape.");
                    // Style indexes after StateNewStyles are relative to the newly appended arrays.
                    FlushEdges(); fillBase = fills.Count - 1; lineBase = lines.Count - 1;
                    r.Align(); ReadFills(r, version, fills); ReadLines(r, version, lines);
                    fillBits = (int)r.Bits(4); lineBits = (int)r.Bits(4);
                }
                continue;
            }
            if (++edgeCount > MaximumEdges) throw new InvalidDataException("The shape exceeds the preview edge limit.");
            bool straight = r.Bits(1) != 0;
            int nbits = (int)r.Bits(4) + 2;
            var start = new Point(x / 20.0, y / 20.0);
            Point control = default;
            if (straight)
            {
                if (r.Bits(1) != 0) { x = checked(x + r.Signed(nbits)); y = checked(y + r.Signed(nbits)); }
                else if (r.Bits(1) != 0) y = checked(y + r.Signed(nbits));
                else x = checked(x + r.Signed(nbits));
            }
            else
            {
                int cx = checked(x + r.Signed(nbits)), cy = checked(y + r.Signed(nbits));
                control = new Point(cx / 20.0, cy / 20.0);
                x = checked(cx + r.Signed(nbits)); y = checked(cy + r.Signed(nbits));
            }
            var edge = new Edge(start, new Point(x / 20.0, y / 20.0), control, !straight);
            // Opposite sides of the same edge must have opposite direction, preserving holes.
            Add(fillEdges, fill0, edge); Add(fillEdges, fill1, edge.Reverse()); Add(lineEdges, line, edge);
        }
        r.Align(); FlushEdges();
        return drawing;

        void FlushEdges()
        {
            foreach (var pair in fillEdges)
            {
                if (pair.Key >= fills.Count) throw new InvalidDataException("The shape references a missing fill style.");
                if (fills[pair.Key] != null) drawing.Children.Add(new GeometryDrawing(fills[pair.Key], null, MakeGeometry(pair.Value, true, winding)));
            }
            foreach (var pair in lineEdges)
            {
                if (pair.Key >= lines.Count) throw new InvalidDataException("The shape references a missing line style.");
                if (lines[pair.Key] != null) drawing.Children.Add(new GeometryDrawing(null, lines[pair.Key], MakeGeometry(pair.Value, false, false)));
            }
            fillEdges.Clear(); lineEdges.Clear();
        }
    }

    private static void Add(Dictionary<int, List<Edge>> collection, int style, Edge edge)
    {
        if (style == 0) return;
        if (!collection.TryGetValue(style, out var edges)) collection.Add(style, edges = []);
        edges.Add(edge);
    }

    private static Geometry MakeGeometry(List<Edge> edges, bool filled, bool winding)
    {
        var geometry = new StreamGeometry { FillRule = winding ? FillRule.Nonzero : FillRule.EvenOdd };
        // Styles can be interleaved and contours need not occur in path order. Join by exact twip endpoints.
        var starts = new Dictionary<Point, Queue<int>>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (!starts.TryGetValue(edges[i].Start, out var indexes)) starts.Add(edges[i].Start, indexes = []);
            indexes.Enqueue(i);
        }
        var used = new bool[edges.Count];
        using (var context = geometry.Open())
        {
            for (int i = 0; i < edges.Count; i++)
            {
                if (used[i]) continue;
                Point first = edges[i].Start;
                context.BeginFigure(first, filled, false);
                int index = i;
                while (true)
                {
                    var edge = edges[index]; used[index] = true;
                    if (edge.Curved) context.QuadraticBezierTo(edge.Control, edge.End, true, false);
                    else context.LineTo(edge.End, true, false);
                    if (edge.End == first || !starts.TryGetValue(edge.End, out var next)) break;
                    while (next.Count > 0 && used[next.Peek()]) next.Dequeue();
                    if (next.Count == 0) break;
                    index = next.Dequeue();
                }
            }
        }
        geometry.Freeze(); return geometry;
    }

    private void ReadFills(ArtworkReader r, int version, List<Brush> output)
    {
        int count = r.U8(); if (count == 255 && version >= 2) count = r.U16();
        for (int i = 0; i < count; i++) output.Add(ReadFill(r, version));
    }

    private Brush ReadFill(ArtworkReader r, int version)
    {
        int type = r.U8();
        if (type == 0) return new SolidColorBrush(r.Color(version >= 3));
        if (type is 0x10 or 0x12 or 0x13)
        {
            var matrix = r.Matrix(); int flags = r.U8(), count = flags & 15;
            var stops = new GradientStopCollection();
            for (int i = 0; i < count; i++) { double ratio = r.U8() / 255.0; stops.Add(new GradientStop(r.Color(version >= 3), ratio)); }
            double focal = type == 0x13 ? r.I16() / 256.0 : 0;
            GradientBrush brush = type == 0x10
                ? new LinearGradientBrush(stops, new Point(-819.2, 0), new Point(819.2, 0))
                : new RadialGradientBrush(stops) { Center = new Point(0, 0), GradientOrigin = new Point(focal * 819.2, 0), RadiusX = 819.2, RadiusY = 819.2 };
            brush.MappingMode = BrushMappingMode.Absolute;
            brush.SpreadMethod = ((flags >> 6) & 3) switch { 1 => GradientSpreadMethod.Reflect, 2 => GradientSpreadMethod.Repeat, _ => GradientSpreadMethod.Pad };
            brush.ColorInterpolationMode = ((flags >> 4) & 3) == 1 ? ColorInterpolationMode.ScRgbLinearInterpolation : ColorInterpolationMode.SRgbLinearInterpolation;
            brush.Transform = new MatrixTransform(matrix);
            return brush;
        }
        if (type is >= 0x40 and <= 0x43)
        {
            ushort id = r.U16(); var matrix = r.Matrix();
            if (id == ushort.MaxValue) return null; // SWF defines 65535 as a non-rendering bitmap fill.
            var image = GetImage(id);
            if (image?.Source == null) { Warn($"Bitmap fill {id} is unavailable in this package; its fill is omitted."); return null; }
            // Bitmap matrices map image pixels to twips; object matrices map pixels to pixels.
            matrix.M11 /= 20; matrix.M12 /= 20; matrix.M21 /= 20; matrix.M22 /= 20;
            var brush = new ImageBrush(image.Source)
            {
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, image.Width, image.Height),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect(0, 0, 1, 1),
                Stretch = Stretch.Fill, TileMode = type is 0x40 or 0x42 ? TileMode.Tile : TileMode.None,
                Transform = new MatrixTransform(matrix)
            };
            RenderOptions.SetBitmapScalingMode(brush, type >= 0x42 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            return brush;
        }
        throw new InvalidDataException($"Unsupported fill type 0x{type:X2}.");
    }

    private void ReadLines(ArtworkReader r, int version, List<Pen> output)
    {
        int count = r.U8(); if (count == 255 && version >= 2) count = r.U16();
        for (int i = 0; i < count; i++)
        {
            double width = r.U16() / 20.0;
            if (version < 4) { output.Add(new Pen(new SolidColorBrush(r.Color(version >= 3)), Math.Max(width, 0.05))); continue; }
            int startCap = (int)r.Bits(2), join = (int)r.Bits(2);
            bool filled = r.Bits(1) != 0, noHScale = r.Bits(1) != 0, noVScale = r.Bits(1) != 0, pixelHinting = r.Bits(1) != 0;
            r.Bits(5); bool noClose = r.Bits(1) != 0; int endCap = (int)r.Bits(2);
            double miter = join == 2 ? r.U16() / 256.0 : 10;
            Brush brush = filled ? ReadFill(r, version) : new SolidColorBrush(r.Color(true));
            output.Add(brush == null ? null : new Pen(brush, Math.Max(width, 0.05))
            {
                StartLineCap = Cap(startCap), EndLineCap = Cap(endCap),
                LineJoin = join switch { 1 => PenLineJoin.Bevel, 2 => PenLineJoin.Miter, _ => PenLineJoin.Round }, MiterLimit = Math.Max(1, miter)
            });
            if (noHScale || noVScale || pixelHinting || noClose) Warn("Some shape strokes use pixel hinting or scale/cap rules that WPF approximates.");
        }
    }
    private static PenLineCap Cap(int cap) => cap switch { 1 => PenLineCap.Flat, 2 => PenLineCap.Square, _ => PenLineCap.Round };

    private Drawing ReadSprite(ushort id, int frame) => ReadTimeline(id, frame);

    private Drawing ReadTimeline(ushort? id, int frame)
    {
        int count = id.HasValue && definitions.TryGetValue(id.Value, out var sprite) && sprite.Code == 39
            ? sprite.SpriteFrameCount : document.FrameCount;
        frame = Math.Clamp(frame, 1, Math.Max(1, count));
        var scene = GetScene(id, frame);
        foreach (string warning in scene.Warnings) Warn(warning);
        var result = new DrawingGroup();
        var masks = new List<(ushort End, Geometry Geometry)>();
        foreach (var item in scene.Items)
        {
            masks.RemoveAll(mask => mask.End < item.Depth);
            if (!item.Visible) continue;
            Drawing character = RenderCharacter(item.CharacterId, frame);
            var m = item.Matrix;
            var transformed = new DrawingGroup { Transform = new MatrixTransform(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY) };
            transformed.Children.Add(character);
            if (item.ClipDepth is > 0)
            {
                var mask = ExtractGeometry(transformed);
                masks.Add((item.ClipDepth.Value, mask));
                continue;
            }
            if (item.ColorTransform != ScaleformColorTransform.Identity)
                transformed.Children[0] = ApplyColorTransform(character, item.ColorTransform);
            if (masks.Count > 0)
            {
                Geometry clip = masks[0].Geometry;
                for (int i = 1; i < masks.Count; i++) clip = new CombinedGeometry(GeometryCombineMode.Intersect, clip, masks[i].Geometry);
                var clipped = new DrawingGroup { ClipGeometry = clip }; clipped.Children.Add(transformed); result.Children.Add(clipped);
            }
            else result.Children.Add(transformed);
            if (item.HasEffects && item.ColorTransform == ScaleformColorTransform.Identity)
                Warn("Runtime filters and blend modes are preserved in the movie; the viewport approximates those effects.");
        }
        return result;
    }

    private Drawing ApplyColorTransform(Drawing drawing, ScaleformColorTransform color)
    {
        if (colorDrawings.TryGetValue((drawing, color), out var cached)) return cached;
        Drawing result = TransformDrawing(drawing, color);
        if (result.CanFreeze) result.Freeze();
        if (colorDrawings.Count >= 8192) colorDrawings.Clear();
        return colorDrawings[(drawing, color)] = result;
    }

    private Drawing TransformDrawing(Drawing drawing, ScaleformColorTransform color)
    {
        bool alphaOnly = color.RedMultiplier == 1 && color.GreenMultiplier == 1 && color.BlueMultiplier == 1 &&
                         color.RedOffset == 0 && color.GreenOffset == 0 && color.BlueOffset == 0 && color.AlphaOffset == 0 &&
                         color.AlphaMultiplier is >= 0 and <= 1;
        if (alphaOnly)
        {
            var opacity = new DrawingGroup { Opacity = color.AlphaMultiplier }; opacity.Children.Add(drawing); return opacity;
        }
        if (drawing is GeometryDrawing geometry)
        {
            Pen pen = geometry.Pen?.Clone(); if (pen != null) pen.Brush = TransformBrush(pen.Brush, color);
            return new GeometryDrawing(TransformBrush(geometry.Brush, color), pen, geometry.Geometry);
        }
        if (drawing is ImageDrawing image) return new ImageDrawing(TransformImage(image.ImageSource, color), image.Rect);
        if (drawing is GlyphRunDrawing glyph) return new GlyphRunDrawing(TransformBrush(glyph.ForegroundBrush, color), glyph.GlyphRun);
        if (drawing is DrawingGroup group)
        {
            // Clone() recursively copies the entire frozen subtree before it is thrown
            // away. Retain immutable layout resources and transform each child once.
            var result = new DrawingGroup
            {
                Transform = group.Transform, ClipGeometry = group.ClipGeometry, Opacity = group.Opacity,
                OpacityMask = group.OpacityMask, GuidelineSet = group.GuidelineSet
            };
            foreach (Drawing child in group.Children) result.Children.Add(ApplyColorTransform(child, color));
            return result;
        }
        return drawing;
    }

    private Brush TransformBrush(Brush original, ScaleformColorTransform color)
    {
        if (original == null) return null;
        Brush result = original.Clone();
        if (result is SolidColorBrush solid) solid.Color = TransformColor(solid.Color, color);
        else if (result is GradientBrush gradient)
            foreach (var stop in gradient.GradientStops) stop.Color = TransformColor(stop.Color, color);
        else if (result is ImageBrush image) image.ImageSource = TransformImage(image.ImageSource, color);
        return result;
    }

    private ImageSource TransformImage(ImageSource source, ScaleformColorTransform color)
    {
        if (source == null) return null;
        if (colorImages.TryGetValue((source, color), out var cached)) return cached;
        if (source is not BitmapSource bitmap) { Warn("A vector image color transform cannot be reproduced in the viewport."); return source; }
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaximumImagePixels) throw new InvalidDataException("The transformed image exceeds the preview pixel limit.");
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = checked(bitmap.PixelWidth * 4); var pixels = new byte[checked(stride * bitmap.PixelHeight)]; converted.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = Channel(pixels[i], color.BlueMultiplier, color.BlueOffset);
            pixels[i + 1] = Channel(pixels[i + 1], color.GreenMultiplier, color.GreenOffset);
            pixels[i + 2] = Channel(pixels[i + 2], color.RedMultiplier, color.RedOffset);
            pixels[i + 3] = Channel(pixels[i + 3], color.AlphaMultiplier, color.AlphaOffset);
        }
        var transformed = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        transformed.Freeze(); colorImages[(source, color)] = transformed; return transformed;
    }

    private static Color TransformColor(Color original, ScaleformColorTransform color) => Color.FromArgb(
        Channel(original.A, color.AlphaMultiplier, color.AlphaOffset), Channel(original.R, color.RedMultiplier, color.RedOffset),
        Channel(original.G, color.GreenMultiplier, color.GreenOffset), Channel(original.B, color.BlueMultiplier, color.BlueOffset));
    private static byte Channel(byte channel, double multiplier, double offset) => (byte)Math.Clamp((int)Math.Round(channel * multiplier + offset), 0, 255);

    private static Geometry ExtractGeometry(Drawing drawing)
    {
        if (drawing is GeometryDrawing shape) return shape.Geometry;
        if (drawing is ImageDrawing image) return new RectangleGeometry(image.Rect);
        if (drawing is GlyphRunDrawing glyph) return glyph.GlyphRun.BuildGeometry();
        if (drawing is not DrawingGroup group) return Geometry.Empty;
        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (Drawing child in group.Children) geometry.Children.Add(ExtractGeometry(child));
        Geometry clipped = group.ClipGeometry == null ? geometry : new CombinedGeometry(GeometryCombineMode.Intersect, geometry, group.ClipGeometry);
        return new GeometryGroup { Transform = group.Transform, Children = new GeometryCollection { clipped } };
    }

    private Drawing ReadButton(ScaleformTag tag)
    {
        var r = new ArtworkReader(tag.Payload); r.U16();
        if (tag.Code == 34) { r.U8(); r.U16(); }
        var result = new DrawingGroup();
        while (true)
        {
            int flags = r.U8(); if (flags == 0) break;
            ushort character = r.U16(); r.U16(); var matrix = r.Matrix();
            if ((flags & 1) != 0)
            {
                var child = new DrawingGroup { Transform = new MatrixTransform(matrix) };
                child.Children.Add(RenderCharacter(character)); result.Children.Add(child);
            }
            if (tag.Code == 34)
            {
                bool add = r.Bits(1) != 0, mult = r.Bits(1) != 0; int bits = (int)r.Bits(4);
                if (mult) for (int i = 0; i < 4; i++) r.Signed(bits);
                if (add) for (int i = 0; i < 4; i++) r.Signed(bits);
                r.Align();
                if ((flags & 0x30) != 0) throw new InvalidDataException("Filtered button artwork is not supported in this preview.");
                if (add || mult) Warn("Button color effects are preserved; the preview shows its normal-state artwork.");
            }
        }
        return result;
    }

    private Drawing ReadEditText(ScaleformTag tag)
    {
        var r = new ArtworkReader(tag.Payload); ushort id = r.U16(); Rect rectangle = r.Rectangle();
        int f1 = r.U8(), f2 = r.U8(); bool hasFont = (f1 & 1) != 0;
        ushort fontId = hasFont ? r.U16() : (ushort)0;
        string fontClass = (f2 & 0x80) != 0 ? r.String() : null;
        double height = hasFont ? r.U16() / 20.0 : 12;
        Color color = (f1 & 4) != 0 ? r.Color(true) : Colors.White;
        if ((f1 & 2) != 0) r.U16();
        int alignment = 0; double left = 0, right = 0, indent = 0, leading = 0;
        if ((f2 & 0x20) != 0) { alignment = r.U8(); left = r.U16() / 20.0; right = r.U16() / 20.0; indent = r.U16() / 20.0; leading = r.I16() / 20.0; }
        r.String();
        string text = (f1 & 0x80) != 0 ? r.String() : "";
        if (string.IsNullOrEmpty(text)) return new DrawingGroup();
        if ((f2 & 2) != 0)
        {
            text = Regex.Replace(text, @"<(br\s*/?|/p)\s*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            text = WebUtility.HtmlDecode(Regex.Replace(text, @"<!--[\s\S]*?-->|<(?:""[^""]*""|'[^']*'|[^'"">])*>", "", RegexOptions.None, TimeSpan.FromSeconds(1)));
        }
        if (text.Length > MaximumPreviewTextLength)
        {
            // Cap layout work, including the per-grapheme tracking preview. The movie text is untouched.
            int length = MaximumPreviewTextLength;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            text = text[..length];
            Warn($"Text field {id} exceeds the preview text limit; only its first {length} characters are shown.");
        }
        double letterSpacing = 0;
        string formattedFontName = null;
        if (tag.CanEditTextFormat)
        {
            var format = tag.GetTextFormat();
            fontId = format.FontId ?? fontId;
            height = format.FontSize;
            color = Color.FromArgb((byte)format.Color, (byte)(format.Color >> 24), (byte)(format.Color >> 16), (byte)(format.Color >> 8));
            alignment = (int)format.Alignment;
            letterSpacing = format.LetterSpacing;
            formattedFontName = format.FontName;
        }
        var font = GetFont(fontId);
        string fontName = !string.IsNullOrWhiteSpace(formattedFontName) ? formattedFontName : font?.Name ?? fontClass;
        if (string.IsNullOrWhiteSpace(fontName) && fontId != 0)
        {
            fontNames ??= document.GetElementFonts().ToDictionary(candidate => candidate.Id, candidate => candidate.Name);
            fontName = fontNames.GetValueOrDefault(fontId);
        }
        var typeface = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(fontName) ? "Segoe UI" : fontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!double.IsFinite(height) || height <= 0) height = 12;
        height = Math.Min(height, ushort.MaxValue / 20.0);
        double width = Math.Max(1, rectangle.Width - left - right);
        var brush = new SolidColorBrush(color);
        var result = new DrawingGroup();
        using (var dc = result.Open())
        {
            dc.PushClip(new RectangleGeometry(rectangle));
            var origin = new Point(rectangle.X + left + indent + 2, rectangle.Y + 2);
            if (double.IsFinite(letterSpacing) && letterSpacing != 0)
                DrawTrackedText(dc, text, typeface, height, brush, Math.Clamp(letterSpacing, -512, 512), width, rectangle.Bottom, origin, alignment, leading);
            else
            {
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, height, brush, 1)
                {
                    MaxTextWidth = width, MaxTextHeight = Math.Max(1, rectangle.Height),
                    TextAlignment = alignment switch { 1 => TextAlignment.Right, 2 => TextAlignment.Center, 3 => TextAlignment.Justify, _ => TextAlignment.Left },
                    Trimming = TextTrimming.None
                };
                if (leading > -height && height + leading > 0) formatted.LineHeight = height + leading;
                dc.DrawText(formatted, origin);
            }
            dc.Pop();
        }
        Warn($"Text field {id} uses an approximate system-font preview; in-game font substitution, mixed HTML styles and script-provided text may differ.");
        return result;
    }

    private static void DrawTrackedText(DrawingContext dc, string text, Typeface typeface, double height, Brush brush,
        double spacing, double width, double bottom, Point origin, int alignment, double leading)
    {
        // Tracking is added between complete Unicode text elements, preserving surrogate pairs and
        // combining marks. Drawing individual elements changes advances without stretching glyphs.
        var cache = new Dictionary<string, FormattedText>();
        var glyphs = new List<(FormattedText Text, bool Space, bool Newline)>();
        var elements = StringInfo.GetTextElementEnumerator(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        while (elements.MoveNext())
        {
            string value = elements.GetTextElement();
            if (value == "\n") { glyphs.Add((null, false, true)); continue; }
            if (!cache.TryGetValue(value, out var formatted))
            {
                formatted = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, height, brush, 1);
                cache[value] = formatted;
            }
            glyphs.Add((formatted, string.IsNullOrWhiteSpace(value), false));
        }
        double lineHeight = height + leading > 0 ? height + leading : height;
        int start = 0;
        double y = origin.Y;
        while (start < glyphs.Count && y < bottom)
        {
            int end = start, next, breakNext = -1, breakEnd = start, contentEnd = start;
            double pen = 0, lineWidth = 0, contentWidth = 0, breakWidth = 0;
            bool wrapped = false;
            while (end < glyphs.Count && !glyphs[end].Newline)
            {
                var glyph = glyphs[end];
                double candidate = Math.Max(lineWidth, pen + glyph.Text.WidthIncludingTrailingWhitespace);
                if (candidate > width && end > start)
                {
                    wrapped = true;
                    break;
                }
                lineWidth = candidate;
                pen += Math.Max(0, glyph.Text.WidthIncludingTrailingWhitespace + spacing);
                end++;
                if (glyph.Space)
                {
                    breakNext = end; breakEnd = contentEnd; breakWidth = contentWidth;
                }
                else { contentEnd = end; contentWidth = lineWidth; }
            }
            if (wrapped && breakNext > start)
            {
                next = breakNext; end = breakEnd; lineWidth = breakWidth;
            }
            else
            {
                next = end;
                if (!wrapped && next < glyphs.Count && glyphs[next].Newline) next++;
                end = contentEnd; lineWidth = contentWidth;
            }
            if (wrapped) while (next < glyphs.Count && glyphs[next].Space) next++;
            double x = origin.X + (alignment switch { 1 => width - lineWidth, 2 => (width - lineWidth) / 2, _ => 0 });
            int spaces = 0;
            if (alignment == 3 && wrapped)
                for (int i = start; i < end; i++) if (glyphs[i].Space) spaces++;
            double extra = spaces > 0 ? Math.Max(0, width - lineWidth) / spaces : 0;
            for (int i = start; i < end; i++)
            {
                dc.DrawText(glyphs[i].Text, new Point(x, y));
                x += Math.Max(0, glyphs[i].Text.WidthIncludingTrailingWhitespace + spacing);
                if (glyphs[i].Space) x += extra;
            }
            start = next;
            y += lineHeight;
        }
    }

    private Drawing ReadStaticText(ScaleformTag tag)
    {
        var r = new ArtworkReader(tag.Payload); ushort id = r.U16(); r.Rectangle(); var matrix = r.Matrix();
        int glyphBits = r.U8(), advanceBits = r.U8();
        if (glyphBits > 32 || advanceBits > 32) throw new InvalidDataException("The static text glyph sizes are invalid.");
        var result = new DrawingGroup { Transform = new MatrixTransform(matrix) };
        ushort fontId = 0; double height = 0; int x = 0, y = 0; Color color = Colors.Black;
        int glyphCount = 0;
        while (true)
        {
            int flags = r.U8(); if (flags == 0) break;
            if ((flags & 0x80) == 0) throw new InvalidDataException("The static text record has an invalid type.");
            if ((flags & 8) != 0) fontId = r.U16();
            if ((flags & 4) != 0) color = r.Color(tag.Code == 33);
            if ((flags & 1) != 0) x = r.I16();
            if ((flags & 2) != 0) y = r.I16();
            if ((flags & 8) != 0) height = r.U16();
            int count = r.U8();
            var font = GetFont(fontId);
            for (int i = 0; i < count; i++)
            {
                int glyph = (int)r.Bits(glyphBits), advance = r.Signed(advanceBits);
                if (++glyphCount > MaximumEdges) throw new InvalidDataException("The text exceeds the preview glyph limit.");
                if (font != null && glyph < font.Glyphs.Count)
                {
                    double scale = height / (font.IsFont3 ? 20480.0 : 1024.0);
                    var drawing = new GeometryDrawing(new SolidColorBrush(color), null, font.Glyphs[glyph]);
                    var placed = new DrawingGroup { Transform = new MatrixTransform(scale, 0, 0, scale, x / 20.0, y / 20.0) };
                    placed.Children.Add(drawing); result.Children.Add(placed);
                }
                else Warn($"Static text {id} needs font {fontId}, glyph {glyph}; that embedded font is unavailable and the glyph is omitted.");
                x = checked(x + advance);
            }
            r.Align();
        }
        return result;
    }

    private FontData GetFont(ushort id)
    {
        if (fonts.TryGetValue(id, out var cached)) return cached;
        fonts[id] = null;
        if (!definitions.TryGetValue(id, out var tag)) return null;
        if (tag.Code == 1005) { Warn($"Font {id} uses proprietary compacted glyphs; embedded glyph rendering is unavailable."); return null; }
        if (tag.Code is not (10 or 48 or 75)) return null;
        try
        {
            var r = new ArtworkReader(tag.Payload); r.U16(); string name = null;
            int count, offsetStart; bool wide = false; int[] offsets; int end;
            if (tag.Code == 10)
            {
                offsetStart = r.Position; int first = r.U16(); count = first / 2;
                if ((first & 1) != 0 || count == 0 || count > 32767) throw new InvalidDataException("The font glyph offset table is invalid.");
                offsets = new int[count]; offsets[0] = first;
                for (int i = 1; i < count; i++) offsets[i] = r.U16();
                end = r.Length - offsetStart;
            }
            else
            {
                int flags = r.U8(); wide = (flags & 8) != 0; r.U8(); name = r.SizedString(); count = r.U16();
                offsetStart = r.Position; offsets = new int[count];
                for (int i = 0; i < count; i++) offsets[i] = checked((int)(wide ? r.U32() : r.U16()));
                end = checked((int)(wide ? r.U32() : r.U16()));
            }
            var font = new FontData { Name = name, IsFont3 = tag.Code == 75 };
            for (int i = 0; i < count; i++)
            {
                int next = i + 1 < count ? offsets[i + 1] : end;
                if (next < offsets[i] || offsets[i] < 0 || offsetStart > r.Length - next) throw new InvalidDataException("The font glyph offsets exceed its record.");
                if (next == offsets[i]) { font.Glyphs.Add(Geometry.Empty); continue; }
                var glyph = new ArtworkReader(tag.Payload.Slice(offsetStart + offsets[i], next - offsets[i]));
                font.Glyphs.Add(ExtractGeometry(ReadShapeRecords(glyph, 1, false)));
            }
            fonts[id] = font; return font;
        }
        catch (Exception e) when (IsArtworkError(e)) { Warn($"Font {id} cannot be previewed: {e.Message}"); return null; }
    }

    private void ReadExternalImage(ScaleformTag tag)
    {
        var r = new ArtworkReader(tag.Payload);
        uint imageId = tag.Code == 1009 ? r.U32() : r.U16();
        r.U16(); int width = r.U16(), height = tag.Code == 1003 ? 1 : r.U16();
        string exportName = tag.Code == 1009 ? r.SizedString() : null;
        string filename = r.SizedString();
        // GFX image IDs carry resource-type bits above the low 16-bit SWF character ID.
        if (imageId > ushort.MaxValue) { Warn($"External image {imageId} uses an indirect resource ID; it is not previewed as a character."); return; }
        if (width == 0 || height == 0) throw new InvalidDataException("An external image has zero dimensions.");
        var external = new ExternalImage(filename, exportName, width, height);
        if (tag.Code == 1003) externalGradients[(ushort)imageId] = external;
        else externalImages[(ushort)imageId] = external;
    }

    private ImageData ResolveExternalImage(ExternalImage external, bool gradient)
    {
        ImageSource source = externalImageResolver?.Invoke(external.Filename);
        if (source == null && !string.IsNullOrWhiteSpace(external.ExportName)) source = externalImageResolver?.Invoke(external.ExportName);
        if (source == null && !gradient) Warn($"External image '{external.Filename}' is not available in this package; its artwork is omitted.");
        return new ImageData(source, external.Width, external.Height);
    }

    private ImageData GetImage(ushort id)
    {
        if (images.TryGetValue(id, out var cached)) return cached;
        if (externalImages.TryGetValue(id, out var external)) return images[id] = ResolveExternalImage(external, false);
        images[id] = null;
        if (!definitions.TryGetValue(id, out var tag))
        {
            if (!gradientImages.TryGetValue(id, out var gradient) && externalGradients.TryGetValue(id, out var externalGradient))
                gradientImages[id] = gradient = ResolveExternalImage(externalGradient, true);
            return images[id] = gradient;
        }
        if (tag.Code is not (6 or 20 or 21 or 35 or 36 or 90 or 1008)) return null;
        try
        {
            var r = new ArtworkReader(tag.Payload); r.U16(); BitmapSource source;
            if (tag.Code == 1008)
            {
                ushort parentId = r.U16(); int left = r.U16(), top = r.U16(), right = r.U16(), bottom = r.U16();
                var parent = GetImage(parentId);
                if (right <= left || bottom <= top || parent?.Source is not BitmapSource parentBitmap)
                    throw new InvalidDataException("The atlas subimage or its parent bitmap is unavailable.");
                var pixelRect = new Int32Rect((int)Math.Round(left * parentBitmap.PixelWidth / (double)parent.Width), (int)Math.Round(top * parentBitmap.PixelHeight / (double)parent.Height),
                    (int)Math.Round((right - left) * parentBitmap.PixelWidth / (double)parent.Width), (int)Math.Round((bottom - top) * parentBitmap.PixelHeight / (double)parent.Height));
                var cropped = new CroppedBitmap(parentBitmap, pixelRect); cropped.Freeze();
                images[id] = new ImageData(cropped, right - left, bottom - top); return images[id];
            }
            if (tag.Code is 20 or 36) source = ReadLossless(r, tag.Code == 36);
            else
            {
                int alphaOffset = tag.Code is 35 or 90 ? checked((int)r.U32()) : r.Length - r.Position;
                if (tag.Code == 90) r.U16();
                byte[] jpeg = r.Bytes(alphaOffset);
                if (tag.Code == 6)
                {
                    var tables = Flatten(document.Tags).FirstOrDefault(t => t.Code == 8);
                    if (tables != null) jpeg = JoinJpeg(tables.Payload.ToArray(), jpeg);
                }
                // Older encoders can prefix a spurious EOI/SOI pair.
                if (jpeg.Length > 4 && jpeg[0] == 255 && jpeg[1] == 217 && jpeg[2] == 255 && jpeg[3] == 216) jpeg = jpeg[4..];
                using var stream = new MemoryStream(jpeg);
                source = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                if ((long)source.PixelWidth * source.PixelHeight > MaximumImagePixels) throw new InvalidDataException("The bitmap exceeds the preview pixel limit.");
                if (tag.Code is 35 or 90)
                {
                    var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                    int stride = checked(converted.PixelWidth * 4); byte[] pixels = new byte[checked(stride * converted.PixelHeight)]; converted.CopyPixels(pixels, stride, 0);
                    byte[] alpha = Inflate(r.Bytes(r.Length - r.Position), checked(source.PixelWidth * source.PixelHeight));
                    for (int i = 0; i < alpha.Length; i++) pixels[i * 4 + 3] = alpha[i];
                    source = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                }
            }
            source.Freeze();
            images[id] = new ImageData(source, source.PixelWidth, source.PixelHeight);
            return images[id];
        }
        catch (Exception e) when (IsArtworkError(e)) { Warn($"Bitmap {id} cannot be previewed: {e.Message}"); return null; }
    }

    private static BitmapSource ReadLossless(ArtworkReader r, bool alpha)
    {
        int format = r.U8(), width = r.U16(), height = r.U16();
        if (width == 0 || height == 0 || (long)width * height > MaximumImagePixels) throw new InvalidDataException("The bitmap dimensions exceed the preview limit.");
        int palette = format == 3 ? r.U8() + 1 : 0;
        int packedStride = format switch { 3 => (width + 3) & ~3, 4 => (checked(width * 2) + 3) & ~3, 5 => checked(width * 4), _ => throw new InvalidDataException($"Unknown lossless bitmap format {format}.") };
        int paletteStride = alpha ? 4 : 3;
        byte[] raw = Inflate(r.Bytes(r.Length - r.Position), checked(palette * paletteStride + packedStride * height));
        int stride = checked(width * 4); byte[] pixels = new byte[checked(stride * height)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int destination = y * stride + x * 4, source = palette * paletteStride + y * packedStride;
            if (format == 3)
            {
                int index = raw[source + x]; if (index >= palette) throw new InvalidDataException("A bitmap palette index is out of range.");
                int p = index * paletteStride; pixels[destination] = raw[p + 2]; pixels[destination + 1] = raw[p + 1]; pixels[destination + 2] = raw[p]; pixels[destination + 3] = alpha ? raw[p + 3] : (byte)255;
            }
            else if (format == 4)
            {
                int value = raw[source + x * 2] | (raw[source + x * 2 + 1] << 8);
                pixels[destination] = (byte)((value & 31) * 255 / 31); pixels[destination + 1] = (byte)(((value >> 5) & 31) * 255 / 31); pixels[destination + 2] = (byte)(((value >> 10) & 31) * 255 / 31); pixels[destination + 3] = 255;
            }
            else
            {
                source += x * 4; pixels[destination] = raw[source + 3]; pixels[destination + 1] = raw[source + 2]; pixels[destination + 2] = raw[source + 1]; pixels[destination + 3] = alpha ? raw[source] : (byte)255;
            }
        }
        return BitmapSource.Create(width, height, 96, 96, alpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, pixels, stride);
    }

    private Drawing DrawImage(ushort id)
    {
        var image = GetImage(id);
        return image?.Source == null ? new DrawingGroup() : new ImageDrawing(image.Source, new Rect(0, 0, image.Width, image.Height));
    }
    private static byte[] JoinJpeg(byte[] tables, byte[] image)
    {
        int firstLength = tables.Length >= 2 && tables[^2] == 255 && tables[^1] == 217 ? tables.Length - 2 : tables.Length;
        int secondStart = image.Length >= 2 && image[0] == 255 && image[1] == 216 ? 2 : 0;
        byte[] result = new byte[firstLength + image.Length - secondStart];
        tables.AsSpan(0, firstLength).CopyTo(result); image.AsSpan(secondStart).CopyTo(result.AsSpan(firstLength)); return result;
    }
    private static byte[] Inflate(byte[] input, int expected)
    {
        if (expected < 0 || expected > MaximumImagePixels * 4 + 1024) throw new InvalidDataException("The bitmap decompression limit is exceeded.");
        using var stream = new MemoryStream(input); using var zlib = new ZLibStream(stream, CompressionMode.Decompress);
        byte[] result = new byte[expected]; zlib.ReadExactly(result);
        if (zlib.ReadByte() != -1) throw new InvalidDataException("The decompressed bitmap length differs from its dimensions.");
        return result;
    }
    private Drawing Unsupported(ushort id, string type)
    {
        Warn($"Symbol {id} ({type}) has no artwork preview; its stored bounds are shown where available.");
        return Outline(GetStoredBounds(id));
    }
    private static Drawing Outline(Rect rectangle)
    {
        if (rectangle.IsEmpty || rectangle.Width == 0 || rectangle.Height == 0) return new DrawingGroup();
        return new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 210, 161, 75)), 0.75) { DashStyle = DashStyles.Dash }, new RectangleGeometry(rectangle));
    }
    private void Warn(string warning)
    {
        if (!warningSet.Add(warning)) return;
        if (warnings.Count < 250) warnings.Add(warning);
        else if (warnings.Count == 250) warnings.Add("Additional artwork warnings were omitted.");
    }
    private static bool IsArtworkError(Exception e) => e is InvalidDataException or IOException or ArgumentException or NotSupportedException or OverflowException or FormatException or InvalidOperationException or RegexMatchTimeoutException;
    private static IEnumerable<ScaleformTag> Flatten(IEnumerable<ScaleformTag> tags)
    {
        foreach (var tag in tags) { yield return tag; foreach (var child in Flatten(tag.Children)) yield return child; }
    }
    private readonly record struct Edge(Point Start, Point End, Point Control, bool Curved) { public Edge Reverse() => new(End, Start, Control, Curved); }
    private sealed record ImageData(ImageSource Source, int Width, int Height);
    private sealed record ExternalImage(string Filename, string ExportName, int Width, int Height);
    private sealed class FontData { public string Name; public bool IsFont3; public List<Geometry> Glyphs = []; }
}
