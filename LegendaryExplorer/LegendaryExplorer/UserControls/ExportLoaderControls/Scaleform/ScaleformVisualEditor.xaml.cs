using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorerCore.Scaleform;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>A direct-manipulation editor. Only deliberate edits are sent to the binary draft.</summary>
public partial class ScaleformVisualEditor : UserControl
{
    private ScaleformDocument source;
    private byte[] sourceBytes;
    private ScaleformDocument working;
    private ScaleformArtworkRenderer renderer;
    private ScaleformScene scene;
    private ScaleformVisualDraft draft;
    private IDictionary<string, string> textEdits;
    private Func<string, ImageSource> resolveImage;
    private Func<string, string, int, Drawing> resolveSymbol;
    private readonly Dictionary<ScaleformTag, string> identities = new();
    private readonly Dictionary<string, ScaleformTag> tags = new();
    private readonly ConditionalWeakTable<ScaleformVisualDraft, ArtworkSnapshot> artworkSnapshots = new();
    private List<LibrarySymbol> library = new();
    private bool updating;
    private Point libraryDragStart;
    private ScaleformMatrix? gestureOriginal;
    private ScaleformMatrix? gesturePreviousEdit;
    private string gestureIdentity;
    private bool gestureHadEdit;
    private string[] propertySnapshot = Array.Empty<string>();
    private bool fieldCommitSucceeded = true;

    public event EventHandler DraftChanged;
    public event Action<string> StatusChanged;
    public IReadOnlyList<VisualElement> VisibleElements { get; private set; } = Array.Empty<VisualElement>();
    public VisualElement SelectedElement { get; private set; }
    public ScaleformVisualDraft Draft => draft;
    public ScaleformDesignSurface Viewport => CanvasViewport;
    public ScaleformDocument PreviewDocument => working;
    public ScaleformElementCreator Creator => ElementCreator;

    public ScaleformVisualEditor()
    {
        InitializeComponent();
        CanvasViewport.Editor = this;
    }

    public void LoadMovie(ScaleformDocument document, Func<string, ImageSource> externalResolver,
        ScaleformVisualDraft visualDraft, IDictionary<string, string> pendingTextEdits,
        Func<string, string, int, Drawing> externalSymbolResolver = null)
    {
        source = document; sourceBytes = document.Compile(); resolveImage = externalResolver; draft = visualDraft; textEdits = pendingTextEdits; resolveSymbol = externalSymbolResolver;
        renderer = artworkSnapshots.TryGetValue(visualDraft, out var savedArtwork) && savedArtwork.SourceBytes.AsSpan().SequenceEqual(sourceBytes)
            ? savedArtwork.Renderer : null;
        RebuildWorking();
        CanvasViewport.Fit();
    }

    public void ClearMovie()
    {
        source = working = null; sourceBytes = null; renderer = null; draft = null;
        SelectedElement = null; VisibleElements = Array.Empty<VisualElement>();
        identities.Clear(); tags.Clear(); library.Clear();
        LayersTree.ItemsSource = null; LibraryList.ItemsSource = null;
        UpdateProperties(); CanvasViewport.InvalidateVisual();
    }

    // Original index paths are resolved before inserting records, so adding an instance
    // cannot redirect an existing edit to the next tag in the file.
    public static void ApplyDraft(ScaleformDocument document, ScaleformVisualDraft visualDraft, IDictionary<string, string> pendingTextEdits = null)
    {
        foreach (var edit in visualDraft.Matrices.Where(edit => !edit.Key.StartsWith('+'))) ScaleformScene.FindTag(document, edit.Key).SetPlacementMatrix(edit.Value);
        foreach (var add in visualDraft.Additions)
        {
            if (add.NewElement != null)
            {
                var created = document.AddElementDefinition(add.NewElement, add.SpriteId); add.CharacterId = created.CharacterId;
                foreach (var generated in GeneratedTags(created.Definition, add.Id + "/definition"))
                    if (visualDraft.Matrices.TryGetValue(generated.Path, out var matrix)) generated.Tag.SetPlacementMatrix(matrix);
                if (created.ContentTag.CanEditText && pendingTextEdits?.TryGetValue(add.Id + "/content", out var text) == true) created.ContentTag.SetText(text);
            }
            var tag = document.AddPlacement(add.CharacterId, add.Matrix, add.SpriteId, add.Frame, add.Depth);
            if (!string.IsNullOrEmpty(add.Name)) tag.SetPlacementName(add.Name);
            add.Depth ??= ScaleformPlacement.Read(tag).Depth;
        }
        foreach (var key in visualDraft.Keyframes) document.AddTransformKeyframe(key.Depth, key.Matrix, key.SpriteId, key.Frame);
        foreach (var remove in visualDraft.Removals) document.RemovePlacement(remove.Depth, remove.SpriteId, remove.Frame);
    }

    private void RebuildWorking()
    {
        if (source == null) return;
        string previous = SelectedElement?.Identity;
        var previousScope = SelectedElement?.OwnerSpriteId;
        ushort? previousDepth = SelectedElement?.Depth;
        working = ScaleformDocument.Parse(sourceBytes);
        identities.Clear(); tags.Clear();
        Index(working.Tags, "");
        foreach (var edit in textEdits)
            if (tags.TryGetValue(edit.Key, out var tag) && tag.CanEditText) tag.SetText(edit.Value);
        foreach (var edit in draft.Matrices.Where(edit => !edit.Key.StartsWith('+'))) tags[edit.Key].SetPlacementMatrix(edit.Value);
        foreach (var add in draft.Additions)
        {
            if (add.NewElement != null)
            {
                var created = working.AddElementDefinition(add.NewElement, add.SpriteId); add.CharacterId = created.CharacterId;
                foreach (var generated in GeneratedTags(created.Definition, add.Id + "/definition"))
                {
                    identities[generated.Tag] = generated.Path; tags[generated.Path] = generated.Tag;
                    if (draft.Matrices.TryGetValue(generated.Path, out var matrix)) generated.Tag.SetPlacementMatrix(matrix);
                }
                string contentPath = add.Id + "/content";
                identities[created.ContentTag] = contentPath; tags[contentPath] = created.ContentTag;
                if (created.ContentTag.CanEditText && textEdits.TryGetValue(contentPath, out var text)) created.ContentTag.SetText(text);
            }
            var tag = working.AddPlacement(add.CharacterId, add.Matrix, add.SpriteId, add.Frame, add.Depth);
            if (!string.IsNullOrEmpty(add.Name)) tag.SetPlacementName(add.Name);
            add.Depth ??= ScaleformPlacement.Read(tag).Depth;
            identities[tag] = add.Id; tags[add.Id] = tag;
        }
        foreach (var key in draft.Keyframes)
        {
            var tag = working.AddTransformKeyframe(key.Depth, key.Matrix, key.SpriteId, key.Frame);
            identities[tag] = key.Id; tags[key.Id] = tag;
        }
        foreach (var remove in draft.Removals) working.RemovePlacement(remove.Depth, remove.SpriteId, remove.Frame);
        renderer = new ScaleformArtworkRenderer(working, resolveImage, resolveSymbol, renderer);
        var snapshot = artworkSnapshots.GetValue(draft, _ => new ArtworkSnapshot());
        snapshot.SourceBytes = sourceBytes; snapshot.Renderer = renderer;
        RefreshView(previous, previousScope, previousDepth, true);
    }

    public void ReloadDraft() => RebuildWorking();

    private static IEnumerable<(string Path, ScaleformTag Tag)> GeneratedTags(ScaleformTag definition, string path)
    {
        yield return (path, definition);
        for (int i = 0; i < definition.Children.Count; i++)
            foreach (var child in GeneratedTags(definition.Children[i], path + "/" + i)) yield return child;
    }

    private void RefreshView(string previous = null, ushort? previousScope = null, ushort? previousDepth = null, bool rebuildLibrary = false)
    {
        if (draft.SpriteId.HasValue && (!renderer.GetScene().Definitions.TryGetValue(draft.SpriteId.Value, out var scope) || scope.Code != 39))
        {
            // Undoing creation while inside that group returns to the movie canvas.
            draft.SpriteId = null; draft.Frame = 1; draft.InitialFrameChosen = false;
        }
        if (!draft.InitialFrameChosen)
        {
            if (draft.Frame == 1) draft.Frame = ChooseAuthoringFrame(working, draft.SpriteId);
            draft.InitialFrameChosen = true;
        }
        var overview = renderer.GetScene(draft.SpriteId);
        draft.Frame = Math.Clamp(draft.Frame, 1, overview.FrameCount);
        scene = renderer.GetScene(draft.SpriteId, draft.Frame);
        VisibleElements = BuildElements(scene, Matrix.Identity, draft.SpriteId, draft.Frame, new HashSet<ushort>(), 0);
        SelectedElement = AllElements(VisibleElements).FirstOrDefault(e => e.Identity == previous)
            ?? (previousDepth.HasValue ? AllElements(VisibleElements).FirstOrDefault(e => e.OwnerSpriteId == previousScope && e.Depth == previousDepth) : null);
        updating = true;
        LayersTree.ItemsSource = VisibleElements;
        FrameSlider.Maximum = scene.FrameCount; FrameSlider.Value = draft.Frame;
        FrameLabel.Text = $"Frame {draft.Frame} / {scene.FrameCount}";
        ScopeLabel.Text = draft.SpriteId.HasValue ? "Inside " + GetSymbolName(draft.SpriteId.Value, overview) : "Movie canvas";
        updating = false;
        if (rebuildLibrary)
        {
            var libraryArtwork = renderer;
            var libraryDocument = working;
            library = overview.Definitions.Where(p => p.Value.Code is 2 or 6 or 7 or 11 or 20 or 21 or 22 or 32 or 33 or 34 or 35 or 36 or 37 or 39 or 83 or 90 or 1001 or 1009)
                .Select(p => new LibrarySymbol(p.Key, GetSymbolName(p.Key, overview), () => libraryArtwork.RenderCharacter(p.Key,
                    p.Value.Code == 39 ? ChooseAuthoringFrame(libraryDocument, p.Key, libraryArtwork) : 1))).OrderBy(p => p.Name).ToList();
            FilterLibrary();
        }
        UpdateProperties(); CanvasViewport.InvalidateVisual();
        if (SelectedElement != null) SelectLayerContainer(LayersTree, VisibleElements, SelectedElement);
    }

    private void Index(IReadOnlyList<ScaleformTag> records, string prefix)
    {
        for (int i = 0; i < records.Count; i++)
        {
            string path = prefix + i; tags[path] = records[i]; identities[records[i]] = path;
            Index(records[i].Children, path + "/");
        }
    }

    private List<VisualElement> BuildElements(ScaleformScene current, Matrix parent, ushort? ownerId, int frame, HashSet<ushort> ancestors, int depth)
    {
        var result = new List<VisualElement>();
        if (depth > 32) return result;
        foreach (var item in current.Items)
        {
            Matrix world = ToWpf(item.Matrix); world.Append(parent);
            var element = new VisualElement(item, identities.GetValueOrDefault(item.PlacementTag, item.PlacementPath), parent, ownerId, frame);
            Rect bounds = item.Definition?.Code == 39 ? renderer.RenderCharacter(item.CharacterId, frame).Bounds : renderer.GetCharacterBounds(item.CharacterId);
            element.LocalBounds = bounds.IsEmpty || !double.IsFinite(bounds.Width) ? new Rect(-10, -10, 20, 20) : bounds;
            element.WorldBounds = TransformBounds(element.LocalBounds, world);
            if (item.Definition?.Code == 39 && !ancestors.Contains(item.CharacterId))
            {
                var nested = renderer.GetScene(item.CharacterId);
                int nestedFrame = Math.Min(frame, nested.FrameCount);
                var nextAncestors = new HashSet<ushort>(ancestors) { item.CharacterId };
                element.Children = BuildElements(renderer.GetScene(item.CharacterId, nestedFrame), world, item.CharacterId, nestedFrame, nextAncestors, depth + 1);
            }
            result.Add(element);
        }
        return result;
    }

    private static string GetSymbolName(ushort id, ScaleformScene current)
    {
        if (current.SymbolNames.TryGetValue(id, out var name)) return name;
        var instance = current.Items.FirstOrDefault(i => i.CharacterId == id);
        if (instance != null) return instance.Name;
        if (!current.Definitions.TryGetValue(id, out var definition)) return $"Artwork {id}";
        string kind = definition.Code switch { 39 => "Group", 37 or 11 or 33 => "Text", 7 or 34 => "Button", 2 or 22 or 32 or 83 => "Shape", _ => "Image" };
        string text = definition.CanEditText ? definition.TextValue : string.Empty;
        if (text.Length > 24) text = text[..24] + "…";
        return text.Length == 0 ? $"{kind} {id}" : $"{kind}: {text}";
    }

    public bool SelectElement(string identity)
    {
        var element = AllElements(VisibleElements).FirstOrDefault(e => e.Identity == identity);
        if (element == null) return false;
        Select(element); return true;
    }

    internal void Select(VisualElement element)
    {
        SelectedElement = element; UpdateProperties(); CanvasViewport.InvalidateVisual();
        if (element != null && LayersTree.SelectedItem != element) SelectLayerContainer(LayersTree, VisibleElements, element);
    }

    private static bool SelectLayerContainer(ItemsControl control, IReadOnlyList<VisualElement> elements, VisualElement selected)
    {
        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            bool contains = ReferenceEquals(element, selected) || AllElements(element.Children).Any(child => ReferenceEquals(child, selected));
            if (!contains) continue;
            control.UpdateLayout();
            if (control.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem container) return false;
            if (ReferenceEquals(element, selected)) { container.IsSelected = true; container.BringIntoView(); return true; }
            container.IsExpanded = true; container.UpdateLayout();
            return SelectLayerContainer(container, element.Children, selected);
        }
        return false;
    }

    public void MoveSelectedBy(double dx, double dy)
    {
        if (SelectedElement == null) return;
        Matrix inverse = SelectedElement.ParentMatrix;
        if (!inverse.HasInverse) { StatusChanged?.Invoke("This group has no usable size. Open its parent group to resize it first."); return; }
        inverse.Invert(); Vector delta = inverse.Transform(new Vector(dx, dy));
        var matrix = SelectedElement.Matrix;
        ChangeMatrix(matrix with { OffsetX = matrix.OffsetX + delta.X, OffsetY = matrix.OffsetY + delta.Y });
    }

    public void ResizeSelected(double width, double height)
    {
        if (SelectedElement == null) return;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be greater than zero.");
        var value = SelectedElement.Matrix;
        double oldWidth = Math.Max(0.001, SelectedElement.LocalBounds.Width * Math.Sqrt(value.M11 * value.M11 + value.M12 * value.M12));
        double oldHeight = Math.Max(0.001, SelectedElement.LocalBounds.Height * Math.Sqrt(value.M21 * value.M21 + value.M22 * value.M22));
        ChangeMatrix(value with { M11 = value.M11 * width / oldWidth, M12 = value.M12 * width / oldWidth,
            M21 = value.M21 * height / oldHeight, M22 = value.M22 * height / oldHeight });
    }

    private void ChangeMatrix(ScaleformMatrix value, bool record = true)
    {
        if (SelectedElement == null || SelectedElement.Matrix == value) return;
        var selected = SelectedElement;
        var addition = draft.Additions.FirstOrDefault(a => a.Id == selected.Identity);
        var keyframe = draft.Keyframes.FirstOrDefault(k => k.Id == selected.Identity);
        if (selected.Item.PlacementFrame < selected.Frame && keyframe == null)
        {
            var key = new ScaleformVisualKeyframe { Depth = selected.Depth, Frame = selected.Frame, SpriteId = selected.OwnerSpriteId, Matrix = value };
            // Insert a real keyframe directly into the isolated preview. Re-index paths
            // without discarding already decoded artwork when dragging starts.
            var tag = working.AddTransformKeyframe(key.Depth, value, key.SpriteId, key.Frame);
            identities[tag] = key.Id; tags[key.Id] = tag;
            draft.Keyframes.Add(key);
            if (record) draft.Record(() => draft.Keyframes.Remove(key), () => draft.Keyframes.Add(key));
            renderer.RefreshTimelineIndex(key.SpriteId);
            RefreshView(key.Id, key.SpriteId, key.Depth);
            DraftChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        else if (addition != null)
        {
            selected.Item.PlacementTag.SetPlacementMatrix(value);
            var old = addition.Matrix; addition.Matrix = value;
            if (record) draft.Record(() => addition.Matrix = old, () => addition.Matrix = value);
        }
        else if (keyframe != null)
        {
            selected.Item.PlacementTag.SetPlacementMatrix(value);
            var old = keyframe.Matrix; keyframe.Matrix = value;
            if (record) draft.Record(() => keyframe.Matrix = old, () => keyframe.Matrix = value);
        }
        else
        {
            selected.Item.PlacementTag.SetPlacementMatrix(value);
            bool existed = draft.Matrices.TryGetValue(selected.Identity, out var old);
            draft.Matrices[selected.Identity] = value;
            if (record) draft.Record(() => { if (existed) draft.Matrices[selected.Identity] = old; else draft.Matrices.Remove(selected.Identity); },
                () => draft.Matrices[selected.Identity] = value);
        }
        if (SelectedElement != null)
        {
            SelectedElement.Matrix = value;
            UpdateElementBounds(SelectedElement);
        }
        renderer.InvalidatePlacement(selected.OwnerSpriteId);
        if (record) RefreshView(SelectedElement?.Identity, SelectedElement?.OwnerSpriteId, SelectedElement?.Depth);
        else { UpdateProperties(); CanvasViewport.InvalidateVisual(); }
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateElementBounds(VisualElement element)
    {
        var world = ToWpf(element.Matrix); world.Append(element.ParentMatrix);
        element.WorldBounds = TransformBounds(element.LocalBounds, world);
        foreach (var child in element.Children)
        {
            child.ParentMatrix = world; UpdateElementBounds(child);
        }
    }

    internal void BeginGesture()
    {
        if (SelectedElement == null) return;
        gestureOriginal = SelectedElement.Matrix; gestureIdentity = SelectedElement.Identity;
        gestureHadEdit = draft.Matrices.TryGetValue(gestureIdentity, out var old);
        gesturePreviousEdit = gestureHadEdit ? old : null;
    }

    internal void PreviewGesture(ScaleformMatrix matrix)
    {
        try { ChangeMatrix(matrix, false); }
        catch (Exception ex) { StatusChanged?.Invoke("Cannot move this element: " + ex.Message); }
    }

    internal void EndGesture()
    {
        if (!gestureOriginal.HasValue || SelectedElement == null) return;
        var oldValue = gestureOriginal.Value; var newValue = SelectedElement.Matrix;
        string id = SelectedElement.Identity;
        if (oldValue != newValue)
        {
            var addition = draft.Additions.FirstOrDefault(a => a.Id == id);
            var key = draft.Keyframes.FirstOrDefault(k => k.Id == id);
            if (addition != null) draft.Record(() => addition.Matrix = oldValue, () => addition.Matrix = newValue);
            else if (key != null)
            {
                bool created = id != gestureIdentity;
                draft.Record(() => { if (created) draft.Keyframes.Remove(key); else key.Matrix = oldValue; },
                    () => { key.Matrix = newValue; if (created && !draft.Keyframes.Contains(key)) draft.Keyframes.Add(key); });
            }
            else
            {
                bool had = gestureHadEdit; var previous = gesturePreviousEdit;
                draft.Record(() => { if (had) draft.Matrices[id] = previous.Value; else draft.Matrices.Remove(id); }, () => draft.Matrices[id] = newValue);
            }
        }
        else if (SelectedElement.Identity != gestureIdentity)
        {
            var unusedKeyframe = draft.Keyframes.FirstOrDefault(k => k.Id == SelectedElement.Identity);
            if (unusedKeyframe != null)
            {
                draft.Keyframes.Remove(unusedKeyframe); gestureOriginal = null; RebuildWorking(); return;
            }
        }
        else if (!SelectedElement.Identity.StartsWith('+') && !SelectedElement.Identity.StartsWith('~'))
        {
            if (gestureHadEdit) draft.Matrices[gestureIdentity] = gesturePreviousEdit.Value;
            else draft.Matrices.Remove(gestureIdentity);
        }
        gestureOriginal = null;
        RefreshView(SelectedElement?.Identity, SelectedElement?.OwnerSpriteId, SelectedElement?.Depth);
    }

    public void AddSymbolAt(ushort characterId, double x, double y)
    {
        if (working == null) return;
        var addition = new ScaleformVisualAddition { CharacterId = characterId, Name = NextElementName("element"), SpriteId = draft.SpriteId, Frame = draft.Frame,
            Matrix = ScaleformMatrix.Identity with { OffsetX = x, OffsetY = y } };
        // Reject invalid placements (including recursive groups) without retaining a broken draft.
        var check = ScaleformDocument.Parse(working.Compile());
        check.AddPlacement(characterId, addition.Matrix, addition.SpriteId, addition.Frame);
        draft.Additions.Add(addition);
        draft.Record(() => draft.Additions.Remove(addition), () => draft.Additions.Add(addition));
        RebuildWorking(); SelectElement(addition.Id); DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddNewElement(ScaleformElementSpec spec, double x, double y)
    {
        if (working == null) return;
        ArgumentNullException.ThrowIfNull(spec);
        spec = new ScaleformElementSpec { Kind = spec.Kind, Name = spec.Name, Width = spec.Width, Height = spec.Height, Color = spec.Color,
            Text = spec.Text, FontId = spec.FontId, FontSize = spec.FontSize, Pixels = spec.Pixels, PixelWidth = spec.PixelWidth, PixelHeight = spec.PixelHeight };
        spec.Name = NextElementName(spec.Name);
        var addition = new ScaleformVisualAddition { NewElement = spec, Name = NextElementName(spec.Name), SpriteId = draft.SpriteId, Frame = draft.Frame,
            Matrix = ScaleformMatrix.Identity with { OffsetX = x, OffsetY = y } };
        var check = ScaleformDocument.Parse(working.Compile());
        var created = check.AddElementDefinition(spec, addition.SpriteId);
        check.AddPlacement(created.CharacterId, addition.Matrix, addition.SpriteId, addition.Frame).SetPlacementName(addition.Name);
        draft.Additions.Add(addition);
        draft.Record(() => draft.Additions.Remove(addition), () => draft.Additions.Add(addition));
        RebuildWorking(); SelectElement(addition.Id); DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private string NextElementName(string preferred)
    {
        preferred = Regex.Replace(preferred ?? "element", "[^A-Za-z0-9_]", "_");
        if (preferred.Length > 90) preferred = preferred[..90];
        if (preferred.Length == 0 || !char.IsLetter(preferred[0]) && preferred[0] != '_') preferred = "element" + preferred;
        var used = scene.Items.Select(item => item.InstanceName).ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(preferred)) return preferred;
        for (int i = 2; ; i++) if (!used.Contains(preferred + i)) return preferred + i;
    }

    public void RemoveSelected()
    {
        if (SelectedElement == null) return;
        var selected = SelectedElement;
        var addition = draft.Additions.FirstOrDefault(a => a.Id == selected.Identity);
        // Authored symbols remain available in Library even when their first instance
        // is removed. Other instances and edits inside the symbol may still use it.
        if (addition is { NewElement: null } && selected.Frame == addition.Frame &&
            !draft.Keyframes.Any(k => k.Depth == addition.Depth && k.SpriteId == addition.SpriteId) &&
            !draft.Removals.Any(r => r.Depth == addition.Depth && r.SpriteId == addition.SpriteId))
        {
            draft.Additions.Remove(addition);
            draft.Record(() => draft.Additions.Add(addition), () => draft.Additions.Remove(addition));
        }
        else
        {
            var removal = new ScaleformVisualRemoval(selected.Depth, selected.OwnerSpriteId, selected.Frame);
            draft.Removals.Add(removal); draft.Record(() => draft.Removals.Remove(removal), () => draft.Removals.Add(removal));
        }
        SelectedElement = null; RebuildWorking(); DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplySelectedText(string text)
    {
        var definition = SelectedElement?.Item.Definition;
        if (definition?.CanEditText != true || !identities.TryGetValue(definition, out string path)) return;
        string old = textEdits.TryGetValue(path, out var edited) ? edited : definition.TextValue;
        bool html = IsHtmlText(definition);
        if (text == PlainText(old, html)) return;
        string encoded = PreserveTextFormatting(old, text, html);
        bool existed = textEdits.ContainsKey(path);
        definition.SetText(encoded);
        textEdits[path] = encoded;
        draft.Record(() => { if (existed) textEdits[path] = old; else textEdits.Remove(path); }, () => textEdits[path] = encoded);
        renderer.InvalidateCharacter(SelectedElement.CharacterId);
        RefreshView(SelectedElement.Identity, SelectedElement.OwnerSpriteId, SelectedElement.Depth); DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (draft?.UndoHistory.Count is not > 0) return;
        var change = draft.UndoHistory.Pop(); change.Undo(); draft.RedoHistory.Push(change);
        RebuildWorking(); DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (draft?.RedoHistory.Count is not > 0) return;
        var change = draft.RedoHistory.Pop(); change.Redo(); draft.UndoHistory.Push(change);
        RebuildWorking(); DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetFrame(int frame)
    {
        if (draft == null) return;
        if (draft.Frame == frame) return;
        draft.Frame = frame; SelectedElement = null; RefreshView();
    }

    public void EnterGroup(ushort? spriteId)
    {
        if (draft == null) return;
        draft.SpriteId = spriteId; draft.Frame = ChooseAuthoringFrame(working, spriteId); SelectedElement = null; RefreshView(); CanvasViewport.Fit();
    }

    // Intro/outro frames can intentionally have zero opacity. Open a visible authoring
    // pose, while keeping every original frame available on the timeline.
    private int ChooseAuthoringFrame(ScaleformDocument document, ushort? spriteId, ScaleformArtworkRenderer artwork = null)
    {
        artwork ??= renderer;
        var initial = artwork.GetScene(spriteId);
        if (initial.FrameCount == 1) return 1;
        double initialScore = VisibleInkScore(artwork.RenderTimeline(spriteId, 1));
        int chosen = 1; double best = initialScore;
        var frames = Enumerable.Range(0, 9).Select(i => 1 + (int)Math.Round(i * (initial.FrameCount - 1) / 8.0)).Distinct();
        foreach (int frame in frames)
        {
            double score = VisibleInkScore(artwork.RenderTimeline(spriteId, frame));
            if (score > best + .001) { best = score; chosen = frame; }
        }
        return initialScore >= best * .9 ? 1 : chosen;
    }

    private static double VisibleInkScore(Drawing drawing)
    {
        if (drawing == null) return 0;
        if (drawing is DrawingGroup group)
            return group.Opacity < .01 ? 0 : group.Children.Sum(VisibleInkScore) * group.Opacity * Math.Abs(group.Transform?.Value.Determinant ?? 1);
        if (drawing.Bounds.IsEmpty) return 0;
        double opacity = drawing is GeometryDrawing { Brush: SolidColorBrush solid } ? solid.Color.A / 255.0 * solid.Opacity : 1;
        return Math.Max(0, drawing.Bounds.Width * drawing.Bounds.Height * opacity);
    }

    internal Drawing GetDrawing() => renderer?.RenderTimeline(draft.SpriteId, draft.Frame);
    internal Brush GetBackground()
    {
        var tag = working?.Tags.FirstOrDefault(t => t.Code == 9);
        return tag?.Payload.Length == 3 ? new SolidColorBrush(Color.FromRgb(tag.Payload.Span[0], tag.Payload.Span[1], tag.Payload.Span[2]))
            : new SolidColorBrush(Color.FromRgb(24, 31, 40));
    }
    internal void FocusSelectedText() { if (TextPanel.Visibility == Visibility.Visible) { VisualText.Focus(); VisualText.SelectAll(); } }
    internal Rect GetStageBounds() => working == null ? new Rect(0, 0, 1280, 720) :
        draft.SpriteId.HasValue ? NonEmpty(renderer.RenderCharacter(draft.SpriteId.Value, draft.Frame).Bounds) : new Rect(0, 0, Math.Max(1, working.StageWidth), Math.Max(1, working.StageHeight));
    internal IReadOnlyList<string> PreviewWarnings => renderer?.Warnings ?? Array.Empty<string>();
    internal bool HasVisibleElements => draft != null && VisibleInkScore(renderer?.RenderTimeline(draft.SpriteId, draft.Frame)) > .01;
    internal void NotifyZoom(double zoom) => ZoomLabel.Text = $"{zoom * 100:0}%";
    internal void ReportStatus(string text) => StatusChanged?.Invoke(text);

    private void UpdateProperties()
    {
        updating = true;
        var element = SelectedElement;
        PropertiesPanel.IsEnabled = element != null;
        ElementName.Text = element?.Name ?? "Select an element";
        ElementInfo.Text = element == null ? "Click an item on the canvas or in Layers." :
            element.OwnerSpriteId.HasValue ? "Inside a reusable group. Editing its contents updates every instance of that group." : "Drag on the canvas to move this element.";
        var m = element?.Matrix ?? ScaleformMatrix.Identity;
        PositionX.Text = Format(m.OffsetX); PositionY.Text = Format(m.OffsetY);
        ElementWidth.Text = Format((element?.LocalBounds.Width ?? 0) * Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12));
        ElementHeight.Text = Format((element?.LocalBounds.Height ?? 0) * Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22));
        Rotation.Text = Format(Math.Atan2(m.M12, m.M11) * 180 / Math.PI);
        EditGroupButton.IsEnabled = element?.Item.Definition?.Code == 39;
        bool hasText = element?.Item.Definition?.CanEditText == true;
        TextPanel.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        VisualText.Text = hasText ? PlainText(element.Item.Definition.TextValue, IsHtmlText(element.Item.Definition)) : string.Empty;
        propertySnapshot = new[] { PositionX.Text, PositionY.Text, ElementWidth.Text, ElementHeight.Text, Rotation.Text };
        updating = false;
    }

    private void Transform_Commit(object sender, RoutedEventArgs e)
    {
        fieldCommitSucceeded = true;
        if (updating || SelectedElement == null) return;
        string[] fields = { PositionX.Text, PositionY.Text, ElementWidth.Text, ElementHeight.Text, Rotation.Text };
        if (fields.SequenceEqual(propertySnapshot)) return;
        try
        {
            var m = SelectedElement.Matrix;
            if (fields[0] != propertySnapshot[0]) m = m with { OffsetX = Number(fields[0]) };
            if (fields[1] != propertySnapshot[1]) m = m with { OffsetY = Number(fields[1]) };
            if (fields[2] != propertySnapshot[2])
            {
                double width = Number(fields[2]);
                if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be greater than zero.");
                double ratio = width / Math.Max(0.001, SelectedElement.LocalBounds.Width * Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12));
                m = m with { M11 = m.M11 * ratio, M12 = m.M12 * ratio };
            }
            if (fields[3] != propertySnapshot[3])
            {
                double height = Number(fields[3]);
                if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be greater than zero.");
                double ratio = height / Math.Max(0.001, SelectedElement.LocalBounds.Height * Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22));
                m = m with { M21 = m.M21 * ratio, M22 = m.M22 * ratio };
            }
            if (fields[4] != propertySnapshot[4])
            {
                double delta = Number(fields[4]) * Math.PI / 180 - Math.Atan2(m.M12, m.M11), cos = Math.Cos(delta), sin = Math.Sin(delta);
                m = m with { M11 = m.M11 * cos - m.M12 * sin, M12 = m.M11 * sin + m.M12 * cos,
                    M21 = m.M21 * cos - m.M22 * sin, M22 = m.M21 * sin + m.M22 * cos };
            }
            ChangeMatrix(m);
        }
        catch (Exception ex) { fieldCommitSucceeded = false; StatusChanged?.Invoke("Could not change the element: " + ex.Message); UpdateProperties(); }
    }

    /// <summary>Hosts flush focused number fields before navigation, apply, or close.</summary>
    public bool CommitPendingFields()
    {
        Transform_Commit(this, new RoutedEventArgs());
        return fieldCommitSucceeded;
    }

    private void Transform_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Transform_Commit(sender, e); e.Handled = true; CanvasViewport.Focus(); } }
    private void VisualText_Changed(object sender, TextChangedEventArgs e) { if (!updating) { try { ApplySelectedText(VisualText.Text); } catch (Exception ex) { StatusChanged?.Invoke(ex.Message); } } }
    private void Layer_Selected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (!updating && e.NewValue is VisualElement element) Select(element); }
    private void Layer_DoubleClick(object sender, MouseButtonEventArgs e) => EditGroup_Click(sender, e);
    private void EditGroup_Click(object sender, RoutedEventArgs e) { if (SelectedElement?.Item.Definition?.Code == 39) EnterGroup(SelectedElement.CharacterId); }
    private void Root_Click(object sender, RoutedEventArgs e) => EnterGroup(null);
    private void Frame_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!updating && draft != null) SetFrame((int)Math.Round(e.NewValue)); }
    private void Fit_Click(object sender, RoutedEventArgs e) => CanvasViewport.Fit();
    private void ActualSize_Click(object sender, RoutedEventArgs e) => CanvasViewport.ActualSize();
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Remove_Click(object sender, RoutedEventArgs e) { try { RemoveSelected(); } catch (Exception ex) { StatusChanged?.Invoke(ex.Message); } }
    private void Outlines_Changed(object sender, RoutedEventArgs e) { if (CanvasViewport != null) { CanvasViewport.ShowOutlines = OutlineToggle.IsChecked == true; CanvasViewport.InvalidateVisual(); } }
    private void LibrarySearch_Changed(object sender, TextChangedEventArgs e) { if (LibraryList != null) FilterLibrary(); }
    private void FilterLibrary() => LibraryList.ItemsSource = library.Where(s => s.Name.Contains(LibrarySearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    private void Library_MouseDown(object sender, MouseButtonEventArgs e) => libraryDragStart = e.GetPosition(LibraryList);
    private void Library_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || LibraryList.SelectedItem is not LibrarySymbol symbol || (e.GetPosition(LibraryList) - libraryDragStart).Length < 6) return;
        DragDrop.DoDragDrop(LibraryList, new DataObject("LegendaryExplorer.ScaleformSymbol", symbol.CharacterId), DragDropEffects.Copy);
    }
    private void Library_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryList.SelectedItem is not LibrarySymbol symbol) return;
        var center = CanvasViewport.ToWorld(new Point(CanvasViewport.ActualWidth / 2, CanvasViewport.ActualHeight / 2));
        TryAdd(symbol.CharacterId, center);
    }
    private void Canvas_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("LegendaryExplorer.ScaleformSymbol") || e.Data.GetDataPresent("LegendaryExplorer.ScaleformNewElement") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Canvas_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("LegendaryExplorer.ScaleformSymbol") is ushort id) TryAdd(id, CanvasViewport.ToWorld(e.GetPosition(CanvasViewport)));
        else if (e.Data.GetData("LegendaryExplorer.ScaleformNewElement") is ScaleformElementSpec spec)
        {
            var point = CanvasViewport.ToWorld(e.GetPosition(CanvasViewport));
            try { AddNewElement(spec, point.X, point.Y); }
            catch (Exception ex) { StatusChanged?.Invoke("Could not create this element: " + ex.Message); }
        }
        e.Handled = true;
    }
    private void TryAdd(ushort id, Point point) { try { AddSymbolAt(id, point.X, point.Y); } catch (Exception ex) { StatusChanged?.Invoke("Could not add this artwork here: " + ex.Message); } }
    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static string Format(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
    private static bool IsHtmlText(ScaleformTag tag)
    {
        if (tag.Code != 37 || tag.Payload.Length < 5) return false;
        int rectangleBytes = (5 + 4 * (tag.Payload.Span[2] >> 3) + 7) / 8;
        int flags = 2 + rectangleBytes;
        return flags + 1 < tag.Payload.Length && (tag.Payload.Span[flags + 1] & 2) != 0;
    }
    private static string PlainText(string text, bool html) => !html ? text : WebUtility.HtmlDecode(
        Regex.Replace(Regex.Replace(text, @"(?i)<br\s*/?>|</p\s*>", "\n"), "<[^>]*>", string.Empty)).TrimEnd('\r', '\n');
    private static string PreserveTextFormatting(string original, string text, bool html)
    {
        if (!html) return text;
        string encoded = WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br/>");
        if (!original.TrimStart().StartsWith('<')) return encoded;
        var opening = Regex.Match(original, @"^(?:\s*<[^/!][^>]*>)+");
        var closing = Regex.Match(original, @"(?:</[^>]+>\s*)+$");
        return opening.Success && closing.Success ? opening.Value + encoded + closing.Value : encoded;
    }
    internal static Matrix ToWpf(ScaleformMatrix m) => new(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY);
    internal static Rect TransformBounds(Rect r, Matrix m) => r.IsEmpty ? Rect.Empty : new MatrixTransform(m).TransformBounds(r);
    private static Rect NonEmpty(Rect bounds) => bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1 ? new Rect(0, 0, 1280, 720) : bounds;
    internal static IEnumerable<VisualElement> AllElements(IEnumerable<VisualElement> elements) => elements.SelectMany(e => new[] { e }.Concat(AllElements(e.Children)));

    public sealed class VisualElement(ScaleformSceneItem item, string identity, Matrix parentMatrix, ushort? ownerSpriteId, int frame)
    {
        public ScaleformSceneItem Item { get; } = item;
        public string Identity { get; } = identity;
        public string Name => Item.Name.StartsWith("Define", StringComparison.Ordinal) ? $"{Kind} {CharacterId}" : Item.Name;
        public string Kind => Item.Definition?.Code switch { 39 => "Group", 37 or 11 or 33 => "Text", 7 or 34 => "Button", 2 or 22 or 32 or 83 => "Shape", _ => "Artwork" };
        public ushort CharacterId => Item.CharacterId;
        public ushort Depth => Item.Depth;
        public ScaleformMatrix Matrix { get; internal set; } = item.Matrix;
        public Matrix ParentMatrix { get; internal set; } = parentMatrix;
        public Rect LocalBounds { get; internal set; }
        public Rect WorldBounds { get; internal set; }
        public ushort? OwnerSpriteId { get; } = ownerSpriteId;
        public int Frame { get; } = frame;
        public IReadOnlyList<VisualElement> Children { get; internal set; } = Array.Empty<VisualElement>();
    }

    public sealed class LibrarySymbol(ushort characterId, string name, Func<Drawing> artwork)
    {
        public ushort CharacterId { get; } = characterId;
        public string Name { get; } = name;
        private ImageSource thumbnail;
        public ImageSource Thumbnail => thumbnail ??= new DrawingImage(artwork());
    }

    private sealed class ArtworkSnapshot
    {
        public byte[] SourceBytes;
        public ScaleformArtworkRenderer Renderer;
    }
}
