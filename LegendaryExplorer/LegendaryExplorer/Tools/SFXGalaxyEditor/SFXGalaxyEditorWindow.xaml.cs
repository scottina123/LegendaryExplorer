using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.Win32;
using Path = System.IO.Path;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace LegendaryExplorer.Tools.SFXGalaxyEditor;

/// <summary>
/// LE3 editor for the SFXGalaxy object hierarchy stored in BioD_Nor_203aGalaxyMap.pcc.
/// The viewport is intentionally package-data-driven and does not depend on the legacy galaxy map tool.
/// </summary>
public partial class SFXGalaxyEditorWindow : WPFBase, IRecents
{
    private const int MapExtent = 1024;
    private const string VanillaGalaxyMapFile = "BioD_Nor_203aGalaxyMap.pcc";

    private readonly Dictionary<int, SFXGalaxyNode> _nodesByUIndex = [];
    private readonly Dictionary<int, string> _tlkCache = [];
    private readonly Dictionary<SFXGalaxyNode, FrameworkElement> _markerElements = [];
    private readonly Dictionary<SFXGalaxyNode, Point> _visibleCenters = [];
    private string _queuedFile;
    private int _queuedExportUIndex;
    private bool _handledInitialLoad;
    private bool _suppressPackageRefresh;
    private bool _refreshQueued;

    private SFXGalaxyNode _dragNode;
    private Point _dragStart;
    private Point _dragOrigin;
    private SFXGalaxyNode _relaySource;
    private Line _relayPreview;

    public ObservableCollectionExtended<SFXGalaxyNode> HierarchyRoots { get; } = [];
    public ObservableCollectionExtended<SFXGalaxyNode> SearchResults { get; } = [];
    public ObservableCollectionExtended<SFXGalaxyEditableExport> EditableExports { get; } = [];

    private SFXGalaxyNode _rootNode;
    private SFXGalaxyNode _currentNode;
    public SFXGalaxyNode CurrentNode
    {
        get => _currentNode;
        private set
        {
            if (SetProperty(ref _currentNode, value))
            {
                OnPropertyChanged(nameof(CurrentViewLabel));
                OnPropertyChanged(nameof(CurrentObjectCountText));
                UpdateStatus();
                BuildBreadcrumbs();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private SFXGalaxyNode _selectedNode;
    public SFXGalaxyNode SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                RefreshPropertyExports();
                UpdateStatus();
                RenderCurrentLevel();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private bool _showCoordinateGrid;
    public bool ShowCoordinateGrid
    {
        get => _showCoordinateGrid;
        set
        {
            if (SetProperty(ref _showCoordinateGrid, value))
            {
                RenderCurrentLevel();
            }
        }
    }

    private string _statusText = "Open an LE3 galaxy map package to begin.";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentViewLabel => CurrentNode?.Kind switch
    {
        SFXGalaxyNodeKind.Galaxy => "GALAXY VIEW",
        SFXGalaxyNodeKind.Cluster => "CLUSTER VIEW",
        SFXGalaxyNodeKind.System => "SYSTEM VIEW",
        SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.Anomaly => "PLANET VIEW",
        _ => "OBJECT VIEW"
    };

    public string CurrentObjectCountText => CurrentNode is null
        ? string.Empty
        : $"{CurrentNode.Children.Count} {ObjectNoun(CurrentNode.Children.Count)}";

    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand NavigateIntoCommand { get; }
    public ICommand FocusSearchCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }

    public string Toolname => "SFXGalaxyEditorLE3";

    public SFXGalaxyEditorWindow() : base("SFXGalaxy Editor (LE3)")
    {
        OpenCommand = new GenericCommand(OpenPackage);
        SaveCommand = new GenericCommand(SavePackage, () => Pcc is not null);
        SaveAsCommand = new GenericCommand(SavePackageAs, () => Pcc is not null);
        BackCommand = new GenericCommand(NavigateBack, () => CurrentNode?.Parent is not null);
        NavigateIntoCommand = new GenericCommand(NavigateIntoSelected, () => CanEnter(SelectedNode));
        FocusSearchCommand = new GenericCommand(() => SearchBox?.Focus(), () => Pcc is not null);
        DuplicateCommand = new GenericCommand(DuplicateSelected, () => SelectedNode is { Parent: not null, IsImplicitStar: false });
        DeleteCommand = new GenericCommand(DeleteSelected, () => SelectedNode is { Parent: not null, IsImplicitStar: false });

        InitializeComponent();
        SearchResultsList.ItemsSource = SearchResults;
        RecentsController.InitRecentControl(Toolname, Recents_MenuItem, LoadFile);
    }

    public SFXGalaxyEditorWindow(ExportEntry export) : this()
    {
        _queuedFile = export.FileRef.FilePath;
        _queuedExportUIndex = export.UIndex;
    }

    public static bool CanOpenExport(ExportEntry export) =>
        TryGetGalaxyObject(export, out _) && export.Game == MEGame.LE3;

    public static bool TryGetGalaxyObject(ExportEntry export, [NotNullWhen(true)] out ExportEntry galaxyObject)
    {
        for (ExportEntry current = export; current is not null; current = current.Parent as ExportEntry)
        {
            if (current.ClassName == "SFXGalaxy" || current.IsA("SFXGalaxyMapObject"))
            {
                galaxyObject = current;
                return true;
            }
        }

        galaxyObject = null;
        return false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_handledInitialLoad)
        {
            return;
        }
        _handledInitialLoad = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!string.IsNullOrWhiteSpace(_queuedFile))
            {
                string file = _queuedFile;
                int exportIndex = _queuedExportUIndex;
                _queuedFile = null;
                _queuedExportUIndex = 0;
                LoadFile(file);
                if (_nodesByUIndex.TryGetValue(exportIndex, out SFXGalaxyNode node))
                {
                    NavigateToSearchResult(node);
                }
                Activate();
                return;
            }

            if (Pcc is null)
            {
                OpenHighestMountedGalaxyMap(showErrors: true);
            }
            Activate();
        }));
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (e.Cancel)
        {
            return;
        }

        RecentsController?.Dispose();
        PropertiesInterpreter?.UnloadExport();
        MetadataLoader?.UnloadExport();
        HierarchyRoots.ClearEx();
        SearchResults.ClearEx();
        EditableExports.ClearEx();
        UnLoadMEPackage();
    }

    private void OpenPackage()
    {
        OpenFileDialog dialog = AppDirectories.GetOpenPackageDialog();
        if (DirectoryMemory.ShowDialog(dialog) == true)
        {
            LoadFile(dialog.FileName);
        }
    }

    private void OpenVanilla_Click(object sender, RoutedEventArgs e)
    {
        OpenHighestMountedGalaxyMap(showErrors: true);
    }

    private void OpenHighestMountedGalaxyMap(bool showErrors)
    {
        if (string.IsNullOrWhiteSpace(MEDirectories.GetDefaultGamePath(MEGame.LE3)))
        {
            if (showErrors)
            {
                MessageBox.Show(this,
                    "Configure your Legendary Edition installation path before opening the highest-mounted LE3 galaxy map.",
                    "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        if (!MELoadedFiles.TryGetHighestMountedFile(MEGame.LE3, VanillaGalaxyMapFile, out string filePath))
        {
            if (showErrors)
            {
                MessageBox.Show(this, $"Could not locate a mounted {VanillaGalaxyMapFile} in the configured LE3 installation.",
                    "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        LoadFile(filePath);
    }

    public void LoadFile(string fileName)
    {
        try
        {
            PropertiesInterpreter.UnloadExport();
            MetadataLoader.UnloadExport();
            LoadMEPackage(fileName);

            if (Pcc.Game != MEGame.LE3)
            {
                throw new InvalidDataException("SFXGalaxy Editor supports Legendary Edition 3 packages only.");
            }

            ExportEntry galaxy = Pcc.Exports.FirstOrDefault(e => e.ClassName == "SFXGalaxy" && !e.IsDefaultObject && !e.IsTrash());
            if (galaxy is null)
            {
                throw new InvalidDataException("This package does not contain an SFXGalaxy instance.");
            }

            RebuildHierarchy(galaxy.UIndex, galaxy.UIndex);
            // Match Level Editor: keep the exact loaded package path visible in the window title.
            Title = $"SFXGalaxy Editor (LE3) — {Pcc.FilePath}";
            RecentsController.AddRecent(fileName, false, Pcc.Game);
            RecentsController.SaveRecentList(true);
            OnPropertyChanged(nameof(Pcc));
            UpdateStatus();
        }
        catch (Exception exception)
        {
            PropertiesInterpreter.UnloadExport();
            MetadataLoader.UnloadExport();
            UnLoadMEPackage();
            HierarchyRoots.ClearEx();
            MessageBox.Show(this, $"Unable to open this galaxy map package:\n\n{exception.Message}",
                "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SavePackage()
    {
        await Pcc.SaveAsync();
        UpdateStatus();
    }

    private async void SavePackageAs()
    {
        SaveFileDialog dialog = new()
        {
            Filter = "Unreal package|*.pcc;*.upk;*.u|All files|*.*",
            FileName = Path.GetFileName(Pcc.FilePath)
        };
        if (DirectoryMemory.ShowDialog(dialog) == true)
        {
            await Pcc.SaveAsync(dialog.FileName);
            Title = $"SFXGalaxy Editor (LE3) — {Pcc.FilePath}";
            OnPropertyChanged(nameof(Pcc));
            UpdateStatus();
        }
    }

    private void BuildHierarchy(ExportEntry galaxy)
    {
        _nodesByUIndex.Clear();
        _tlkCache.Clear();
        HashSet<int> visited = [];
        _rootNode = BuildNode(galaxy, null, visited);
        if (_rootNode is null)
        {
            throw new InvalidDataException("The SFXGalaxy hierarchy could not be read.");
        }
    }

    private SFXGalaxyNode BuildNode(ExportEntry export, SFXGalaxyNode parent, HashSet<int> visited)
    {
        if (!visited.Add(export.UIndex))
        {
            return null;
        }

        PropertyCollection properties = export.GetProperties();
        SFXGalaxyNodeKind kind = Classify(export, properties);
        SFXGalaxyNode node = new()
        {
            Export = export,
            Parent = parent,
            Kind = kind,
            DisplayName = ResolveDisplayName(export, properties, kind),
            Description = ResolveDescription(properties),
            PosX = properties.GetProp<IntProperty>("PosX")?.Value ?? MapExtent / 2,
            PosY = properties.GetProp<IntProperty>("PosY")?.Value ?? MapExtent / 2
        };
        _nodesByUIndex[export.UIndex] = node;

        if (kind == SFXGalaxyNodeKind.System)
        {
            node.Children.Add(new SFXGalaxyNode
            {
                Export = export,
                Parent = node,
                Kind = SFXGalaxyNodeKind.Star,
                DisplayName = $"{node.DisplayName} star",
                Description = "The system's implicit central star. Its appearance is stored on SFXSystem as SunColor, StarColor, and FlareTint.",
                IsImplicitStar = true,
                PosX = MapExtent / 2,
                PosY = MapExtent / 2
            });
        }

        ArrayProperty<ObjectProperty> children = properties.GetProp<ArrayProperty<ObjectProperty>>("Children");
        if (children is not null)
        {
            foreach (ObjectProperty childReference in children)
            {
                if (childReference.ResolveToEntry(Pcc) is not ExportEntry childExport || childExport.IsTrash())
                {
                    continue;
                }

                SFXGalaxyNode child = BuildNode(childExport, node, visited);
                if (child is not null)
                {
                    node.Children.Add(child);
                }
            }
        }

        return node;
    }

    private static SFXGalaxyNodeKind Classify(ExportEntry export, PropertyCollection properties)
    {
        if (export.ClassName == "SFXGalaxy") return SFXGalaxyNodeKind.Galaxy;
        if (export.ClassName == "SFXCluster") return SFXGalaxyNodeKind.Cluster;
        if (export.ClassName == "SFXSystem") return SFXGalaxyNodeKind.System;
        if (export.ClassName.Contains("MassRelay", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.MassRelay;
        if (export.ClassName.Contains("FuelDepot", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.FuelDepot;
        if (export.ClassName.Contains("Reaper", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.Reaper;
        if (export.IsA("SFXPlanetFeature")) return SFXGalaxyNodeKind.Feature;
        if (export.IsA("BioPlanet"))
        {
            string systemType = properties.GetProp<EnumProperty>("SystemLevelType")?.Value.Name ?? string.Empty;
            string orbitType = properties.GetProp<EnumProperty>("OrbitRing")?.Value.Name ?? string.Empty;
            if (orbitType.Contains("ASTEROID", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.AsteroidBelt;
            if (systemType.Contains("ANOMALY", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.Anomaly;
            return SFXGalaxyNodeKind.Planet;
        }
        return SFXGalaxyNodeKind.Object;
    }

    private string ResolveDisplayName(ExportEntry export, PropertyCollection properties, SFXGalaxyNodeKind kind)
    {
        if (properties.GetProp<StringRefProperty>("DisplayName") is { Value: > 0 } displayName)
        {
            string resolved = ResolveTlk(displayName.Value);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        string tag = properties.GetProp<StrProperty>("Tag")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(tag) && !IsGenericTag(tag))
        {
            return tag;
        }

        return kind == SFXGalaxyNodeKind.Galaxy
            ? "The Milky Way"
            : $"{KindName(kind)} {export.ObjectName.Number}";
    }

    private string ResolveDescription(PropertyCollection properties)
    {
        foreach (string propertyName in new[] { "Description", "PlanetPlotLabel", "LandingSiteText", "ButtonLabel" })
        {
            if (properties.GetProp<StringRefProperty>(propertyName) is { Value: > 0 } stringRef)
            {
                string resolved = ResolveTlk(stringRef.Value);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved.Replace("\r", " ").Replace("\n", " ");
                }
            }
        }
        return string.Empty;
    }

    private string ResolveTlk(int stringRef)
    {
        if (_tlkCache.TryGetValue(stringRef, out string cached))
        {
            return cached;
        }

        string value;
        try
        {
            value = TLKManagerWPF.GlobalFindStrRefbyID(stringRef, Pcc)?.Trim().Trim('"') ?? string.Empty;
            if (value.Equals("No Data", StringComparison.OrdinalIgnoreCase) || value.StartsWith("No TLK", StringComparison.OrdinalIgnoreCase))
            {
                value = string.Empty;
            }
        }
        catch
        {
            value = string.Empty;
        }
        _tlkCache[stringRef] = value;
        return value;
    }

    private void RebuildHierarchy(int currentUIndex = 0, int selectedUIndex = 0, bool selectedWasStar = false, int editableExportUIndex = 0)
    {
        if (Pcc is null)
        {
            return;
        }

        ExportEntry galaxy = Pcc.Exports.FirstOrDefault(e => e.ClassName == "SFXGalaxy" && !e.IsDefaultObject && !e.IsTrash());
        if (galaxy is null)
        {
            return;
        }

        BuildHierarchy(galaxy);
        HierarchyRoots.ClearEx();
        HierarchyRoots.Add(_rootNode);

        CurrentNode = _nodesByUIndex.GetValueOrDefault(currentUIndex) ?? _rootNode;
        SFXGalaxyNode selected = _nodesByUIndex.GetValueOrDefault(selectedUIndex) ?? CurrentNode;
        if (selectedWasStar && selected.Kind == SFXGalaxyNodeKind.System)
        {
            selected = selected.Children.FirstOrDefault(c => c.IsImplicitStar) ?? selected;
        }
        SelectedNode = selected;
        if (editableExportUIndex != 0 && EditableExports.FirstOrDefault(option => option.Export.UIndex == editableExportUIndex) is { } preferred)
        {
            EditableExportCombo.SelectedItem = preferred;
        }
        RenderCurrentLevel();
        SelectTreeNode(selected);
    }

    private void NavigateTo(SFXGalaxyNode node)
    {
        if (!CanEnter(node))
        {
            return;
        }
        CurrentNode = node;
        SelectedNode = node;
        RenderCurrentLevel();
        SelectTreeNode(node);
    }

    private static bool CanEnter(SFXGalaxyNode node) => node is not null && !node.IsImplicitStar
        && (node.Kind is SFXGalaxyNodeKind.Galaxy or SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System || node.Children.Count > 0);

    private void NavigateIntoSelected() => NavigateTo(SelectedNode);

    private void NavigateBack()
    {
        if (CurrentNode?.Parent is SFXGalaxyNode parent)
        {
            CurrentNode = parent;
            SelectedNode = parent;
            RenderCurrentLevel();
            SelectTreeNode(parent);
        }
    }

    private void BuildBreadcrumbs()
    {
        if (BreadcrumbPanel is null)
        {
            return;
        }
        BreadcrumbPanel.Children.Clear();
        if (CurrentNode is null)
        {
            return;
        }

        List<SFXGalaxyNode> path = [];
        for (SFXGalaxyNode node = CurrentNode; node is not null; node = node.Parent)
        {
            path.Add(node);
        }
        path.Reverse();

        foreach (SFXGalaxyNode node in path)
        {
            if (BreadcrumbPanel.Children.Count > 0)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "›", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray
                });
            }
            Button button = new()
            {
                Content = node.DisplayName,
                Padding = new Thickness(4, 1, 4, 1),
                Tag = node,
                FontWeight = node == CurrentNode ? FontWeights.SemiBold : FontWeights.Normal
            };
            button.Click += (_, _) => NavigateTo((SFXGalaxyNode)button.Tag);
            BreadcrumbPanel.Children.Add(button);
        }
    }

    private void RenderCurrentLevel()
    {
        if (MapCanvas is null)
        {
            return;
        }

        MapCanvas.Children.Clear();
        _markerElements.Clear();
        _visibleCenters.Clear();
        if (CurrentNode is null)
        {
            return;
        }

        DrawSpaceBackground();
        if (ShowCoordinateGrid)
        {
            DrawCoordinateGrid();
        }
        if (CurrentNode.Kind == SFXGalaxyNodeKind.Galaxy)
        {
            DrawRelayConnections();
        }
        if (CurrentNode.Kind == SFXGalaxyNodeKind.System)
        {
            DrawSystemOrbits();
        }

        foreach (SFXGalaxyNode node in CurrentNode.Children)
        {
            DrawMarker(node);
        }
        OnPropertyChanged(nameof(CurrentObjectCountText));
    }

    private void DrawSpaceBackground()
    {
        Ellipse glow = new()
        {
            Width = 880,
            Height = 880,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush(Color.FromArgb(70, 24, 83, 113), Color.FromArgb(0, 1, 5, 10))
        };
        Canvas.SetLeft(glow, 72);
        Canvas.SetTop(glow, 72);
        MapCanvas.Children.Add(glow);

        Random random = new(203);
        for (int i = 0; i < 180; i++)
        {
            double size = random.NextDouble() * 1.8 + 0.5;
            Ellipse star = new()
            {
                Width = size,
                Height = size,
                Fill = i % 13 == 0 ? Brushes.LightSkyBlue : Brushes.White,
                Opacity = random.NextDouble() * 0.65 + 0.25,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(star, random.NextDouble() * MapExtent);
            Canvas.SetTop(star, random.NextDouble() * MapExtent);
            MapCanvas.Children.Add(star);
        }
    }

    private void DrawCoordinateGrid()
    {
        for (int coordinate = 0; coordinate <= MapExtent; coordinate += 64)
        {
            bool major = coordinate % 256 == 0;
            Brush stroke = new SolidColorBrush(major ? Color.FromArgb(85, 65, 137, 165) : Color.FromArgb(38, 91, 139, 158));
            MapCanvas.Children.Add(new Line { X1 = coordinate, Y1 = 0, X2 = coordinate, Y2 = MapExtent, Stroke = stroke, StrokeThickness = major ? 1.2 : 0.6, IsHitTestVisible = false });
            MapCanvas.Children.Add(new Line { X1 = 0, Y1 = coordinate, X2 = MapExtent, Y2 = coordinate, Stroke = stroke, StrokeThickness = major ? 1.2 : 0.6, IsHitTestVisible = false });
            if (major && coordinate < MapExtent)
            {
                TextBlock label = new() { Text = (coordinate / (double)MapExtent).ToString("0.00"), Foreground = Brushes.LightBlue, FontSize = 12, IsHitTestVisible = false };
                Canvas.SetLeft(label, coordinate + 3);
                Canvas.SetTop(label, 2);
                MapCanvas.Children.Add(label);
            }
        }
    }

    private void DrawRelayConnections()
    {
        HashSet<(int, int)> drawn = [];
        foreach (SFXGalaxyNode cluster in CurrentNode.Children.Where(n => n.Kind == SFXGalaxyNodeKind.Cluster))
        {
            ArrayProperty<ObjectProperty> links = cluster.Export.GetProperty<ArrayProperty<ObjectProperty>>("RelayConnections");
            if (links is null)
            {
                continue;
            }
            foreach (ObjectProperty link in links)
            {
                if (!_nodesByUIndex.TryGetValue(link.Value, out SFXGalaxyNode other) || other.Parent != CurrentNode)
                {
                    continue;
                }
                (int, int) key = cluster.Export.UIndex < other.Export.UIndex
                    ? (cluster.Export.UIndex, other.Export.UIndex)
                    : (other.Export.UIndex, cluster.Export.UIndex);
                if (!drawn.Add(key))
                {
                    continue;
                }
                MapCanvas.Children.Add(new Line
                {
                    X1 = cluster.PosX, Y1 = cluster.PosY, X2 = other.PosX, Y2 = other.PosY,
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 224, 61, 77)), StrokeThickness = 2.4,
                    IsHitTestVisible = false
                });
            }
        }
    }

    private void DrawSystemOrbits()
    {
        foreach (SFXGalaxyNode node in CurrentNode.Children.Where(n => !n.IsImplicitStar && ShouldShowOrbit(n)))
        {
            double radius = Math.Sqrt(Math.Pow(node.PosX - MapExtent / 2.0, 2) + Math.Pow(node.PosY - MapExtent / 2.0, 2));
            if (radius < 16)
            {
                continue;
            }
            Ellipse orbit = new()
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = node.Kind == SFXGalaxyNodeKind.AsteroidBelt
                    ? new SolidColorBrush(Color.FromArgb(175, 194, 169, 121))
                    : new SolidColorBrush(Color.FromArgb(90, 88, 150, 177)),
                StrokeThickness = node.Kind == SFXGalaxyNodeKind.AsteroidBelt ? 4 : 1.2,
                StrokeDashArray = node.Kind == SFXGalaxyNodeKind.AsteroidBelt ? new DoubleCollection([1, 3]) : null,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(orbit, MapExtent / 2.0 - radius);
            Canvas.SetTop(orbit, MapExtent / 2.0 - radius);
            MapCanvas.Children.Add(orbit);
        }
    }

    private static bool ShouldShowOrbit(SFXGalaxyNode node)
    {
        if (!node.Export.IsA("BioPlanet"))
        {
            return false;
        }
        PropertyCollection properties = node.Export.GetProperties();
        if (properties.GetProp<BoolProperty>("ShowOrbitRing") is { Value: false })
        {
            return false;
        }
        return properties.GetProp<EnumProperty>("OrbitRing")?.Value.Name != "OR_NONE";
    }

    private void DrawMarker(SFXGalaxyNode node)
    {
        double size = node.Kind switch
        {
            SFXGalaxyNodeKind.Star => 42,
            SFXGalaxyNodeKind.Cluster => 28,
            SFXGalaxyNodeKind.System => 24,
            SFXGalaxyNodeKind.Planet => 22,
            SFXGalaxyNodeKind.AsteroidBelt => 15,
            _ => 18
        };
        double x = Math.Clamp(node.PosX, 0, MapExtent);
        double y = Math.Clamp(node.PosY, 0, MapExtent);
        Canvas marker = new() { Width = 230, Height = 58, Tag = node, Cursor = Cursors.Hand };
        Ellipse body = new()
        {
            Width = size,
            Height = size,
            Fill = node.KindBrush,
            Stroke = node == SelectedNode ? Brushes.White : new SolidColorBrush(Color.FromArgb(210, 22, 91, 118)),
            StrokeThickness = node == SelectedNode ? 3 : 1.5
        };
        marker.Children.Add(body);

        if (node.Kind == SFXGalaxyNodeKind.Star)
        {
            Ellipse corona = new() { Width = size + 24, Height = size + 24, Stroke = new SolidColorBrush(Color.FromArgb(100, 255, 190, 42)), StrokeThickness = 8, IsHitTestVisible = false };
            Canvas.SetLeft(corona, -12);
            Canvas.SetTop(corona, -12);
            marker.Children.Insert(0, corona);
        }

        TextBlock label = new()
        {
            Text = node.DisplayName,
            Foreground = Brushes.White,
            FontSize = node.Kind is SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System ? 15 : 13,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(145, 6, 13, 20)),
            Padding = new Thickness(3, 1, 3, 1),
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, size + 6);
        Canvas.SetTop(label, Math.Max(0, size / 2 - 10));
        marker.Children.Add(label);

        if (node.Kind == SFXGalaxyNodeKind.Cluster && CurrentNode.Kind == SFXGalaxyNodeKind.Galaxy)
        {
            Ellipse connector = new()
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.IndianRed,
                Stroke = Brushes.White,
                StrokeThickness = 1,
                Cursor = Cursors.Cross,
                ToolTip = "Drag to another cluster to create a relay connection",
                Tag = node
            };
            Canvas.SetLeft(connector, size - 3);
            Canvas.SetTop(connector, size / 2 - 5);
            connector.MouseLeftButtonDown += RelayHandle_MouseLeftButtonDown;
            marker.Children.Add(connector);
        }

        marker.MouseLeftButtonDown += Marker_MouseLeftButtonDown;
        marker.MouseRightButtonUp += Marker_MouseRightButtonUp;
        Canvas.SetLeft(marker, x - size / 2);
        Canvas.SetTop(marker, y - size / 2);
        MapCanvas.Children.Add(marker);
        _markerElements[node] = marker;
        _visibleCenters[node] = new Point(x, y);
    }

    private void Marker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { Cursor: { } cursor } && cursor == Cursors.Cross)
        {
            return;
        }
        if (sender is not FrameworkElement { Tag: SFXGalaxyNode node })
        {
            return;
        }
        SelectedNode = node;
        if (e.ClickCount == 2 && node.Kind is SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System)
        {
            _dragNode = null;
            Mouse.Capture(null);
            NavigateTo(node);
            e.Handled = true;
            return;
        }
        if (node.IsImplicitStar)
        {
            e.Handled = true;
            return;
        }
        _dragNode = node;
        _dragStart = e.GetPosition(MapCanvas);
        _dragOrigin = new Point(node.PosX, node.PosY);
        Mouse.Capture(MapCanvas);
        e.Handled = true;
    }

    private void RelayHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SFXGalaxyNode cluster })
        {
            return;
        }
        SelectedNode = cluster;
        _relaySource = cluster;
        Point pointer = e.GetPosition(MapCanvas);
        _relayPreview = new Line
        {
            X1 = cluster.PosX, Y1 = cluster.PosY, X2 = pointer.X, Y2 = pointer.Y,
            Stroke = Brushes.OrangeRed, StrokeThickness = 2, StrokeDashArray = new DoubleCollection([4, 3]),
            IsHitTestVisible = false
        };
        MapCanvas.Children.Add(_relayPreview);
        Mouse.Capture(MapCanvas);
        e.Handled = true;
    }

    private void MapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        Point pointer = e.GetPosition(MapCanvas);
        if (_relaySource is not null && _relayPreview is not null)
        {
            _relayPreview.X2 = Math.Clamp(pointer.X, 0, MapExtent);
            _relayPreview.Y2 = Math.Clamp(pointer.Y, 0, MapExtent);
            return;
        }
        if (_dragNode is null || e.LeftButton != MouseButtonState.Pressed || !_markerElements.TryGetValue(_dragNode, out FrameworkElement marker))
        {
            return;
        }
        Vector delta = pointer - _dragStart;
        _dragNode.PosX = (int)Math.Round(Math.Clamp(_dragOrigin.X + delta.X, 0, MapExtent));
        _dragNode.PosY = (int)Math.Round(Math.Clamp(_dragOrigin.Y + delta.Y, 0, MapExtent));
        double markerSize = MarkerSize(_dragNode);
        Canvas.SetLeft(marker, _dragNode.PosX - markerSize / 2);
        Canvas.SetTop(marker, _dragNode.PosY - markerSize / 2);
        _visibleCenters[_dragNode] = new Point(_dragNode.PosX, _dragNode.PosY);
        UpdateStatus();
    }

    private void MapCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_relaySource is not null)
        {
            Point pointer = e.GetPosition(MapCanvas);
            SFXGalaxyNode target = _visibleCenters
                .Where(pair => pair.Key.Kind == SFXGalaxyNodeKind.Cluster && pair.Key != _relaySource)
                .OrderBy(pair => (pair.Value - pointer).Length)
                .FirstOrDefault(pair => (pair.Value - pointer).Length <= 45).Key;
            SFXGalaxyNode source = _relaySource;
            CancelRelayDrag();
            if (target is not null)
            {
                AddRelayConnection(source, target);
            }
            return;
        }
        if (_dragNode is not null)
        {
            SFXGalaxyNode node = _dragNode;
            _dragNode = null;
            Mouse.Capture(null);
            PropertyCollection properties = node.Export.GetProperties();
            properties.AddOrReplaceProp(new IntProperty(node.PosX, "PosX"));
            properties.AddOrReplaceProp(new IntProperty(node.PosY, "PosY"));
            _suppressPackageRefresh = true;
            node.Export.WriteProperties(properties);
            _suppressPackageRefresh = false;
            RenderCurrentLevel();
            UpdateStatus();
        }
    }

    private void MapCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released && _relaySource is not null)
        {
            CancelRelayDrag();
        }
    }

    private void CancelRelayDrag()
    {
        if (_relayPreview is not null)
        {
            MapCanvas.Children.Remove(_relayPreview);
        }
        _relayPreview = null;
        _relaySource = null;
        Mouse.Capture(null);
    }

    private void Marker_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SFXGalaxyNode node } marker)
        {
            return;
        }
        SelectedNode = node;
        ContextMenu menu = new();
        if (CanEnter(node))
        {
            MenuItem open = new() { Header = "Open" };
            open.Click += (_, _) => NavigateTo(node);
            menu.Items.Add(open);
        }
        if (node.Kind == SFXGalaxyNodeKind.Cluster)
        {
            MenuItem deleteConnection = new() { Header = "Delete connection" };
            foreach (SFXGalaxyNode connected in GetRelayConnections(node).OrderBy(n => n.DisplayName))
            {
                MenuItem item = new() { Header = connected.DisplayName };
                item.Click += (_, _) => RemoveRelayConnection(node, connected);
                deleteConnection.Items.Add(item);
            }
            deleteConnection.IsEnabled = deleteConnection.Items.Count > 0;
            menu.Items.Add(deleteConnection);
        }
        if (node.Parent is not null && !node.IsImplicitStar)
        {
            menu.Items.Add(new Separator());
            MenuItem delete = new() { Header = "Delete object…" };
            delete.Click += (_, _) => DeleteSelected();
            menu.Items.Add(delete);
        }
        marker.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddRelayConnection(SFXGalaxyNode first, SFXGalaxyNode second)
    {
        if (first == second || HasRelayConnection(first.Export, second.Export.UIndex))
        {
            return;
        }
        _suppressPackageRefresh = true;
        WriteRelayReference(first.Export, second.Export, true);
        WriteRelayReference(second.Export, first.Export, true);
        _suppressPackageRefresh = false;
        RenderCurrentLevel();
    }

    private void RemoveRelayConnection(SFXGalaxyNode first, SFXGalaxyNode second)
    {
        _suppressPackageRefresh = true;
        WriteRelayReference(first.Export, second.Export, false);
        WriteRelayReference(second.Export, first.Export, false);
        _suppressPackageRefresh = false;
        RenderCurrentLevel();
    }

    private static void WriteRelayReference(ExportEntry cluster, ExportEntry other, bool add)
    {
        PropertyCollection properties = cluster.GetProperties();
        ArrayProperty<ObjectProperty> connections = properties.GetProp<ArrayProperty<ObjectProperty>>("RelayConnections");
        if (connections is null)
        {
            if (!add) return;
            connections = new ArrayProperty<ObjectProperty>("RelayConnections");
            properties.Add(connections);
        }
        if (add)
        {
            if (connections.All(reference => reference.Value != other.UIndex))
            {
                connections.Add(new ObjectProperty(other));
            }
        }
        else
        {
            for (int i = connections.Count - 1; i >= 0; i--)
            {
                if (connections[i].Value == other.UIndex) connections.RemoveAt(i);
            }
        }
        cluster.WriteProperties(properties);
    }

    private static bool HasRelayConnection(ExportEntry cluster, int otherUIndex) =>
        cluster.GetProperty<ArrayProperty<ObjectProperty>>("RelayConnections")?.Any(reference => reference.Value == otherUIndex) == true;

    private IEnumerable<SFXGalaxyNode> GetRelayConnections(SFXGalaxyNode cluster)
    {
        ArrayProperty<ObjectProperty> connections = cluster.Export.GetProperty<ArrayProperty<ObjectProperty>>("RelayConnections");
        return connections?.Select(reference => _nodesByUIndex.GetValueOrDefault(reference.Value)).Where(node => node is not null)
               ?? [];
    }

    private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SFXGalaxyNode node)
        {
            SelectedNode = node;
        }
    }

    private void HierarchyTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HierarchyTree.SelectedItem is SFXGalaxyNode node)
        {
            NavigateTo(node);
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        SearchResults.ClearEx();
        if (_rootNode is null || query.Length < 2)
        {
            SearchResultsList.Visibility = Visibility.Collapsed;
            return;
        }
        SearchResults.AddRange(_rootNode.SelfAndDescendants()
            .Where(node => node.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100));
        SearchResultsList.Visibility = SearchResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchResultsList.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.Key == Key.Enter)
        {
            SFXGalaxyNode node = SearchResultsList.SelectedItem as SFXGalaxyNode ?? SearchResults.FirstOrDefault();
            if (node is not null)
            {
                NavigateToSearchResult(node);
                e.Handled = true;
            }
        }
    }

    private void SearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is SFXGalaxyNode node && SearchResultsList.IsKeyboardFocusWithin)
        {
            NavigateToSearchResult(node);
        }
    }

    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is SFXGalaxyNode node)
        {
            NavigateToSearchResult(node);
        }
    }

    private void NavigateToSearchResult(SFXGalaxyNode node)
    {
        CurrentNode = node.Parent ?? node;
        SelectedNode = node;
        SearchResultsList.Visibility = Visibility.Collapsed;
        RenderCurrentLevel();
        SelectTreeNode(node);
    }

    private void RefreshPropertyExports()
    {
        EditableExports.ClearEx();
        PropertiesInterpreter?.UnloadExport();
        MetadataLoader?.UnloadExport();
        if (SelectedNode?.Export is not ExportEntry export)
        {
            return;
        }

        string label = SelectedNode.IsImplicitStar ? "SFXSystem (star properties)" : $"Object: {export.ObjectName.Instanced}";
        EditableExports.Add(new SFXGalaxyEditableExport(export, label));
        if (!SelectedNode.IsImplicitStar && export.GetProperty<ObjectProperty>("Appearance")?.ResolveToEntry(Pcc) is ExportEntry appearance)
        {
            EditableExports.Add(new SFXGalaxyEditableExport(appearance, $"Appearance: {appearance.ObjectName.Instanced}"));
        }
        EditableExportCombo.SelectedIndex = 0;
    }

    private void EditableExportCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PropertiesInterpreter.UnloadExport();
        MetadataLoader.UnloadExport();
        if (EditableExportCombo.SelectedItem is SFXGalaxyEditableExport selected)
        {
            PropertiesInterpreter.LoadExport(selected.Export);
            MetadataLoader.LoadExport(selected.Export);
        }
    }

    private void AddCluster_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Cluster);
    private void AddSystem_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.System);
    private void AddPlanet_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Planet);
    private void AddAsteroidBelt_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.AsteroidBelt);
    private void AddAnomaly_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Anomaly);
    private void AddMassRelay_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.MassRelay);
    private void AddFuelDepot_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.FuelDepot);
    private void AddReaper_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Reaper);
    private void AddFeature_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Feature);

    private void AddKnownKind(SFXGalaxyNodeKind kind)
    {
        if (_rootNode is null)
        {
            return;
        }
        SFXGalaxyNode parent = FindCreationParent(kind);
        if (parent is null)
        {
            MessageBox.Show(this, CreationParentMessage(kind), "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SFXGalaxyNode template = _rootNode.SelfAndDescendants().FirstOrDefault(node => !node.IsImplicitStar && node.Kind == kind);
        if (template is null)
        {
            MessageBox.Show(this, $"This package does not contain a {KindName(kind)} object to use as a safe LE3 template.",
                "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        CreateFromTemplate(template, parent);
    }

    private void AddFromTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_rootNode is null)
        {
            return;
        }
        SFXGalaxyNode parent = ResolveTemplateParent();
        if (parent is null)
        {
            MessageBox.Show(this, "Select the galaxy, a cluster, a system, or a planet before choosing an object template.",
                "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IEnumerable<SFXGalaxyNode> candidates = parent.Kind switch
        {
            SFXGalaxyNodeKind.Galaxy => _rootNode.SelfAndDescendants().Where(n => n.Kind == SFXGalaxyNodeKind.Cluster),
            SFXGalaxyNodeKind.Cluster => _rootNode.SelfAndDescendants().Where(n => n.Kind == SFXGalaxyNodeKind.System),
            SFXGalaxyNodeKind.System => _rootNode.SelfAndDescendants().Where(n => n.Parent?.Kind == SFXGalaxyNodeKind.System && !n.IsImplicitStar),
            _ => _rootNode.SelfAndDescendants().Where(n => n.Kind == SFXGalaxyNodeKind.Feature)
        };
        Dictionary<string, SFXGalaxyNode> choices = [];
        foreach (SFXGalaxyNode candidate in candidates)
        {
            string key = $"{candidate.KindLabel} — {candidate.DisplayName} [{candidate.Export.ClassName}, #{candidate.Export.UIndex}]";
            choices.TryAdd(key, candidate);
        }
        if (choices.Count == 0)
        {
            MessageBox.Show(this, "No compatible templates exist in this package.", "SFXGalaxy Editor",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string chosen = InputComboBoxDialog.GetValue(this, "Choose an existing LE3 object as the template for the new object:",
            "Create galaxy map object", choices.Keys.OrderBy(key => key), choices.Keys.OrderBy(key => key).First());
        if (chosen is not null && choices.TryGetValue(chosen, out SFXGalaxyNode template))
        {
            CreateFromTemplate(template, parent);
        }
    }

    private void DuplicateSelected()
    {
        if (SelectedNode is { Parent: not null, IsImplicitStar: false } selected)
        {
            CreateFromTemplate(selected, selected.Parent);
        }
    }

    private void CreateFromTemplate(SFXGalaxyNode template, SFXGalaxyNode parent)
    {
        string suggested = $"New {template.KindLabel}";
        string label = PromptDialog.Prompt(this, "Internal editor label (stored in Tag):", "Create galaxy map object", suggested, true)?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }
        int existingStringRef = template.Export.GetProperty<StringRefProperty>("DisplayName")?.Value ?? 0;
        string stringRefText = PromptDialog.Prompt(this,
            "DisplayName TLK StringRef ID (leave blank or use 0 to edit it later in Properties):",
            "Create galaxy map object", existingStringRef.ToString(), true);
        if (stringRefText is null)
        {
            return;
        }
        int.TryParse(stringRefText, out int displayNameStringRef);

        _suppressPackageRefresh = true;
        try
        {
            ExportEntry clone = EntryCloner.CloneEntry(template.Export, incrementIndex: true, newParentUIndex: parent.Export.UIndex);
            PropertyCollection cloneProperties = clone.GetProperties();
            ResetClonedObjectProperties(cloneProperties, template.Kind);
            int ordinal = parent.Children.Count(child => !child.IsImplicitStar);
            cloneProperties.AddOrReplaceProp(new StrProperty(label, "Tag"));
            cloneProperties.AddOrReplaceProp(new IntProperty(Math.Clamp(256 + ordinal * 73 % 640, 0, MapExtent), "PosX"));
            cloneProperties.AddOrReplaceProp(new IntProperty(Math.Clamp(300 + ordinal * 109 % 560, 0, MapExtent), "PosY"));
            if (displayNameStringRef > 0)
            {
                cloneProperties.AddOrReplaceProp(new StringRefProperty(displayNameStringRef, "DisplayName"));
            }
            clone.WriteProperties(cloneProperties);
            CloneOwnedAppearance(template.Export, clone);
            AddChildReferences(parent, clone);
            int parentIndex = parent.Export.UIndex;
            RebuildHierarchy(parentIndex, clone.UIndex);
            if (_nodesByUIndex.TryGetValue(clone.UIndex, out SFXGalaxyNode created))
            {
                SelectTreeNode(created);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not create this object:\n\n{exception.Message}", "SFXGalaxy Editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _suppressPackageRefresh = false;
        }
    }

    private static void ResetClonedObjectProperties(PropertyCollection properties, SFXGalaxyNodeKind kind)
    {
        foreach (string transient in new[] { "ObjectActor", "TemporaryComponents", "ActorSpawned", "AudioComponent", "ScanEmitter" })
        {
            properties.RemoveNamedProperty(transient);
        }
        foreach (string arrayName in kind switch
                 {
                     SFXGalaxyNodeKind.Cluster => new[] { "Children", "Systems", "RelayConnections" },
                     SFXGalaxyNodeKind.System => new[] { "Children", "Planets", "aReapersTouchingPlayer" },
                     SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly => new[] { "Children", "Features", "AutoGrantedFeatures" },
                     _ => new[] { "Children" }
                 })
        {
            properties.GetProp<ArrayProperty<ObjectProperty>>(arrayName)?.Clear();
        }
    }

    private void CloneOwnedAppearance(ExportEntry template, ExportEntry clone)
    {
        if (template.GetProperty<ObjectProperty>("Appearance")?.ResolveToEntry(Pcc) is not ExportEntry appearance
            || appearance.Parent != template || appearance.IsDefaultObject)
        {
            return;
        }
        ExportEntry appearanceClone = EntryCloner.CloneEntry(appearance, incrementIndex: true, newParentUIndex: clone.UIndex);
        clone.WriteProperty(new ObjectProperty(appearanceClone, "Appearance"));
    }

    private static void AddChildReferences(SFXGalaxyNode parent, ExportEntry child)
    {
        PropertyCollection parentProperties = parent.Export.GetProperties();
        ArrayProperty<ObjectProperty> children = parentProperties.GetProp<ArrayProperty<ObjectProperty>>("Children");
        if (children is null)
        {
            children = new ArrayProperty<ObjectProperty>("Children");
            parentProperties.Add(children);
        }
        children.Add(new ObjectProperty(child));

        string typedArrayName = parent.Kind switch
        {
            SFXGalaxyNodeKind.Galaxy when child.ClassName == "SFXCluster" => "Clusters",
            SFXGalaxyNodeKind.Cluster when child.ClassName == "SFXSystem" => "Systems",
            // LE3's Planets array is really the table for every SFXSystemLevelObject,
            // including relays, depots, Reapers, and anomalies—not only BioPlanet.
            SFXGalaxyNodeKind.System => "Planets",
            _ when child.IsA("SFXPlanetFeature") => "Features",
            _ => null
        };
        if (typedArrayName is not null)
        {
            ArrayProperty<ObjectProperty> typed = parentProperties.GetProp<ArrayProperty<ObjectProperty>>(typedArrayName);
            if (typed is null)
            {
                typed = new ArrayProperty<ObjectProperty>(typedArrayName);
                parentProperties.Add(typed);
            }
            int tableId = typed.Count;
            typed.Add(new ObjectProperty(child));
            child.WriteProperty(new IntProperty(tableId, "TableID"));
        }
        parent.Export.WriteProperties(parentProperties);
    }

    private void DeleteSelected()
    {
        if (SelectedNode is not { Parent: not null, IsImplicitStar: false } target)
        {
            return;
        }
        int descendantCount = target.SelfAndDescendants().Count(node => !node.IsImplicitStar) - 1;
        string detail = descendantCount > 0 ? $" and its {descendantCount} descendant objects" : string.Empty;
        if (MessageBox.Show(this, $"Delete {target.DisplayName}{detail}?\n\nThe exports will be moved to the package Trash tree.",
                "Delete galaxy map object", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SFXGalaxyNode parent = target.Parent;
        _suppressPackageRefresh = true;
        try
        {
            if (target.Kind == SFXGalaxyNodeKind.Cluster)
            {
                foreach (SFXGalaxyNode connected in GetRelayConnections(target).ToList())
                {
                    WriteRelayReference(connected.Export, target.Export, false);
                }
            }
            RemoveChildReferences(parent.Export, target.Export);
            EntryPruner.TrashEntryAndDescendants(target.Export);
            RebuildHierarchy(parent.Export.UIndex, parent.Export.UIndex);
        }
        finally
        {
            _suppressPackageRefresh = false;
        }
    }

    private static void RemoveChildReferences(ExportEntry parent, ExportEntry child)
    {
        PropertyCollection properties = parent.GetProperties();
        foreach (string arrayName in new[] { "Children" })
        {
            if (properties.GetProp<ArrayProperty<ObjectProperty>>(arrayName) is not { } array) continue;
            for (int i = array.Count - 1; i >= 0; i--)
            {
                if (array[i].Value == child.UIndex) array.RemoveAt(i);
            }
        }
        foreach (string arrayName in new[] { "Clusters", "Systems", "Planets", "Features" })
        {
            if (properties.GetProp<ArrayProperty<ObjectProperty>>(arrayName) is not { } array) continue;
            foreach (ObjectProperty reference in array.Where(reference => reference.Value == child.UIndex))
            {
                reference.Value = 0;
            }
        }
        parent.WriteProperties(properties);
    }

    private SFXGalaxyNode FindCreationParent(SFXGalaxyNodeKind kind) => kind switch
    {
        SFXGalaxyNodeKind.Cluster => _rootNode,
        SFXGalaxyNodeKind.System => FindContextNode(SFXGalaxyNodeKind.Cluster),
        SFXGalaxyNodeKind.Feature => FindContextNode(SFXGalaxyNodeKind.Planet, SFXGalaxyNodeKind.Anomaly, SFXGalaxyNodeKind.AsteroidBelt),
        _ => FindContextNode(SFXGalaxyNodeKind.System)
    };

    private SFXGalaxyNode ResolveTemplateParent()
    {
        SFXGalaxyNode node = SelectedNode ?? CurrentNode;
        while (node is not null)
        {
            if (node.Kind is SFXGalaxyNodeKind.Galaxy or SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System
                or SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.Anomaly or SFXGalaxyNodeKind.AsteroidBelt)
            {
                return node;
            }
            node = node.Parent;
        }
        return null;
    }

    private SFXGalaxyNode FindContextNode(params SFXGalaxyNodeKind[] kinds)
    {
        for (SFXGalaxyNode node = SelectedNode ?? CurrentNode; node is not null; node = node.Parent)
        {
            if (kinds.Contains(node.Kind)) return node;
        }
        for (SFXGalaxyNode node = CurrentNode; node is not null; node = node.Parent)
        {
            if (kinds.Contains(node.Kind)) return node;
        }
        return null;
    }

    private static string CreationParentMessage(SFXGalaxyNodeKind kind) => kind switch
    {
        SFXGalaxyNodeKind.System => "Select a cluster before adding a system.",
        SFXGalaxyNodeKind.Feature => "Select a planet or anomaly before adding a planet feature.",
        SFXGalaxyNodeKind.Cluster => "Open an SFXGalaxy package before adding a cluster.",
        _ => "Select a system before adding this object."
    };

    private void UpdateStatus()
    {
        if (Pcc is null)
        {
            StatusText = "Open an LE3 galaxy map package to begin.";
            return;
        }
        string path = CurrentNode is null ? string.Empty : string.Join(" › ", GetPath(CurrentNode).Select(node => node.DisplayName));
        string selection = SelectedNode is null ? string.Empty : $"  |  Selected: {SelectedNode.DisplayName} ({SelectedNode.PosX}, {SelectedNode.PosY})";
        StatusText = $"{Path.GetFileName(Pcc.FilePath)}  |  {path}{selection}";
    }

    private static IEnumerable<SFXGalaxyNode> GetPath(SFXGalaxyNode node)
    {
        Stack<SFXGalaxyNode> path = new();
        for (; node is not null; node = node.Parent) path.Push(node);
        return path;
    }

    private void SelectTreeNode(SFXGalaxyNode node)
    {
        if (HierarchyTree is null || node is null)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ExpandAncestors(node);
            TreeViewItem container = FindTreeViewItem(HierarchyTree, node);
            if (container is not null)
            {
                container.IsSelected = true;
                container.BringIntoView();
            }
        }));
    }

    private void ExpandAncestors(SFXGalaxyNode node)
    {
        foreach (SFXGalaxyNode ancestor in GetPath(node).TakeWhile(item => item != node))
        {
            if (FindTreeViewItem(HierarchyTree, ancestor) is TreeViewItem item)
            {
                item.IsExpanded = true;
                item.UpdateLayout();
            }
        }
    }

    private static TreeViewItem FindTreeViewItem(ItemsControl parent, object target)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem direct)
        {
            return direct;
        }
        foreach (object item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem child) continue;
            TreeViewItem result = FindTreeViewItem(child, target);
            if (result is not null) return result;
        }
        return null;
    }

    public override void HandleUpdate(List<PackageUpdate> updates)
    {
        if (_suppressPackageRefresh || Pcc is null || !updates.Any(update => update.Change.HasFlag(PackageChange.Export)))
        {
            return;
        }
        if (_refreshQueued)
        {
            return;
        }
        _refreshQueued = true;
        int currentIndex = CurrentNode?.Export?.UIndex ?? 0;
        int selectedIndex = SelectedNode?.Export?.UIndex ?? 0;
        bool selectedWasStar = SelectedNode?.IsImplicitStar == true;
        int editableExportIndex = (EditableExportCombo?.SelectedItem as SFXGalaxyEditableExport)?.Export.UIndex ?? 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            RebuildHierarchy(currentIndex, selectedIndex, selectedWasStar, editableExportIndex);
        }));
    }

    public void PropogateRecentsChange(string propogationToolSource, IEnumerable<RecentsControl.RecentItem> newRecents) =>
        RecentsController.PropogateRecentsChange(false, newRecents);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length != 1 || !files[0].EndsWith(".pcc", StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            LoadFile(files[0]);
        }
    }

    private static bool IsGenericTag(string tag) => tag.Equals("Galaxy", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("Cluster", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("System", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("Planet", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("Feature", StringComparison.OrdinalIgnoreCase);

    private static string KindName(SFXGalaxyNodeKind kind) => kind switch
    {
        SFXGalaxyNodeKind.AsteroidBelt => "Asteroid Belt",
        SFXGalaxyNodeKind.MassRelay => "Mass Relay",
        SFXGalaxyNodeKind.FuelDepot => "Fuel Depot",
        _ => kind.ToString()
    };

    private static string ObjectNoun(int count) => count == 1 ? "object" : "objects";
    private static double MarkerSize(SFXGalaxyNode node) => node.Kind switch
    {
        SFXGalaxyNodeKind.Star => 42,
        SFXGalaxyNodeKind.Cluster => 28,
        SFXGalaxyNodeKind.System => 24,
        SFXGalaxyNodeKind.Planet => 22,
        SFXGalaxyNodeKind.AsteroidBelt => 15,
        _ => 18
    };
}
