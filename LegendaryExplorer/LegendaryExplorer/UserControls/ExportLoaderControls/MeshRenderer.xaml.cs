using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorer.UserControls.SharedToolControls.Scene3D;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkeletalMesh = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>
/// Interaction logic for MeshRenderer.xaml
/// </summary>
public partial class MeshRenderer : ExportLoaderControl, ISceneRenderContextConfigurable
{
    private static readonly string[] parsableClasses = ["SkeletalMesh", "StaticMesh", "FracturedStaticMesh", "BioSocketSupermodel", "ModelComponent", "Model"];

    #region 3D

    public MeshRenderContext MeshContext { get; }

    private bool _rotating = Settings.Meshplorer_ViewRotating;
    private bool _renderWireframe;
    private bool _renderSolid = true;
    private bool _renderGameShader;
    private bool _firstperson;

    public bool Rotating
    {
        get => _rotating;
        set
        {
            if (SetProperty(ref _rotating, value))
            {
                Settings.Meshplorer_ViewRotating = value;
                Settings.Save();
            }
        }
    }

    public bool RenderWireframe
    {
        get => _renderWireframe;
        set => SetProperty(ref _renderWireframe, value);
    }

    private bool _canUseGameShaders;
    public bool CanUseGameShaders
    {
        get => _canUseGameShaders;
        set => SetProperty(ref _canUseGameShaders, value);
    }

    public bool RenderGameShader
    {
        get => _renderGameShader;
        set
        {
            if (SetProperty(ref _renderGameShader, value) && _renderGameShader)
            {
                RenderSolid = false;
                if (GameShaderMeshPreview is null)
                {
                    LoadExport(CurrentLoadedExport);
                }
            }
        }
    }

    public bool RenderSolid
    {
        get => _renderSolid;
        set
        {
            if (SetProperty(ref _renderSolid, value) && _renderSolid)
            {
                RenderGameShader = false;
            }
        }
    }

    public bool FirstPerson
    {
        get => _firstperson;
        set
        {
            if (SetProperty(ref _firstperson, value))
            {
                MeshContext.Camera.FirstPerson = value;
            }
        }
    }

    private int _currentLOD;
    public int CurrentLOD
    {
        get => _currentLOD;
        set
        {
            if (SetProperty(ref _currentLOD, value))
            {
                //SceneViewer.Context.RenderScene();
            }
        }
    }
    public ObservableCollectionExtended<string> LODPicker { get; } = [];

    #region DISPLAY OPTIONS
    private bool _setAlphaToBlack = true;
    public bool SetAlphaToBlack
    {
        get => _setAlphaToBlack;
        set
        {
            SetProperty(ref _setAlphaToBlack, value);
            if (value)
            {
                this.MeshContext.RenderFlags |= RenderContext.ShaderFlags.AlphaAsBlack;
            }
            else
            {
                this.MeshContext.RenderFlags &= ~RenderContext.ShaderFlags.AlphaAsBlack;
            }
        }
    }

    private bool _showRedChannel = true;
    public bool ShowRedChannel
    {
        get => _showRedChannel;
        set
        {
            SetProperty(ref _showRedChannel, value);
            if (value)
            {
                this.MeshContext.RenderFlags |= RenderContext.ShaderFlags.EnableRedChannel;
            }
            else
            {
                this.MeshContext.RenderFlags &= ~RenderContext.ShaderFlags.EnableRedChannel;
            }
        }
    }

    private bool _showGreenChannel = true;
    public bool ShowGreenChannel
    {
        get => _showGreenChannel;
        set
        {
            SetProperty(ref _showGreenChannel, value);
            if (value)
            {
                this.MeshContext.RenderFlags |= RenderContext.ShaderFlags.EnableGreenChannel;
            }
            else
            {
                this.MeshContext.RenderFlags &= ~RenderContext.ShaderFlags.EnableGreenChannel;
            }
        }
    }

    private bool _showBlueChannel = true;
    public bool ShowBlueChannel
    {
        get => _showBlueChannel;
        set
        {
            SetProperty(ref _showBlueChannel, value);
            if (value)
            {
                this.MeshContext.RenderFlags |= RenderContext.ShaderFlags.EnableBlueChannel;
            }
            else
            {
                this.MeshContext.RenderFlags &= ~RenderContext.ShaderFlags.EnableBlueChannel;
            }
        }
    }

    private bool _showAlphaChannel = true;
    public bool ShowAlphaChannel
    {
        get => _showAlphaChannel;
        set
        {
            SetProperty(ref _showAlphaChannel, value);
            if (value)
            {
                this.MeshContext.RenderFlags |= RenderContext.ShaderFlags.EnableAlphaChannel;
            }
            else
            {
                this.MeshContext.RenderFlags &= ~RenderContext.ShaderFlags.EnableAlphaChannel;
            }
        }
    }

    private System.Windows.Media.Color _backgroundColor = Colors.White;
    public System.Windows.Media.Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            SetProperty(ref _backgroundColor, value);
            MeshContext.BackgroundColor = value;
        }
    }
    #endregion

    private ModelPreview<LEVertex> GameShaderMeshPreview;
    private ModelPreview<WorldVertex> LEXShaderMeshPreview;

    /// <summary>
    /// Value is true after _Loaded is called. False after _Unloaded (which if in tab control, is called when different tab is selected)
    /// </summary>
    private bool ControlIsLoaded;
    private Mesh<WorldVertex> STMCollisionMesh;
    private Action ViewportLoadAction = null;

    private void SceneContext_RenderScene(object sender, EventArgs e)
    {
        if (CurrentLOD < 0) { CurrentLOD = 0; }

        if (RenderGameShader && GameShaderMeshPreview is not null && CurrentLOD < GameShaderMeshPreview.LODs.Count)
        {
            MeshContext.Wireframe = false;
            Span<RenderPass> passes = [RenderPass.Base, RenderPass.Hair];
            foreach (RenderPass renderPass in passes)
            {
                GameShaderMeshPreview.Render(renderPass, MeshContext, CurrentLOD);
            }
        }
        if (LEXShaderMeshPreview is not null && CurrentLOD < LEXShaderMeshPreview.LODs.Count)
        {
            if (RenderSolid)
            {
                MeshContext.Wireframe = false;
                LEXShaderMeshPreview.Render(RenderPass.ANY, MeshContext, CurrentLOD);
            }
            if (RenderWireframe)
            {
                MeshContext.Wireframe = true;
                LEXShaderMeshPreview.Render(RenderPass.ANY, MeshContext, CurrentLOD);
            }
        }
        if (IsStaticMesh && ShowCollisionMesh && STMCollisionMesh != null)
        {
            MeshContext.RenderMeshAsWireframe(STMCollisionMesh);
        }
    }

    private void CenterView()
    {
        if (CurrentLOD >= 0)
        {
            Vector3? origin = null;
            if (GameShaderMeshPreview != null && GameShaderMeshPreview.LODs.Count > CurrentLOD)
            {
                origin = GameShaderMeshPreview.LODs[CurrentLOD].Mesh.BaseBounds.Origin;
            }
            else if (LEXShaderMeshPreview != null && LEXShaderMeshPreview.LODs.Count > CurrentLOD)
            {
                origin = LEXShaderMeshPreview.LODs[CurrentLOD].Mesh.BaseBounds.Origin;
            }
            if (origin is not null)
            {
                MeshContext.Camera.Position = origin.Value;
                MeshContext.Camera.Pitch = -MathF.PI / 7.0f;
                if (MeshContext.Camera.FirstPerson)
                {
                    MeshContext.Camera.Position -= MeshContext.Camera.CameraForward * MeshContext.Camera.FocusDepth;
                }
            }
            else
            {
                MeshContext.Camera.Position = Vector3.Zero;
                MeshContext.Camera.Pitch = -MathF.PI / 5.0f;
                MeshContext.Camera.Yaw = MathF.PI / 4.0f;
            }
        }
    }
    #endregion

    #region Busy variables
    private bool _isBusy;

    private readonly Stopwatch sw = new();
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy && !value)
            {
                sw.Stop();
                Debug.WriteLine($"MeshRenderer busy time: {sw.Elapsed}");
            }
            else if (!_isBusy && value)
            {
                sw.Reset();
                sw.Start();
            }

            if (SetProperty(ref _isBusy, value))
            {
                IsBusyChanged?.Invoke(this, EventArgs.Empty); //caller will just fetch and update this value
            }
        }
    }

    private bool _busyProgressIndeterminate = true;

    public bool BusyProgressIndeterminate
    {
        get => _busyProgressIndeterminate;
        set => SetProperty(ref _busyProgressIndeterminate, value);
    }

    private string _busyText;

    public string BusyText
    {
        get => _busyText;
        set => SetProperty(ref _busyText, value);
    }

    private int _busyProgressBarMax = 100;

    public int BusyProgressBarMax
    {
        get => _busyProgressBarMax;
        set => SetProperty(ref _busyProgressBarMax, value);
    }

    private int _busyProgressBarValue;
    public int BusyProgressBarValue
    {
        get => _busyProgressBarValue;
        set => SetProperty(ref _busyProgressBarValue, value);
    }

    #endregion

    #region Bindings
    private bool _isStaticMesh;
    public bool IsStaticMesh
    {
        get => _isStaticMesh;
        set => SetProperty(ref _isStaticMesh, value);
    }

    private bool _isModel;
    public bool IsModel
    {
        get => _isModel;
        set => SetProperty(ref _isModel, value);
    }

    private bool _isSkeletalMesh;
    public bool IsSkeletalMesh
    {
        get => _isSkeletalMesh;
        set => SetProperty(ref _isSkeletalMesh, value);
    }

    private bool _isBrush;
    public bool IsBrush
    {
        get => _isBrush;
        set => SetProperty(ref _isBrush, value);
    }

    private bool _showCollisionMesh;
    public bool ShowCollisionMesh
    {
        get => _showCollisionMesh;
        set => SetProperty(ref _showCollisionMesh, value);
    }

    private float _cameraPitch, _cameraYaw, _cameraX, _cameraY, _cameraZ, _cameraFOV, _cameraZNear, _cameraZFar;
    public float CameraPitch
    {
        get => _cameraPitch;
        set => SetProperty(ref _cameraPitch, value);
    }

    public float CameraYaw
    {
        get => _cameraYaw;
        set => SetProperty(ref _cameraYaw, value);
    }

    public float CameraX
    {
        get => _cameraX;
        set => SetProperty(ref _cameraX, value);
    }

    public float CameraY
    {
        get => _cameraY;
        set => SetProperty(ref _cameraY, value);
    }

    public float CameraZ
    {
        get => _cameraZ;
        set => SetProperty(ref _cameraZ, value);
    }

    public float CameraFOV
    {
        get => _cameraFOV;
        set
        {
            if (SetProperty(ref _cameraFOV, value))
            {
                MeshContext.Camera.FOV = LegendaryExplorerCore.SharpDX.MathUtil.DegreesToRadians(value);
            }
        }
    }

    public float CameraZNear
    {
        get => _cameraZNear;
        set
        {
            if (SetProperty(ref _cameraZNear, value))
            {
                MeshContext.Camera.ZNear = value;
            }
        }
    }

    public float CameraZFar
    {
        get => _cameraZFar;
        set
        {
            if (SetProperty(ref _cameraZFar, value))
            {
                MeshContext.Camera.ZFar = value;
            }
        }
    }

    private bool _useDegrees = true, _useRadians, _useUnreal;

    public bool UseDegrees
    {
        get => _useDegrees;
        set => SetProperty(ref _useDegrees, value);
    }

    public bool UseRadians
    {
        get => _useRadians;
        set => SetProperty(ref _useRadians, value);
    }

    public bool UseUnreal
    {
        get => _useUnreal;
        set => SetProperty(ref _useUnreal, value);
    }

    #endregion

    private readonly bool startingUp;
    public MeshRenderer() : base("Mesh Renderer")
    {
        startingUp = true;
        DataContext = this;
        LoadCommands();
        InitializeComponent();
        MeshContext = new MeshRenderContext();
        if (ColorConverter.ConvertFromString(Settings.Meshplorer_BackgroundColor) is System.Windows.Media.Color color)
        {
            BackgroundColor = color;
        }
        SceneViewer.Context = MeshContext;
        //MeshContext.BackgroundColor = color is not null ? new Color(color.Value.R, color.Value.G, color.Value.B) : Color.FromRgba(0x999999);
        SceneViewer.Loaded += (sender, args) =>
        {
            if (MeshContext.IsReady)
            {
                this.ViewportLoadAction?.Invoke();
            }
            this.ViewportLoadAction = null;
        };

        startingUp = false;
    }

    public ICommand UModelExportCommand { get; set; }

    private void LoadCommands()
    {
        UModelExportCommand = new GenericCommand(EnsureUModelAndExport, CanExportViaUModel);
    }

    public event EventHandler IsBusyChanged;

    private bool CanExportViaUModel() => CurrentLoadedExport != null && (IsStaticMesh || IsSkeletalMesh);

    public static bool CanParseStatic(ExportEntry exportEntry)
    {
        return !exportEntry.IsDefaultObject &&
               (parsableClasses.Contains(exportEntry.ClassName, StringComparer.OrdinalIgnoreCase) ||
                (exportEntry.ClassName.CaseInsensitiveEquals("BrushComponent") && exportEntry.GetProperty<StructProperty>("BrushAggGeom") != null) ||
                (exportEntry.Game.IsMEGame() && exportEntry.ClassName.CaseInsensitiveEquals("StaticMeshComponent") && exportEntry.GetProperty<ObjectProperty>("StaticMesh")?.Value != 0));
    }

    public override bool CanParse(ExportEntry exportEntry)
    {
        return CanParseStatic(exportEntry);
    }

    /// <summary>
    /// Used for debugging by listing the used instances
    /// </summary>
    //public ObservableCollectionExtended<PreviewTextureCache.PreviewTextureEntry> SceneViewerProperty => SceneViewer?.Context?.TextureCache?.AssetCache;

    private List<IEntry> OverlayMaterials = null;
    public override void LoadExport(ExportEntry exportEntry)
    {
        UnloadExport();
        if (exportEntry == null)
            return; // Can reload due to static mesh component looking for static mesh


        //SceneViewer.Context.BackgroundColor = new SharpDX.Color(128, 128, 128);
        CurrentLoadedExport = exportEntry;
        CurrentLOD = 0;
        CanUseGameShaders = exportEntry.Game.IsLEGame();
        if (CanUseGameShaders is false && RenderGameShader)
        {
            RenderSolid = true;
        }

        var assetCache = MeshContext.PackageCache;
        if (exportEntry.ClassName is "StaticMeshComponent")
        {
            var mesh = CurrentLoadedExport.GetProperty<ObjectProperty>("StaticMesh")?.ResolveToExport(exportEntry.FileRef, assetCache);
            if (mesh != null)
            {
                var mats = CurrentLoadedExport.GetProperty<ArrayProperty<ObjectProperty>>("Materials");
                if (mats != null)
                {
                    OverlayMaterials = mats.Select(x => x.Value != 0 ? x.ResolveToEntry(CurrentLoadedExport.FileRef) : null).ToList();
                }
            }

            // Reload on the mesh.
            LoadExport(mesh);
            return;
        }

        BusyText = "Fetching assets";
        BusyProgressIndeterminate = true;
        IsBusy = true;

        switch (CurrentLoadedExport.ClassName)
        {
            case "StaticMesh" or "FracturedStaticMesh":
                IsStaticMesh = true;
                break;
            case "BrushComponent":
                IsBrush = true;
                break;
            case "ModelComponent":
            case "Model":
                IsModel = true;
                break;
            default:
                if (CurrentLoadedExport.IsA("SkeletalMesh"))
                {
                    IsSkeletalMesh = true;
                    break;
                }
                else
                {
                    IsBusy = false;
                    return;
                }
        }

        Task.Run(() =>
        {
            Mesh<WorldVertex> stmCollisionMesh = null;
            ModelPreview<WorldVertex> lexModel = null;
            ModelPreview<LEVertex> gameModel = null;
            Exception error = null;
            try
            {
                if (CanUseGameShaders && RenderGameShader && !RefShaderCacheReader.IsShaderOffsetsDictInitialized(Pcc.Game))
                {
                    RefShaderCacheReader.PopulateOffsets(Pcc.Game);
                }
                string className = CurrentLoadedExport.ClassName;
                PreloadedModelData pmd = null;
                switch (className)
                {
                    case "StaticMesh" or "FracturedStaticMesh":
                        var statM = ObjectBinary.From<StaticMesh>(CurrentLoadedExport);
                        if (OverlayMaterials != null)
                        {
                            statM.SetMaterials(OverlayMaterials, true);
                            OverlayMaterials = null;
                        }
                        stmCollisionMesh = MeshContext.GetMeshFromAggGeom(statM.GetCollisionMeshProperty(Pcc));
                        if (RenderGameShader)
                        {
                            gameModel = new ModelPreview<LEVertex>(MeshContext, statM, CurrentLOD);
                        }
                        lexModel = new ModelPreview<WorldVertex>(MeshContext, statM, CurrentLOD);
                        break;
                    case "BrushComponent":
                        var structProp = CurrentLoadedExport.GetProperty<StructProperty>("BrushAggGeom");
                        if (structProp is not null)
                        {
                            lexModel = new ModelPreview<WorldVertex>(MeshContext, MeshContext.GetMeshFromAggGeom(structProp));
                        }
                        break;
                    case "ModelComponent":
                        pmd = PreloadedModelData.LoadModelComponent(CurrentLoadedExport, assetCache);
                        break;
                    case "Model":
                        pmd = PreloadedModelData.LoadModel(CurrentLoadedExport, assetCache);
                        break;
                    default:
                        if (CurrentLoadedExport.IsA("SkeletalMesh"))
                        {
                            var skm = ObjectBinary.From<SkeletalMesh>(CurrentLoadedExport);
                            if (OverlayMaterials != null)
                            {
                                skm.SetMaterials(OverlayMaterials, true);
                                OverlayMaterials = null;
                            }
                            if (RenderGameShader)
                            {
                                gameModel = new ModelPreview<LEVertex>(MeshContext, skm);
                            }
                            lexModel = new ModelPreview<WorldVertex>(MeshContext, skm);
                            break;
                        }
                        throw new Exception($"Cannot render a '{className}'");
                }

                if (pmd is not null)
                {
                    switch (pmd.meshObject)
                    {
                        case ModelComponent mc:
                            lexModel = new ModelPreview<WorldVertex>(MeshContext, GetMeshFromModelComponent(mc), pmd);
                            break;
                        case Model m:
                            var sections = new List<ModelPreviewSection>();
                            Mesh<WorldVertex> mesh = GetMeshFromModelSubcomponents(m, sections);
                            pmd.sections = sections;
                            lexModel = new ModelPreview<WorldVertex>(MeshContext, mesh, pmd);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                error = e;
            }
            return (stmCollisionMesh, lexModel, gameModel, error);
        }).ContinueWithOnUIThread(prevTask =>
        {
            IsBusy = false;
            if (CurrentLoadedExport == null)
            {
                //in the time since the previous task was started, the export has been unloaded
                return;
            }
            var (stmCollisionMesh, lexModel, gameModel, error) = prevTask.Result;
            if (error is not null)
            {
                new ExceptionHandlerDialog(error).Show();
                return;
            }
            if (lexModel is not null || gameModel is not null)
            {
                void loadPreviewAction()
                {
                    STMCollisionMesh = prevTask.Result.stmCollisionMesh;
                    LEXShaderMeshPreview = lexModel;
                    GameShaderMeshPreview = gameModel;

                    assetCache.ReleasePackages();
                    LODPicker.ClearEx();
                    if (GameShaderMeshPreview?.LODs?.Count > 0)
                    {
                        MeshContext.Camera.FocusDepth = GameShaderMeshPreview.LODs[0].Mesh.BaseBounds.SphereRadius * 1.5f;
                        for (int i = 0; i < GameShaderMeshPreview.LODs.Count; i++)
                        {
                            LODPicker.Add($"LOD{i}");
                        }
                    }
                    else if (LEXShaderMeshPreview?.LODs?.Count > 0)
                    {
                        MeshContext.Camera.FocusDepth = LEXShaderMeshPreview.LODs[0].Mesh.BaseBounds.SphereRadius * 1.5f;
                        for (int i = 0; i < LEXShaderMeshPreview.LODs.Count; i++)
                        {
                            LODPicker.Add($"LOD{i}");
                        }
                    }
                    CenterView();
                }

                LODPicker.ClearEx();
                //clearing the LODPicker will set CurrentLOD to -1
                //if it is -1, meshes will not render.
                CurrentLOD = 0;

                // We can't call graphics methods until the render control has been loaded by WPF - only then will it have initialized D3D.
                if (this.MeshContext.IsReady)
                {
                    loadPreviewAction();
                }
                else
                {
                    this.ViewportLoadAction = loadPreviewAction;
                }
            }
        });
    }

    /// <summary>
    /// Exports via UModel after ensuring
    /// </summary>
    public void EnsureUModelAndExport()
    {
        if (CurrentLoadedExport == null) return;
        var savewarning = CurrentLoadedExport.FileRef.IsModified ? MessageBoxResult.None : MessageBoxResult.OK;

        // show if we have not shown before
        if (savewarning == MessageBoxResult.None)
        {
            savewarning = Xceed.Wpf.Toolkit.MessageBox.Show(null,
                                                            "Exporting a model via UModel requires this package to be saved. Confirm it's OK to save this package before UModel processes exporting from this file.",
                                                            "Package save warning",
                                                            MessageBoxButton.OKCancel,
                                                            MessageBoxImage.Exclamation);
        }
        if (savewarning == MessageBoxResult.OK)
        {
            CurrentLoadedExport.FileRef.Save();

            var bw = new BackgroundWorker();
            bw.DoWork += EnsureUModel_BackgroundThread;
            bw.RunWorkerCompleted += (_, b) =>
            {
                if (b.Result is string message)
                {
                    BusyText = "Error downloading umodel";
                    MessageBox.Show($"An error occurred fetching umodel. Please comes to the ME3Tweaks Discord for assistance.\n\n{message}", "Error fetching umodel");
                }
                else if (b.Result == null)
                {
                    UModelHelper.ExportViaUModel(Window.GetWindow(this), CurrentLoadedExport);
                }

                IsBusy = false;
            };
            bw.RunWorkerAsync();
        }
    }

    private void CameraPropsMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock t)
        {
            var text = t.Text.Substring(t.Text.IndexOf(':') + 1).Trim();
            Clipboard.SetText(text);
        }
    }

    public void EnsureUModel_BackgroundThread(object sender, DoWorkEventArgs args)
    {
        // Pass error message back
        args.Result = UModelHelper.EnsureUModel(
            () => IsBusy = true,
            maxProgress => BusyProgressBarMax = maxProgress,
            currentProgress => BusyProgressBarValue = currentProgress,
            busyText => BusyText = busyText
            );
    }

    private Mesh<WorldVertex> GetMeshFromModelSubcomponents(Model model, List<ModelPreviewSection> sections)
    {
        var vertexList = new List<WorldVertex>();
        var triangles = new List<Triangle>();

        foreach (var vertex in model.VertexBuffer)
        {
            // We don't know the normal vectors yet
            vertexList.Add(new WorldVertex(new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), Vector4.Zero, new Vector2(vertex.TexCoord.X, vertex.TexCoord.Y)));
        }
        Span<WorldVertex> vertsSpan = CollectionsMarshal.AsSpan(vertexList);

        foreach (var mcExp in model.Export.FileRef.Exports.Where(x => x.ClassName == "ModelComponent" && !x.IsDefaultObject))
        {
            var mc = ObjectBinary.From<ModelComponent>(mcExp);
            if (mc.Model == model.Self)
            {
                foreach (var modelElement in mc.Elements)
                {
                    foreach (var node in modelElement.Nodes)
                    {
                        var matchingNode = model.Nodes[node];
                        var surface = model.Surfs[matchingNode.iSurf];
                        sections.Add(new ModelPreviewSection(model.Export.FileRef.GetEntry(surface.Material)?.InstancedFullPath ?? "", (uint)triangles.Count * 3, ((uint)matchingNode.NumVertices - 2) * 3));

                        for (uint i = 2; i < matchingNode.NumVertices; i++)
                        {
                            triangles.Add(new Triangle((uint)matchingNode.iVertexIndex, (uint)matchingNode.iVertexIndex + i - 1, (uint)matchingNode.iVertexIndex + i));
                        }
                        // Overwrite the normal vectors of the included vertices now that we know them
                        Vector3 normal = model.Vectors[model.Surfs[matchingNode.iSurf].vNormal];
                        for (int i = 0; i < matchingNode.NumVertices; i++)
                        {
                            vertsSpan[matchingNode.iVertexIndex + i].Normal = new Vector4(-normal.X, normal.Z, normal.Y, 1);
                        }
                    }
                }
            }
        }

        return new Mesh<WorldVertex>(SceneViewer.Context.Device, triangles, vertexList);
    }

    private Mesh<WorldVertex> GetMeshFromModelComponent(ModelComponent mc)
    {
        var parentModel = ObjectBinary.From<Model>(mc.Export.FileRef.GetUExport(mc.Model));
        var vertices = new List<WorldVertex>();

        foreach (var point in parentModel.Points)
        {
            vertices.Add(new WorldVertex(new Vector3(point.X, point.Y, point.Z), Vector4.Zero, Vector2.Zero));
        }

        var triangles = new List<Triangle>();

        foreach (var modelElement in mc.Elements)
        {
            foreach (var node in modelElement.Nodes)
            {
                var matchingNode = parentModel.Nodes[node];
                //var surface = parentModel.Surfs[matchingNode.iSurf];
                //var nodeVertices = new List<LegendaryExplorerCore.SharpDX.Vector3>(matchingNode.NumVertices);

                var vert0 = parentModel.Verts[matchingNode.iVertPool];

                for (uint i = 2; i < matchingNode.NumVertices; i++)
                {
                    var tri = new Triangle((uint)vert0.pVertex, (uint)parentModel.Verts[matchingNode.iVertPool + i - 1].pVertex, (uint)parentModel.Verts[matchingNode.iVertPool + i].pVertex);
                    triangles.Add(tri); // 0 is the base point. The rest of the triangles share this point
                }
            }
        }

        return new Mesh<WorldVertex>(SceneViewer.Context.Device, triangles, vertices);
    }

    private void MeshRenderer_Unloaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("MESHRENDERER UNLOADED");
        if (Parent is TabItem { Parent: TabControl tc })
        {
            tc.SelectionChanged -= MeshRendererWPF_HostingTabSelectionChanged;
        }
        MeshContext.UpdateScene -= SceneContext_UpdateScene;
        MeshContext.RenderScene -= SceneContext_RenderScene;
        ControlIsLoaded = false;
    }

    private void MeshRenderer_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ControlIsLoaded)
        {
            Debug.WriteLine("MESHRENDERER ONLOADED");
            if (Parent is TabItem { Parent: TabControl tc })
            {
                tc.SelectionChanged += MeshRendererWPF_HostingTabSelectionChanged;
            }
            ControlIsLoaded = true;
            MeshContext.UpdateScene += SceneContext_UpdateScene;
            MeshContext.RenderScene += SceneContext_RenderScene;
        }
    }

    private void SceneContext_UpdateScene(object sender, float timeStep)
    {
        if (ControlIsLoaded && Rotating)
        {
            MeshContext.Camera.Yaw += 0.3f * timeStep;
            if (MeshContext.Camera.Yaw > 6.28) //It's in radians 
                MeshContext.Camera.Yaw -= 6.28f; // Subtract so we don't overflow if this is open too long
        }

        Matrix4x4.Invert(MeshContext.Camera.ViewMatrix, out Matrix4x4 viewMatrix);
        Vector3 eyePosition = viewMatrix.Translation;

        if (UseDegrees)
        {
            CameraPitch = MathUtil.RadiansToDegrees(MeshContext.Camera.Pitch);
            CameraYaw = MathUtil.RadiansToDegrees(MeshContext.Camera.Yaw);
        }
        else if (UseRadians)
        {
            CameraPitch = MeshContext.Camera.Pitch;
            CameraYaw = MeshContext.Camera.Yaw;
        }
        else if (UseUnreal)
        {
            CameraPitch = MeshContext.Camera.Pitch.RadiansToUnrealRotationUnits();
            CameraYaw = MeshContext.Camera.Yaw.RadiansToUnrealRotationUnits();
        }

        CameraX = eyePosition.X;
        CameraY = eyePosition.Z; // Z and Y are switched to put the UI coordinates into Unreal Z-up coords
        CameraZ = eyePosition.Y;

        CameraFOV = MathUtil.RadiansToDegrees(MeshContext.Camera.FOV);
        CameraZNear = MeshContext.Camera.ZNear;
        CameraZFar = MeshContext.Camera.ZFar;
    }

    private void BackgroundColorPicker_Changed(object sender, RoutedPropertyChangedEventArgs<System.Windows.Media.Color?> e)
    {
        if (!startingUp && e.NewValue.HasValue)
        {
            var s = e.NewValue.Value.ToString();
            Settings.Meshplorer_BackgroundColor = s;
            Settings.Save();
            MeshContext.BackgroundColor = System.Windows.Media.Color.FromRgb(e.NewValue.Value.R, e.NewValue.Value.G, e.NewValue.Value.B);
        }
    }

    public override void UnloadExport()
    {
        IsBrush = false;
        IsSkeletalMesh = false;
        IsStaticMesh = false;
        IsModel = false;
        CurrentLoadedExport = null;
        STMCollisionMesh?.Dispose();
        STMCollisionMesh = null;
        GameShaderMeshPreview?.Dispose();
        GameShaderMeshPreview = null;
        LEXShaderMeshPreview?.Dispose();
        LEXShaderMeshPreview = null;
        SceneViewer?.Context?.EmptyCaches();
    }

    public override void PopOut()
    {
        if (CurrentLoadedExport != null)
        {
            var elhw = new ExportLoaderHostedWindow(new MeshRenderer(), CurrentLoadedExport)
            {
                Title = $"Mesh Renderer - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath} - {CurrentLoadedExport.FileRef.FilePath}"
            };
            elhw.Show();
        }
    }

    public override void Dispose()
    {
        if (Parent is TabItem { Parent: TabControl tc })
        {
            tc.SelectionChanged -= MeshRendererWPF_HostingTabSelectionChanged;
        }
        UnloadExport();
        if (SceneViewer is { Context: not null })
        {
            MeshContext.RenderScene -= SceneContext_RenderScene;
            MeshContext.UpdateScene -= SceneContext_UpdateScene;
        }
        CurrentLoadedExport = null;
        SceneViewer = null;
    }

    private void MeshRendererWPF_HostingTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Parent is TabItem ti)
        {
            if (e.AddedItems.Contains(ti))
            {
                SceneViewer?.SetShouldRender(true);
            }
            else if (e.RemovedItems.Contains(ti))
            {
                SceneViewer?.SetShouldRender(false);
            }
        }
    }

    private void MeshRendererWPF_OnKeyUp(object sender, KeyEventArgs e)
    {
        SceneViewer?.OnKeyUp(sender, e);
    }

    private void MeshRendererWPF_OnKeyDown(object sender, KeyEventArgs e)
    {
        SceneViewer?.OnKeyDown(sender, e);
    }
}
