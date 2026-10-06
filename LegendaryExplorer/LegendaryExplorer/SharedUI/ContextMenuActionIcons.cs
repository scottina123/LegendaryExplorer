using System;
using System.Globalization;
using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using FontAwesome5;

namespace LegendaryExplorer.SharedUI;

/// <summary>Provides consistent action images and glyphs for static and generated menu options.</summary>
internal static class ContextMenuActionIcons
{
    private static readonly (string Name, string ResourceKey)[] ToolIcons =
    {
        ("package editor", "PackageEditorMenuIcon"),
        ("dialogue editor", "DialogueEditorMenuIcon"),
        ("sequence editor", "SequenceEditorMenuIcon"),
        ("interp editor", "InterpEditorMenuIcon"),
        ("facefx editor", "FaceFXEditorMenuIcon"),
        ("meshplorer", "MeshplorerMenuIcon"),
        ("mesh explorer", "MeshplorerMenuIcon"),
        ("plot editor", "PlotEditorMenuIcon"),
        ("plot database", "PlotDatabaseMenuIcon"),
        ("conditionals editor", "ConditionalsEditorMenuIcon"),
        ("soundplorer", "SoundplorerMenuIcon"),
        ("sound explorer", "SoundplorerMenuIcon"),
        ("pathfinding editor", "PathfindingEditorMenuIcon"),
        ("wwise editor", "WwiseEditorMenuIcon"),
        ("wwiseeditor", "WwiseEditorMenuIcon"),
        ("level editor", "LevelEditorMenuIcon"),
        ("sfx galaxy editor", "SFXGalaxyEditorMenuIcon"),
        ("sfxgalaxy editor", "SFXGalaxyEditorMenuIcon"),
        ("gesture animation importer", "AnimationImporterMenuIcon"),
        ("animation importer", "AnimationImporterMenuIcon"),
        ("animation viewer", "AnimViewerMenuIcon"),
        ("anim viewer", "AnimViewerMenuIcon"),
        ("coalesced editor", "CoalescedEditorMenuIcon"),
        ("tlk editor", "TLKEditorMenuIcon"),
        ("hex converter", "HexConverterMenuIcon"),
        ("asset viewer", "AssetViewerMenuIcon")
    };

    /// <summary>Uses the same application images as explicit context-menu actions.</summary>
    internal static string GetImageResourceKey(MenuItem item, string parentResourceKey = null)
    {
        // Standard commands keep their action glyph even when their header names a tool.
        if (GetStandardCommandIcon(item.Command).HasValue)
            return null;

        string text = GetHeaderText(item);
        if (StartsWithAny(text, "import", "export", "replace"))
        {
            if (text.Contains(" from udk", StringComparison.Ordinal) || text.Contains(" to udk", StringComparison.Ordinal))
                return "UDKMenuIcon";
            if (text.Contains(" with umodel", StringComparison.Ordinal))
                return "UModelMenuIcon";
            if (text.Contains(" from excel", StringComparison.Ordinal) || text.Contains(" to excel", StringComparison.Ordinal))
                return "ExcelMenuIcon";
        }

        if (StartsWithAny(text, "open file location", "open in windows explorer", "open local database folder"))
            return "WindowsExplorerMenuIcon";

        int destinationStart = text.LastIndexOf(" in ", StringComparison.Ordinal);
        string destination = destinationStart >= 0 ? text[(destinationStart + 4)..] : string.Empty;
        foreach (var (name, resourceKey) in ToolIcons)
        {
            if (text.TrimEnd('.', '…') == name
                || (StartsWithAny(text, "open", "base")
                    && (StartsWithAny(text, "open " + name)
                        || StartsWithAny(destination, name))))
                return resourceKey;
        }

        // Generated file/object names inherit the parent's destination image just as
        // other targets inherit its action glyph. Recognized child actions keep their own icon.
        return parentResourceKey is not null && !GetActionIcon(item).HasValue ? parentResourceKey : null;
    }

    internal static EFontAwesomeIcon GetIcon(MenuItem item, EFontAwesomeIcon? parentIcon = null) =>
        GetActionIcon(item) ?? parentIcon
            ?? (item.IsCheckable ? EFontAwesomeIcon.Solid_CheckSquare
                : item.HasItems ? EFontAwesomeIcon.Solid_Folder : EFontAwesomeIcon.Solid_Cog);

    private static EFontAwesomeIcon? GetActionIcon(MenuItem item)
    {
        EFontAwesomeIcon? commandIcon = GetStandardCommandIcon(item.Command);
        if (commandIcon.HasValue)
        {
            return commandIcon.Value;
        }

        string text = GetHeaderText(item);

        // Toolbar dropdowns share these actions across editors, including their nested submenus.
        if (text.TrimEnd('.', '…') == "open") return EFontAwesomeIcon.Solid_FolderOpen;
        if (StartsWithAny(text, "recent", "last export")) return EFontAwesomeIcon.Solid_History;
        if (StartsWithAny(text, "close", "exit")) return EFontAwesomeIcon.Solid_Times;
        if (StartsWithAny(text, "compare", "structural compare")) return EFontAwesomeIcon.Solid_NotEqual;
        if (StartsWithAny(text, "pop out")) return EFontAwesomeIcon.Solid_ExternalLinkAlt;
        if (StartsWithAny(text, "package file header info", "get shader info", "metadata")) return EFontAwesomeIcon.Solid_InfoCircle;
        if (StartsWithAny(text, "bulk export")) return EFontAwesomeIcon.Solid_FileExport;
        if (StartsWithAny(text, "bulk import", "asset importer", "resolve imports")) return EFontAwesomeIcon.Solid_FileImport;
        if (StartsWithAny(text, "bulk delete", "bulk clear")) return EFontAwesomeIcon.Solid_TrashAlt;
        if (StartsWithAny(text, "bulk generate", "auto generate", "build")) return EFontAwesomeIcon.Solid_Magic;
        if (StartsWithAny(text, "bulk change", "bulk property editing", "bulk operations", "options", "properties", "advanced mode"))
            return EFontAwesomeIcon.Solid_SlidersH;
        if (StartsWithAny(text, "debug", "debugging", "check for unrealscript")) return EFontAwesomeIcon.Solid_Bug;
        if (StartsWithAny(text, "experiments", "enable experiments")) return EFontAwesomeIcon.Solid_Flask;
        if (StartsWithAny(text, "compile", "lecl data editor", "binary interpreter", "binary data", "gfx/swf", "hex converter", "kismet logger"))
            return EFontAwesomeIcon.Solid_Code;
        if (StartsWithAny(text, "check")) return EFontAwesomeIcon.Solid_CheckCircle;
        if (StartsWithAny(text, "scan")) return EFontAwesomeIcon.Solid_Search;
        if (StartsWithAny(text, "calculate")) return EFontAwesomeIcon.Solid_Calculator;
        if (StartsWithAny(text, "compact")) return EFontAwesomeIcon.Solid_CompressArrowsAlt;
        if (StartsWithAny(text, "install")) return EFontAwesomeIcon.Solid_Download;
        if (StartsWithAny(text, "associate", "network")) return EFontAwesomeIcon.Solid_Link;
        if (StartsWithAny(text, "migrate", "reverse endianness")) return EFontAwesomeIcon.Solid_ExchangeAlt;
        if (StartsWithAny(text, "commit")) return EFontAwesomeIcon.Solid_Save;
        if (StartsWithAny(text, "apply property edits")) return EFontAwesomeIcon.Solid_PencilAlt;
        if (StartsWithAny(text, "capture viewport")) return EFontAwesomeIcon.Solid_Camera;
        if (StartsWithAny(text, "back")) return EFontAwesomeIcon.Solid_ArrowLeft;
        if (StartsWithAny(text, "re center", "anchor", "follow camera")) return EFontAwesomeIcon.Solid_Crosshairs;
        if (StartsWithAny(text, "reload", "force reload", "re run")) return EFontAwesomeIcon.Solid_Sync;
        if (StartsWithAny(text, "re save", "auto save", "manual save")) return EFontAwesomeIcon.Solid_Save;
        if (StartsWithAny(text, "load", "open package", "open file", "open sfar", "open local database folder",
                "open left package", "open right package", "open vanilla", "open highest mounted", "open version", "open other generation"))
            return EFontAwesomeIcon.Solid_FolderOpen;
        if (StartsWithAny(text, "file list actions", "vanilla plot files", "level presets", "levels", "mod databases", "database"))
            return EFontAwesomeIcon.Solid_Folder;
        if (StartsWithAny(text, "layout", "default auto layout", "auto index")) return EFontAwesomeIcon.Solid_ProjectDiagram;
        if (StartsWithAny(text, "colorize", "blend mode")) return EFontAwesomeIcon.Solid_Palette;
        if (StartsWithAny(text, "toggle", "auto play", "auto parse", "automatically preview", "parse unknown", "limit arrayproperties", "touch comfy"))
            return EFontAwesomeIcon.Solid_CheckSquare;
        if (StartsWithAny(text, "make all dialogue unskippable")) return EFontAwesomeIcon.Solid_Lock;
        if (StartsWithAny(text, "make all dialogue skippable")) return EFontAwesomeIcon.Solid_LockOpen;
        if (StartsWithAny(text, "make all")) return EFontAwesomeIcon.Solid_ExchangeAlt;
        if (StartsWithAny(text, "convenience tools", "tools", "toolbox", "operations", "exkywor", "kinkojiro")) return EFontAwesomeIcon.Solid_Cogs;

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
        if (StartsWithAny(text, "male facefx", "female facefx", "auto facefx", "emotions", "facefx editor")) return EFontAwesomeIcon.Solid_TheaterMasks;

        if (StartsWithAny(text, "skeletal mesh")) return EFontAwesomeIcon.Solid_Skull;
        if (StartsWithAny(text, "static mesh")) return EFontAwesomeIcon.Solid_Archway;
        if (StartsWithAny(text, "meshplorer", "art", "blockingvolume", "dynamic volumes", "other volumes", "cylinders")) return EFontAwesomeIcon.Solid_Cube;
        if (StartsWithAny(text, "texture viewer")) return EFontAwesomeIcon.Solid_Image;
        if (StartsWithAny(text, "soundplorer", "isact", "wwise")) return EFontAwesomeIcon.Solid_VolumeUp;
        if (StartsWithAny(text, "dialogue editor", "tlk", "spoken line")) return EFontAwesomeIcon.Solid_Comment;
        if (StartsWithAny(text, "sequence editor", "sequence references", "pathfinding", "splines", "director group"))
            return EFontAwesomeIcon.Solid_ProjectDiagram;
        if (StartsWithAny(text, "package editor", "level editor", "designer", "actor group", "design", "cover")) return EFontAwesomeIcon.Solid_Cubes;
        if (StartsWithAny(text, "anomaly", "asteroid belt", "cluster", "fuel depot", "mass relay", "planet", "reaper", "scannable planet", "system", "war asset"))
            return EFontAwesomeIcon.Solid_Globe;

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

        return null;
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
