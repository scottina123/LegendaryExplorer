using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Scaleform;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;
using Microsoft.Win32;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>A native, preservation-first movie inspector and editor inside Package Editor.</summary>
public partial class NativeScaleformExportLoader : ExportLoaderControl
{
    private readonly Dictionary<ExportEntry, MovieDraft> drafts = new();
    private MovieDraft draft;
    private TagNode selection;
    private string originalEditorText;
    private string displayedDraftText;
    private Window owner;
    private bool initialized;

    public bool HasMovie => draft != null;
    public bool HasSelection => selection != null;
    public string MovieSummary { get; private set; } = "Select a BioSWF or GFxMovieInfo export to begin.";
    public string MovieDetails { get; private set; }
    public string DependencySummary { get; private set; }
    public string SelectionTitle { get; private set; } = "Browse the movie";
    public string SelectionHelp { get; private set; } = "Choose a resource or script from the tree. Search by name, tag ID, category, or text.";
    public string Status { get; private set; } = "Draft edits stay in this editor until Compile & apply. Save the package afterward.";
    public IReadOnlyList<TagNode> VisibleTags { get; private set; } = Array.Empty<TagNode>();

    public NativeScaleformExportLoader() : base("Native Scaleform Editor")
    {
        InitializeComponent();
        DataContext = this;
        Designer.StatusChanged += SetStatus;
        Designer.DraftChanged += (_, _) => SetStatus("Design changed. Apply changes, then save the package.");
        initialized = true;
    }

    public override bool CanParse(ExportEntry exportEntry) =>
        exportEntry != null && !exportEntry.IsDefaultObject && exportEntry.ClassName is "BioSWF" or "GFxMovieInfo";

    public override void LoadExport(ExportEntry exportEntry)
    {
        bool previousFieldsValid = CaptureDraft();
        CurrentLoadedExport = exportEntry;
        ExternalEditor.LoadExport(exportEntry);
        try
        {
            if (!drafts.TryGetValue(exportEntry, out draft))
            {
                draft = new MovieDraft(new ScaleformExportSession(exportEntry));
                drafts.Add(exportEntry, draft);
            }
            else if (!draft.IsDirty && draft.Session.HasExternalChanges)
            {
                draft.Shared.Dispose();
                draft = new MovieDraft(new ScaleformExportSession(exportEntry));
                drafts[exportEntry] = draft;
            }
            ShowDraft();
            SetStatus(draft.Session.HasExternalChanges
                ? "This movie changed in another editor. Export your draft or reload before applying."
                : draft.IsDirty ? "Restored this movie’s design draft. Apply changes, then save the package."
                : "Drag elements on the canvas. Double-click a group to edit inside it, or drag artwork from Library.");
            if (!previousFieldsValid) SetStatus("The invalid property value was not applied. Opened this movie’s design draft.");
        }
        catch (Exception ex)
        {
            draft = null;
            ClearView();
            MovieSummary = exportEntry.InstancedFullPath;
            SetStatus($"Native editor could not open this movie: {ex.Message} The External editor tab remains available.");
            NotifyView();
        }
    }

    public override void UnloadExport()
    {
        CaptureDraft();
        draft = null;
        CurrentLoadedExport = null;
        ExternalEditor.UnloadExport();
        ClearView();
        NotifyView();
    }

    private void ShowDraft()
    {
        // The tree is always a view of the source snapshot. Export and failed compile
        // attempts must never become the baseline used to detect pending text edits.
        draft.Session.ResetDraft();
        var document = draft.Session.Document;
        draft.Nodes = BuildNodes(document.Tags);
        selection = null;
        originalEditorText = null;
        displayedDraftText = null;
        PayloadEditor.Text = string.Empty;
        PayloadEditor.IsReadOnly = true;
        FrameRateEditor.Text = draft.FrameRate;
        MovieSummary = $"{draft.Session.Export.InstancedFullPath}  ·  {document.Signature} v{document.Version}  ·  {document.StageWidth:0.##} × {document.StageHeight:0.##} px";
        MovieDetails = $"{document.FrameCount} frames · {Flatten(draft.Nodes).Count()} tags including sprite contents\nOriginal format, stage bounds, character IDs, and unedited resources are retained.";
        DependencySummary = GetReferences(draft.Session.Export);
        SelectionTitle = "Browse the movie";
        SelectionHelp = "Choose a tag to inspect it. Supported fields and AVM1 scripts can be edited here.";
        Designer.LoadMovie(document, draft.Textures.Resolve, draft.Visual, draft.Edits, draft.Shared.Resolve);
        UpdateFilter();
        NotifyView();
    }

    private void ClearView()
    {
        selection = null;
        originalEditorText = null;
        displayedDraftText = null;
        Designer.ClearMovie();
        VisibleTags = Array.Empty<TagNode>();
        PayloadEditor.Text = string.Empty;
        PayloadEditor.IsReadOnly = true;
        FrameRateEditor.Text = string.Empty;
        MovieSummary = "Select a BioSWF or GFxMovieInfo export to begin.";
        MovieDetails = DependencySummary = string.Empty;
        SelectionTitle = "Browse the movie";
        SelectionHelp = "Choose a resource from the tree.";
    }

    private void NotifyView()
    {
        foreach (string name in new[] { nameof(HasMovie), nameof(HasSelection), nameof(MovieSummary), nameof(MovieDetails),
                     nameof(DependencySummary), nameof(SelectionTitle), nameof(SelectionHelp), nameof(VisibleTags) })
            OnPropertyChanged(name);
    }

    private bool CaptureDraft()
    {
        if (draft == null) return true;
        bool fieldsValid = Designer.CommitPendingFields();
        draft.FrameRate = FrameRateEditor.Text;
        if (selection != null && !PayloadEditor.IsReadOnly && PayloadEditor.Text != displayedDraftText)
        {
            if (PayloadEditor.Text == originalEditorText) draft.Edits.Remove(selection.Path);
            else draft.Edits[selection.Path] = PayloadEditor.Text;
            displayedDraftText = PayloadEditor.Text;
        }
        return fieldsValid;
    }

    private void Tag_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        CaptureDraft();
        selection = e.NewValue as TagNode;
        if (selection == null)
        {
            originalEditorText = null;
            displayedDraftText = null;
            PayloadEditor.IsReadOnly = true;
            PayloadEditor.Text = string.Empty;
            SelectionTitle = "Browse the movie";
            SelectionHelp = "Choose a tag from the resource tree.";
            NotifyView();
            return;
        }
        var tag = selection.Tag;
        SelectionTitle = tag.Name;
        SelectionHelp = $"Tag {tag.Code} · {tag.Category} · {tag.Payload.Length:N0} bytes · uncompressed offset 0x{tag.OriginalOffset:X}";
        try
        {
            originalEditorText = GetEditableText(tag, out bool editable);
            PayloadEditor.IsReadOnly = !editable;
            PayloadEditor.Text = draft.Edits.TryGetValue(selection.Path, out string edit) ? edit : originalEditorText;
            displayedDraftText = PayloadEditor.Text;
            if (tag.CanEditText) SelectionHelp += "\nEdit the text below; compile to validate its encoding and length.";
            else if (tag.Code == 9) SelectionHelp += "\nBackground color: enter six hexadecimal RGB digits (for example 102838).";
            else if (tag.Code is 12 or 59) SelectionHelp += "\nAVM1 assembly. Keep labels and branch targets; .pushstring and .constantpool accept quoted strings. This is bytecode, not ActionScript source.";
            else SelectionHelp += "\nBinary inspection. This resource is preserved unchanged by the native writer.";
        }
        catch (Exception ex)
        {
            originalEditorText = null;
            PayloadEditor.IsReadOnly = true;
            PayloadEditor.Text = HexPreview(tag.Payload.Span);
            SelectionHelp += $"\nBytecode editing unavailable: {ex.Message}";
        }
        NotifyView();
    }

    private static string GetEditableText(ScaleformTag tag, out bool editable)
    {
        editable = true;
        if (tag.CanEditText) return tag.TextValue;
        if (tag.Code == 9 && tag.Payload.Length == 3) return Convert.ToHexString(tag.Payload.Span);
        if (tag.Code is 12 or 59)
        {
            if (tag.Payload.Length > 2 * 1024 * 1024) throw new InvalidDataException("This script exceeds the native text editor’s 2 MiB limit.");
            return ScaleformActionScript.Disassemble(tag.Payload.Span[(tag.Code == 59 ? 2 : 0)..]);
        }
        editable = false;
        return HexPreview(tag.Payload.Span);
    }

    private static string HexPreview(ReadOnlySpan<byte> bytes)
    {
        int limit = Math.Min(bytes.Length, 65536);
        var text = new StringBuilder();
        for (int offset = 0; offset < limit; offset += 16)
        {
            text.Append(offset.ToString("X8")).Append("  ");
            int count = Math.Min(16, limit - offset);
            for (int i = 0; i < count; i++) text.Append(bytes[offset + i].ToString("X2")).Append(' ');
            text.Append(' ', (16 - count) * 3).Append("  ");
            for (int i = 0; i < count; i++) text.Append(bytes[offset + i] is >= 32 and < 127 ? (char)bytes[offset + i] : '.');
            text.AppendLine();
        }
        if (limit < bytes.Length) text.AppendLine("Preview limited to 64 KiB. Export tag bytes for the complete resource.");
        return text.ToString();
    }

    private void StageDraft(MovieDraft target)
    {
        target.Session.ResetDraft();
        if (!double.TryParse(target.FrameRate, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
            throw new InvalidDataException("Enter a frame rate using a decimal point (for example 30 or 29.97).");
        if (rate != target.Session.Document.FrameRate) target.Session.Document.FrameRate = rate;
        var tags = Flatten(BuildNodes(target.Session.Document.Tags)).ToDictionary(n => n.Path, n => n.Tag);
        target.StagedTags = tags;
        foreach ((string path, string text) in target.Edits)
        {
            var tag = tags[path];
            if (tag.CanEditText) tag.SetText(text);
            else if (tag.Code == 9)
            {
                string hex = text.Trim().TrimStart('#');
                if (hex.Length != 6) throw new InvalidDataException("Background color must contain six hexadecimal RGB digits.");
                byte[] rgb = Convert.FromHexString(hex);
                tag.SetBackgroundColor(rgb[0], rgb[1], rgb[2]);
            }
            else if (tag.Code is 12 or 59)
            {
                byte[] actions = ScaleformActionScript.Assemble(text);
                ScaleformActionScript.Validate(actions);
                tag.SetActionBytes(actions);
            }
            else throw new InvalidOperationException($"Editing {tag.Name} is not supported.");
        }
        ScaleformVisualEditor.ApplyDraft(target.Session.Document, target.Visual);
    }

    private void CompileApply_Click(object sender, RoutedEventArgs e)
    {
        if (draft == null) return;
        if (!CaptureDraft()) return;
        try
        {
            StageDraft(draft);
            bool applied = draft.Session.Apply();
            draft.Edits.Clear();
            draft.Visual.ClearEdits();
            draft.FrameRate = draft.Session.Document.FrameRate.ToString(CultureInfo.InvariantCulture);
            draft.OriginalFrameRate = draft.FrameRate;
            ShowDraft();
            SetStatus(applied
                ? "Compiled, reopened, and applied to the package in memory. Save the package, then test the UI in game."
                : "Validation passed. The movie bytes are unchanged.");
        }
        catch (Exception ex) { SetStatus($"Could not apply the draft: {ex.Message}"); }
    }

    private void ExportMovie_Click(object sender, RoutedEventArgs e)
    {
        if (draft == null) return;
        if (!CaptureDraft()) return;
        try
        {
            StageDraft(draft);
            byte[] bytes = draft.Session.Compile();
            string extension = draft.Session.Document.Signature.EndsWith("FX", StringComparison.Ordinal) ? ".gfx" : ".swf";
            var dialog = new SaveFileDialog { Filter = "Scaleform / Flash movie|*.gfx;*.swf", DefaultExt = extension,
                FileName = draft.Session.Export.ObjectName.Instanced + extension };
            if (dialog.ShowDialog(owner) != true) return;
            WriteAtomically(dialog.FileName, bytes);
            SetStatus("Exported the compiled movie. Package data is unchanged by export.");
        }
        catch (Exception ex) { SetStatus($"Could not export the movie: {ex.Message}"); }
    }

    // Build and validate in memory first; replace the chosen destination only after the whole write succeeds.
    private static void WriteAtomically(string destination, byte[] bytes)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ExportTag_Click(object sender, RoutedEventArgs e)
    {
        if (selection == null || draft == null) return;
        if (!CaptureDraft()) return;
        try
        {
            StageDraft(draft);
            draft.Session.Compile();
            var tag = draft.StagedTags[selection.Path];
            var dialog = new SaveFileDialog { Filter = "SWF tag payload|*.bin", FileName = $"{selection.Tag.Name}.bin" };
            if (dialog.ShowDialog(owner) == true)
            {
                WriteAtomically(dialog.FileName, tag.Payload.ToArray());
                SetStatus("Exported the selected tag from the compiled draft.");
            }
        }
        catch (Exception ex) { SetStatus($"Could not export the tag: {ex.Message}"); }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentLoadedExport == null) return;
        if (!CaptureDraft()) return;
        if (draft?.IsDirty == true && MessageBox.Show(owner, "Discard this movie’s unapplied draft and reload from the package?", "Reload movie", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var export = CurrentLoadedExport;
        draft?.Shared.Dispose();
        drafts.Remove(export);
        draft = null;
        LoadExport(export);
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        UpdateFilter();
    }

    private void EditingTabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || e.Source != EditingTabs || draft == null) return;
        CaptureDraft();
        if (EditingTabs.SelectedIndex == 0)
        {
            draft.Session.ResetDraft();
            Designer.LoadMovie(draft.Session.Document, draft.Textures.Resolve, draft.Visual, draft.Edits, draft.Shared.Resolve);
        }
    }

    private void UpdateFilter()
    {
        CaptureDraft();
        string filter = FilterBox.Text.Trim();
        VisibleTags = draft == null ? Array.Empty<TagNode>() : FilterNodes(draft.Nodes, filter);
        OnPropertyChanged(nameof(VisibleTags));
    }

    private static List<TagNode> FilterNodes(IReadOnlyList<TagNode> nodes, string filter) => nodes
        .Select(node => (node, children: FilterNodes(node.Children, filter)))
        .Where(item => filter.Length == 0 || item.children.Count > 0 ||
            $"{item.node.Name} {item.node.Tag.Code} {item.node.Tag.Category} {item.node.Tag.TextValue}".Contains(filter, StringComparison.OrdinalIgnoreCase))
        .Select(item => new TagNode(item.node.Tag, item.node.Path, item.children)).ToList();

    private static List<TagNode> BuildNodes(IReadOnlyList<ScaleformTag> tags, string prefix = "") => tags
        .Select((tag, index) => new TagNode(tag, $"{prefix}{index}", BuildNodes(tag.Children, $"{prefix}{index}/"))).ToList();

    private static IEnumerable<TagNode> Flatten(IReadOnlyList<TagNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private static string GetReferences(ExportEntry export)
    {
        var references = export.GetProperty<ArrayProperty<ObjectProperty>>("References");
        if (references == null || references.Count == 0) return "No package References array. Movie imports and external resource tags remain in the tag tree.";
        return string.Join(Environment.NewLine, references.Select(reference =>
        {
            var entry = export.FileRef.GetEntry(reference.Value);
            return entry == null ? $"#{reference.Value}: unresolved reference" : $"#{reference.Value}  {entry.InstancedFullPath} ({entry.ClassName})";
        }));
    }

    private void SetStatus(string text)
    {
        Status = text;
        OnPropertyChanged(nameof(Status));
    }

    private void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (owner == window) return;
        owner = window;
    }

    public override bool CanClose()
    {
        if (!CaptureDraft()) return false;
        return !drafts.Values.Any(d => d.IsDirty) || MessageBox.Show(owner,
            "Discard unapplied Scaleform drafts? Choose No to return and compile them before continuing.",
            "Scaleform drafts", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }

    public void ClearPackageDrafts()
    {
        UnloadExport();
        foreach (var cached in drafts.Values) cached.Shared.Dispose();
        drafts.Clear();
    }

    private void PopOut_Click(object sender, RoutedEventArgs e) => PopOut();
    public override void PopOut()
    {
        if (CurrentLoadedExport == null) return;
        new ExportLoaderHostedWindow(new NativeScaleformExportLoader(), CurrentLoadedExport)
        {
            Title = $"Scaleform Editor - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath} - {CurrentLoadedExport.FileRef.FilePath}"
        }.Show();
    }

    public override void Dispose()
    {
        owner = null;
        foreach (var cached in drafts.Values) cached.Shared.Dispose();
        drafts.Clear();
        draft = null;
        Designer.ClearMovie();
        ExternalEditor.Dispose();
    }

    public sealed class TagNode(ScaleformTag tag, string path, IReadOnlyList<TagNode> children)
    {
        public ScaleformTag Tag { get; } = tag;
        public string Path { get; } = path;
        public string Name
        {
            get
            {
                string text = Tag.CanEditText ? Tag.TextValue.Replace('\r', ' ').Replace('\n', ' ') : string.Empty;
                if (text.Length > 42) text = text[..42] + "…";
                string identity = Tag.Code is 2 or 6 or 7 or 10 or 11 or 14 or 20 or 21 or 22 or 32 or 33 or 34 or 35 or 36 or 37 or 39 or 46 or 48 or 60 or 75 or 83 or 84 or 87 or 90 or 91 or 1005
                    && Tag.Payload.Length >= 2 ? $" #{System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(Tag.Payload.Span)}" : string.Empty;
                return Tag.Name + identity + (text.Length > 0 ? $"  “{text}”" : string.Empty);
            }
        }
        public string Detail => $"{Tag.Code} · {Tag.Payload.Length:N0} B";
        public IReadOnlyList<TagNode> Children { get; } = children;
    }

    private sealed class MovieDraft(ScaleformExportSession session)
    {
        public ScaleformExportSession Session { get; } = session;
        public List<TagNode> Nodes { get; set; } = new();
        public Dictionary<string, string> Edits { get; } = new();
        public IReadOnlyDictionary<string, ScaleformTag> StagedTags { get; set; }
        public ScaleformVisualDraft Visual { get; } = new();
        public ScaleformTextureResolver Textures { get; } = new(session.Export);
        private ScaleformSharedAssetResolver shared;
        public ScaleformSharedAssetResolver Shared => shared ??= new ScaleformSharedAssetResolver(session.Export, Textures.Resolve);
        public string OriginalFrameRate { get; set; } = session.Document.FrameRate.ToString(CultureInfo.InvariantCulture);
        public string FrameRate { get; set; } = session.Document.FrameRate.ToString(CultureInfo.InvariantCulture);
        public bool IsDirty => Visual.IsModified || Edits.Count > 0 || FrameRate != OriginalFrameRate;
    }
}
