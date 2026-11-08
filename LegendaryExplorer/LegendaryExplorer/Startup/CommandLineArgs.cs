using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Threading.Tasks;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.CoalescedEditor;
using LegendaryExplorer.Tools.ConditionalsEditor;
using LegendaryExplorer.Tools.FaceFXEditor;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.Tools.MountEditor;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.PathfindingEditor;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorer.Tools.Soundplorer;
using LegendaryExplorer.Tools.SFXGalaxyEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Helpers;

namespace LegendaryExplorer.Startup
{
    public static class CommandLineArgs
    {
        private const string PackageEditorTool = "Package Editor";
        private const string DialogueEditorTool = "Dialogue Editor";
        private const string SequenceEditorTool = "Sequence Editor";
        private const string LevelEditorTool = "Level Editor";
        private const string MeshplorerTool = "Meshplorer";

        // Documentation: 
        // https://github.com/dotnet/command-line-api
        public static RootCommand CreateCLIHandler()
        {
            var root = new RootCommand()
            {
                new Option<FileInfo>(["--open", "-o"], "A file you would like to open in the toolset"),
                new Option<string>(["--tool", "-t"], "The tool to open"),
                new Option<int>("--UIndex", "UIndex in the file to open")
            };

            root.Handler = CommandHandler.Create<FileInfo, string, int>(HandleCLIArgs);
            return root;
        }

        // Method parameters MUST match option names in the RootCommand
        private static void HandleCLIArgs(FileInfo open, string tool, int UIndex)
        {
            var file = open;

            // Handle file opening
            if (file?.Exists ?? false)
            {
                switch(tool)
                {
                    case "PackageEditor":
                        OpenTool<PackageEditorWindow>((p) => p.LoadFile(file.FullName, UIndex));
                        break;
                    case "SequenceEditor":
                        OpenTool<SequenceEditorWPF>((s) => s.LoadFileAndGoTo(file.FullName, UIndex));
                        break;
                    case "DialogueEditor":
                        OpenTool<DialogueEditorWindow>((s) => s.LoadFile(file.FullName, UIndex));
                        break;
                    case "LevelEditor":
                        OpenTool<LevelEditor>(async l => await l.LoadFileAsync(file.FullName));
                        break;
                    case "SFXGalaxyEditor":
                        OpenTool<SFXGalaxyEditorWindow>(editor => editor.LoadFile(file.FullName));
                        break;
                    case "SoundExplorer":
                    case "Soundplorer":
                        OpenTool<SoundplorerWPF>(s => s.LoadFile(file.FullName));
                        break;
                    default:
                        OpenFile(file.FullName, UIndex);
                        break;

                }
            }

            // Handle tool opening without a file
            else
            {
                // Tool must have a parameterless constructor for this to work
                switch (tool)
                {
                    case "PackageEditor":
                        OpenTool<PackageEditorWindow>();
                        break;
                    case "SequenceEditor":
                        OpenTool<SequenceEditorWPF>();
                        break;
                    case "SoundExplorer":
                    case "Soundplorer":
                        OpenTool<SoundplorerWPF>();
                        break;
                    case "DialogueEditor":
                        OpenTool<DialogueEditorWindow>();
                        break;
                    case "LevelEditor":
                        OpenTool<LevelEditor>();
                        break;
                    case "SFXGalaxyEditor":
                        OpenTool<SFXGalaxyEditorWindow>();
                        break;
                    case "PathfindingEditor":
                        OpenTool<PathfindingEditorWindow>();
                        break;
                    case "MeshExplorer":
                    case "Meshplorer":
                        OpenTool<MeshplorerWindow>();
                        break;
                    case "FaceFXEditor":
                        OpenTool<FaceFXEditorWindow>();
                        break;
                    case "AssetDB":
                        (new AssetDatabaseWindow()).Show();
                        break;
                }
            }
        }

        /// <summary>
        /// Opens a file in the tool appropriate for its extension. PCC files prompt the user to choose a tool.
        /// </summary>
        /// <returns>True if the file type is supported and was handed to a tool (or the tool chooser)</returns>
        public static bool OpenFile(string fileName, int uIndex = 0)
        {
            string extension = Path.GetExtension(fileName);
            switch (extension.ToLower())
            {
                case ".pcc":
                case ".sfm":
                case ".upk":
                case ".u":
                case ".udk":
                    OpenPackageFile(fileName, uIndex, extension);
                    return true;
                case ".tlk":
                    var elhw = new ExportLoaderHostedWindow(new TLKEditorExportLoader(), fileName)
                    {
                        Title = $"TLK Editor - {fileName}"
                    };
                    elhw.Show();
                    return true;
                case ".dlc":
                    var me = new MountEditorWindow();
                    me.Show();
                    me.LoadFile(fileName);
                    return true;
                case ".isb":
                case ".afc":
                    OpenTool<SoundplorerWPF>(s => s.LoadFile(fileName));
                    return true;
                case ".cnd":
                    var ce = new ConditionalsEditorWindow();
                    ce.Show();
                    ce.LoadFile(fileName);
                    return true;
                case ".bin":
                    if (CoalescedEditorWindow.IsCoalescedBinFileName(fileName))
                    {
                        OpenTool<CoalescedEditorWindow>(w => w.LoadCoalescedFile(fileName));
                        return true;
                    }
                    return false;
            }
            return false;
        }

        private static void OpenPackageFile(string fileName, int uIndex, string extension)
        {
            if (extension.Equals(".pcc", StringComparison.OrdinalIgnoreCase))
            {
                string selectedTool = OpenPccToolSelector.GetTool(fileName);

                switch (selectedTool)
                {
                    case PackageEditorTool:
                        OpenTool<PackageEditorWindow>((p) => p.LoadFile(fileName, uIndex));
                        break;
                    case DialogueEditorTool:
                        OpenTool<DialogueEditorWindow>((d) => d.LoadFile(fileName, uIndex));
                        break;
                    case SequenceEditorTool:
                        OpenTool<SequenceEditorWPF>((s) => s.LoadFileAndGoTo(fileName, uIndex));
                        break;
                    case LevelEditorTool:
                        OpenTool<LevelEditor>(async l => await l.LoadFileAsync(fileName));
                        break;
                    case MeshplorerTool:
                        OpenTool<MeshplorerWindow>((m) => m.LoadFile(fileName, uIndex));
                        break;
                }
            }
            else
            {
                OpenTool<PackageEditorWindow>((p) => p.LoadFile(fileName, uIndex));
            }
        }

        private static void OpenTool<T>(Action<T> toolAction = null)
            where T : System.Windows.Window, new()
        {
            var editor = new T();
            editor.Show();
            toolAction?.Invoke(editor);
            editor.Activate();
        }
    }
}
