using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorerCore.Scaleform;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

public partial class ScaleformVisualEditor
{
    private readonly Dictionary<ScaleformTag, List<TextFontChoice>> textFontChoices = new();
    private ScaleformTextFormat textFormatSnapshot;
    private string fontSizeSnapshot, letterSpacingSnapshot;
    private TextFontChoice fontChoiceSnapshot;
    private bool textFormatCommitSucceeded = true;

    private void RebuildTextFonts() => textFontChoices.Clear();

    private void UpdateTextFormatProperties()
    {
        var definition = SelectedElement?.Item.Definition;
        bool supported = definition?.CanEditTextFormat == true;
        TextFormatPanel.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        textFormatSnapshot = supported ? definition.GetTextFormat() : null;
        if (!supported) return;
        var format = textFormatSnapshot;
        TextColour.SelectedColor = Color.FromArgb((byte)format.Color, (byte)(format.Color >> 24), (byte)(format.Color >> 16), (byte)(format.Color >> 8));
        if (!textFontChoices.TryGetValue(definition, out var usableFonts))
        {
            usableFonts = working.GetTextFonts(definition).Select(f => new TextFontChoice(f.Id, f.Name)).ToList();
            textFontChoices[definition] = usableFonts;
        }
        var choices = usableFonts.ToList();
        var current = !string.IsNullOrEmpty(format.FontName)
            ? choices.FirstOrDefault(f => f.Name.Equals(format.FontName, StringComparison.OrdinalIgnoreCase))
            : choices.FirstOrDefault(f => f.Id == format.FontId);
        if (current == null)
        {
            current = new TextFontChoice(format.FontId, !string.IsNullOrEmpty(format.FontName) ? format.FontName :
                format.FontId.HasValue ? "Font " + format.FontId : "Default font");
            choices.Insert(0, current);
        }
        TextFont.ItemsSource = choices;
        TextFont.SelectedItem = current;
        fontChoiceSnapshot = current;
        TextFontSize.Text = fontSizeSnapshot = Format(format.FontSize);
        TextLetterSpacing.Text = letterSpacingSnapshot = Format(format.LetterSpacing);
        TextAlignmentBox.SelectedIndex = (int)format.Alignment;
    }

    /// <summary>Formats a complete editable field, preserving the isolated movie draft and undo history.</summary>
    public void ApplySelectedTextFormat(ScaleformTextFormat format)
    {
        var definition = SelectedElement?.Item.Definition;
        if (definition?.CanEditTextFormat != true || !identities.TryGetValue(definition, out string path)) return;
        if (format.FontId != definition.GetTextFormat().FontId && format.FontId.HasValue && !working.GetTextFonts(definition).Any(font => font.Id == format.FontId))
            throw new ArgumentException("Choose a movie font declared before this text field.", nameof(format));
        byte[] before = definition.Payload.ToArray();
        bool hadFormat = draft.TextFormats.TryGetValue(path, out var previousFormat);
        bool hadText = textEdits.TryGetValue(path, out var previousText);
        bool wasHtml = draft.HtmlTextFormats.Contains(path);
        definition.SetTextFormat(format);
        if (before.AsSpan().SequenceEqual(definition.Payload.Span)) return;
        // Store the canonical HTML along with the format. On rebuild, establish the
        // format/HTML flag first, then restore this string, including later text edits.
        var applied = definition.GetTextFormat();
        string appliedText = definition.TextValue;
        draft.TextFormats[path] = applied;
        textEdits[path] = appliedText;
        bool appliedHtml = IsHtmlText(definition);
        if (appliedHtml) draft.HtmlTextFormats.Add(path); else draft.HtmlTextFormats.Remove(path);
        draft.Record(() =>
        {
            if (hadFormat) draft.TextFormats[path] = previousFormat; else draft.TextFormats.Remove(path);
            if (hadText) textEdits[path] = previousText; else textEdits.Remove(path);
            if (wasHtml) draft.HtmlTextFormats.Add(path); else draft.HtmlTextFormats.Remove(path);
        }, () =>
        {
            draft.TextFormats[path] = applied; textEdits[path] = appliedText;
            if (appliedHtml) draft.HtmlTextFormats.Add(path); else draft.HtmlTextFormats.Remove(path);
        });
        renderer.InvalidateCharacter(SelectedElement.CharacterId);
        RefreshView(SelectedElement.Identity, SelectedElement.OwnerSpriteId, SelectedElement.Depth);
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TextFormat_Changed(object sender, RoutedEventArgs e)
    {
        if (!updating && TextAlignmentBox != null) TextFormat_Commit(sender, e);
    }

    private void TextFormat_Commit(object sender, RoutedEventArgs e)
    {
        textFormatCommitSucceeded = true;
        if (updating || textFormatSnapshot == null || SelectedElement?.Item.Definition?.CanEditTextFormat != true) return;
        try
        {
            var format = PendingTextFormat();
            if (format != textFormatSnapshot) ApplySelectedTextFormat(format);
        }
        catch (Exception ex)
        {
            textFormatCommitSucceeded = false;
            StatusChanged?.Invoke("Could not format this text: " + ex.Message);
        }
    }

    private ScaleformTextFormat PendingTextFormat()
    {
        if (textFormatSnapshot == null) return null;
        var format = textFormatSnapshot;
        if (TextFontSize.Text != fontSizeSnapshot) format = format with { FontSize = Number(TextFontSize.Text) };
        if (TextLetterSpacing.Text != letterSpacingSnapshot) format = format with { LetterSpacing = Number(TextLetterSpacing.Text) };
        if (TextColour.SelectedColor is Color colour)
            format = format with { Color = (uint)colour.R << 24 | (uint)colour.G << 16 | (uint)colour.B << 8 | colour.A };
        if (TextFont.SelectedItem is TextFontChoice choice && choice != fontChoiceSnapshot)
            format = format with { FontId = choice.Id, FontName = choice.Name == "Font " + choice.Id ? string.Empty : choice.Name };
        if (TextAlignmentBox.SelectedIndex >= 0) format = format with { Alignment = (ScaleformTextAlignment)TextAlignmentBox.SelectedIndex };
        return format;
    }

    private void TextFormat_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TextFormat_Commit(sender, e);
        e.Handled = true;
        if (textFormatCommitSucceeded) CanvasViewport.Focus();
    }

    public sealed record TextFontChoice(ushort? Id, string Name);
}
