using System;
using System.Globalization;
using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using FontAwesome5;

namespace LegendaryExplorer.SharedUI;

/// <summary>Provides consistent action glyphs for static and generated context-menu options.</summary>
internal static class ContextMenuActionIcons
{
    internal static EFontAwesomeIcon GetIcon(MenuItem item, EFontAwesomeIcon? parentIcon = null)
    {
        EFontAwesomeIcon? commandIcon = GetStandardCommandIcon(item.Command);
        if (commandIcon.HasValue)
        {
            return commandIcon.Value;
        }

        string text = GetHeaderText(item);

        // These actions use the same glyphs as Package Editor's inline tree buttons.
        if (StartsWithAny(text, "go to archetype", "goto archetype")) return EFontAwesomeIcon.Solid_LevelUpAlt;
        if (StartsWithAny(text, "find", "search", "attempt to find")) return EFontAwesomeIcon.Solid_Search;
        if (StartsWithAny(text, "rename", "change name", "edit name")) return EFontAwesomeIcon.Solid_PencilAlt;
        if (StartsWithAny(text, "change links", "change link")) return EFontAwesomeIcon.Solid_Link;
        if (StartsWithAny(text, "clone", "multi clone", "multi cloning", "above clone", "below clone")) return EFontAwesomeIcon.Solid_Clone;
        if (StartsWithAny(text, "trash", "delete")) return EFontAwesomeIcon.Solid_TrashAlt;

        if (StartsWithAny(text, "copy")) return EFontAwesomeIcon.Solid_Copy;
        if (StartsWithAny(text, "cut")) return EFontAwesomeIcon.Solid_Cut;
        if (StartsWithAny(text, "paste")) return EFontAwesomeIcon.Solid_Paste;
        if (StartsWithAny(text, "undo", "restore", "reset")) return EFontAwesomeIcon.Solid_Undo;
        if (StartsWithAny(text, "redo")) return EFontAwesomeIcon.Solid_Redo;
        if (StartsWithAny(text, "save")) return EFontAwesomeIcon.Solid_Save;
        if (StartsWithAny(text, "print")) return EFontAwesomeIcon.Solid_Print;

        if (StartsWithAny(text, "break tangent", "break tangents", "flatten tangent", "flatten tangents", "set as reference curve")) return EFontAwesomeIcon.Solid_BezierCurve;
        if (StartsWithAny(text, "break", "disconnect", "trim unused variable links")) return EFontAwesomeIcon.Solid_Unlink;
        if (StartsWithAny(text, "replace all references", "repoint", "relay connections", "connect", "start drag connection",
                "create connection", "edit link", "edit links", "add link", "input links", "output links", "variable links", "event links"))
            return EFontAwesomeIcon.Solid_Link;

        if (StartsWithAny(text, "remove", "clear", "strip", "drop mip", "nop out")) return EFontAwesomeIcon.Solid_TrashAlt;
        if (StartsWithAny(text, "export", "extract")) return EFontAwesomeIcon.Solid_FileExport;
        if (StartsWithAny(text, "import", "from wav", "from wwise", "insert saved")) return EFontAwesomeIcon.Solid_FileImport;
        if (StartsWithAny(text, "replace", "convert", "localize", "re assign")) return EFontAwesomeIcon.Solid_ExchangeAlt;

        if (StartsWithAny(text, "open file location", "open in windows explorer")) return EFontAwesomeIcon.Solid_FolderOpen;
        if (StartsWithAny(text, "open", "navigate", "go to", "goto")) return EFontAwesomeIcon.Solid_ArrowRight;
        if (StartsWithAny(text, "view reference graph")) return EFontAwesomeIcon.Solid_ProjectDiagram;
        if (StartsWithAny(text, "view", "preview")) return EFontAwesomeIcon.Solid_Eye;
        if (StartsWithAny(text, "focus", "snap camera", "snap actor")) return EFontAwesomeIcon.Solid_Crosshairs;

        if (StartsWithAny(text, "add", "create", "insert", "new", "bulk create"))
        {
            if (text.Contains("favorite", StringComparison.Ordinal)) return EFontAwesomeIcon.Solid_Star;
            return EFontAwesomeIcon.Solid_Plus;
        }

        if (StartsWithAny(text, "expand")) return EFontAwesomeIcon.Solid_ExpandArrowsAlt;
        if (StartsWithAny(text, "collapse")) return EFontAwesomeIcon.Solid_CompressArrowsAlt;
        if (StartsWithAny(text, "array sorting", "sort"))
            return text.Contains("descending", StringComparison.Ordinal)
                ? EFontAwesomeIcon.Solid_SortAmountUp
                : EFontAwesomeIcon.Solid_SortAmountDown;
        if (StartsWithAny(text, "filter", "show only")) return EFontAwesomeIcon.Solid_Filter;
        if (StartsWithAny(text, "select")) return EFontAwesomeIcon.Solid_CheckSquare;
        if (StartsWithAny(text, "hide", "ignore")) return EFontAwesomeIcon.Solid_EyeSlash;
        if (StartsWithAny(text, "show")) return EFontAwesomeIcon.Solid_Eye;
        if (StartsWithAny(text, "pin", "unpin", "pinned")) return EFontAwesomeIcon.Solid_Thumbtack;
        if (StartsWithAny(text, "favorites")) return EFontAwesomeIcon.Solid_Star;
        if (StartsWithAny(text, "play")) return EFontAwesomeIcon.Solid_Play;
        if (StartsWithAny(text, "audio", "male audio", "female audio", "non vocal")) return EFontAwesomeIcon.Solid_VolumeUp;
        if (StartsWithAny(text, "camera", "multicam", "single camera")) return EFontAwesomeIcon.Solid_Camera;
        if (StartsWithAny(text, "cinematic", "ambient line")) return EFontAwesomeIcon.Solid_Video;
        if (StartsWithAny(text, "male facefx", "female facefx", "auto facefx", "emotions")) return EFontAwesomeIcon.Solid_TheaterMasks;

        if (StartsWithAny(text, "top of list")) return EFontAwesomeIcon.Solid_ArrowUp;
        if (StartsWithAny(text, "bottom of list")) return EFontAwesomeIcon.Solid_ArrowDown;
        if (StartsWithAny(text, "move", "shift", "translate", "offset", "rot ", "roll", "pitch", "yaw")) return EFontAwesomeIcon.Solid_ArrowsAlt;
        if (StartsWithAny(text, "set time", "edit time", "adjust time")) return EFontAwesomeIcon.Solid_Clock;
        if (StartsWithAny(text, "adjust", "bulk edit", "manage", "set all paths")) return EFontAwesomeIcon.Solid_SlidersH;
        if (StartsWithAny(text, "edit", "change", "set", "reindex")) return EFontAwesomeIcon.Solid_PencilAlt;
        if (StartsWithAny(text, "force refresh", "refresh", "rebuild", "update", "sync", "match")) return EFontAwesomeIcon.Solid_Sync;
        if (StartsWithAny(text, "generate", "test", "validate")) return EFontAwesomeIcon.Solid_Magic;
        if (StartsWithAny(text, "skip", "use ")) return EFontAwesomeIcon.Solid_StepForward;
        if (StartsWithAny(text, "unskippable")) return EFontAwesomeIcon.Solid_Lock;
        if (StartsWithAny(text, "skippable")) return EFontAwesomeIcon.Solid_LockOpen;
        if (StartsWithAny(text, "meshes", "textures")) return EFontAwesomeIcon.Solid_Cube;
        if (StartsWithAny(text, "string", "conditional", "transition")) return EFontAwesomeIcon.Solid_Code;
        if (StartsWithAny(text, "utilities", "package utilities", "toolset devs")) return EFontAwesomeIcon.Solid_Cogs;
        if (StartsWithAny(text, "core editors")) return EFontAwesomeIcon.Solid_Folder;

        // Informational rows should remain distinct from generated action targets.
        if (!item.HasItems && item.Command is null
            && (!item.IsEnabled || long.TryParse(text, out _))) return EFontAwesomeIcon.Solid_InfoCircle;

        // Generated file names and object names describe the target, while their parent names the action.
        if (parentIcon.HasValue) return parentIcon.Value;
        if (item.IsCheckable) return EFontAwesomeIcon.Solid_CheckSquare;
        return item.HasItems ? EFontAwesomeIcon.Solid_Folder : EFontAwesomeIcon.Solid_Cog;
    }

    private static EFontAwesomeIcon? GetStandardCommandIcon(ICommand command)
    {
        if (command == ApplicationCommands.Copy) return EFontAwesomeIcon.Solid_Copy;
        if (command == ApplicationCommands.Cut) return EFontAwesomeIcon.Solid_Cut;
        if (command == ApplicationCommands.Paste) return EFontAwesomeIcon.Solid_Paste;
        if (command == ApplicationCommands.Undo) return EFontAwesomeIcon.Solid_Undo;
        if (command == ApplicationCommands.Redo) return EFontAwesomeIcon.Solid_Redo;
        if (command == ApplicationCommands.SelectAll) return EFontAwesomeIcon.Solid_CheckSquare;
        if (command == ApplicationCommands.Delete) return EFontAwesomeIcon.Solid_TrashAlt;
        if (command == ApplicationCommands.Find) return EFontAwesomeIcon.Solid_Search;
        if (command == ApplicationCommands.Replace) return EFontAwesomeIcon.Solid_ExchangeAlt;
        if (command == ApplicationCommands.Open) return EFontAwesomeIcon.Solid_FolderOpen;
        if (command == ApplicationCommands.New) return EFontAwesomeIcon.Solid_Plus;
        if (command == ApplicationCommands.Save || command == ApplicationCommands.SaveAs) return EFontAwesomeIcon.Solid_Save;
        if (command == ApplicationCommands.Print || command == ApplicationCommands.PrintPreview) return EFontAwesomeIcon.Solid_Print;
        if (command == ApplicationCommands.Properties) return EFontAwesomeIcon.Solid_SlidersH;
        if (command == ApplicationCommands.Help) return EFontAwesomeIcon.Solid_QuestionCircle;
        return null;
    }

    private static string GetHeaderText(MenuItem item)
    {
        string text = GetText(item.Header);
        if (string.IsNullOrWhiteSpace(text) && item.Command is RoutedUICommand command)
        {
            text = command.Text;
        }

        if (!string.IsNullOrWhiteSpace(item.HeaderStringFormat))
        {
            try
            {
                text = string.Format(CultureInfo.CurrentCulture, item.HeaderStringFormat, text);
            }
            catch (FormatException)
            {
                // A malformed display format should not prevent the menu from opening.
            }
        }

        var normalized = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '_')
            {
                if (i + 1 >= text.Length || text[i + 1] != '_') continue;
                i++;
            }

            normalized.Append(c is '-' or '–' or '—' ? ' ' : c);
        }

        return string.Join(" ", normalized.ToString().ToLowerInvariant().Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string GetText(object value) => value switch
    {
        null => string.Empty,
        string text => text,
        AccessText accessText => accessText.Text,
        TextBlock textBlock => textBlock.Text,
        ContentControl contentControl when !ReferenceEquals(contentControl.Content, contentControl) => GetText(contentControl.Content),
        _ => value.ToString() ?? string.Empty
    };

    private static bool StartsWithAny(string text, params string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            string action = prefix.TrimEnd();
            if (text.StartsWith(action, StringComparison.Ordinal)
                && (text.Length == action.Length || !char.IsLetterOrDigit(text[action.Length]))
                && !(text.Length > action.Length + 1 && text[action.Length] == '.' && char.IsLetterOrDigit(text[action.Length + 1]))) return true;
        }

        return false;
    }
}
