using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.UnrealExtensions.Classes;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorer.UserControls.ExportLoaderControls.TextureViewer;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
//using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Color = LegendaryExplorerCore.SharpDX.Color;
using SkeletalMesh = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UserControls.ExportLoaderControls
{
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
                if (SetProperty(ref _renderGameShader, value))
                {
                    OnPropertyChanged(nameof(ShowLiveMaterialEditor));
                    if (_renderGameShader)
                    {
                        //require reload so that the game shader feature is (relatively) costless when not used
                        if (GameShaderPreview is null)
                        {
                            LoadExport(CurrentLoadedExport);
                        }
                        RenderSolid = false;
                    }
                    else
                    {
                        // Turning the game shader off switches back to the standard textured preview.
                        // Enabling the game shader turns RenderSolid off above, so this must be
                        // restored for both Meshplorer and Morph Editor.
                        RenderSolid = true;
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
                    if (morphViewportHit != null) ClearMorphViewportSelection();
                    _animationPoseDirty = true;
                    if (morphFaceFxPoseActive) ApplyMorphFaceFxPose();
                }
            }
        }
        public ObservableCollectionExtended<string> LODPicker { get; } = new();

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
                    this.MeshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.AlphaAsBlack;
                }
                else
                {
                    this.MeshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.AlphaAsBlack;
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
                    this.MeshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.EnableRedChannel;
                }
                else
                {
                    this.MeshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.EnableRedChannel;
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
                    this.MeshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.EnableGreenChannel;
                }
                else
                {
                    this.MeshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.EnableGreenChannel;
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
                    this.MeshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.EnableBlueChannel;
                }
                else
                {
                    this.MeshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.EnableBlueChannel;
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
                    this.MeshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.EnableAlphaChannel;
                }
                else
                {
                    this.MeshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.EnableAlphaChannel;
                }
            }
        }

        private System.Windows.Media.Color _backgroundColor;
        public System.Windows.Media.Color BackgroundColor
        {
            get => _backgroundColor;
            set
            {
                if (SetProperty(ref _backgroundColor, value))
                {
                    MeshContext.BackgroundColor = value;
                    if (!startingUp)
                    {
                        Settings.Meshplorer_BackgroundColor = value.ToString();
                        Settings.Save();
                    }
                }
            }
        }

        private static System.Windows.Media.Color DarkThemeDefaultBackgroundColor => ThemeManager.DarkCanvasMediaColor;
        private static readonly System.Windows.Media.Color LightThemeDefaultBackgroundColor = System.Windows.Media.Color.FromRgb(128, 128, 128);

        /// <summary>
        /// Returns the default background color for the current theme.
        /// Dark mode uses the same dark background as the Sequence Editor.
        /// </summary>
        public static System.Windows.Media.Color GetThemeDefaultBackgroundColor()
        {
            return Settings.Global_DarkMode_Enabled
                ? DarkThemeDefaultBackgroundColor
                : LightThemeDefaultBackgroundColor;
        }

        private static bool IsThemeDefaultBackgroundColor(System.Windows.Media.Color color)
        {
            return ThemeManager.IsDarkCanvasColor(color) || color == LightThemeDefaultBackgroundColor;
        }
        #endregion

        private ModelPreview<WorldVertex> LEXPreview;
        private ModelPreview<LEVertex> GameShaderPreview;

        public ObservableCollectionExtended<LiveMaterialEditorMaterial> LiveMaterials { get; } = [];

        /// <summary>
        /// Optional host-specific persistence callbacks for the live material editor. When supplied,
        /// the renderer keeps responsibility for the live preview while the host owns serialization
        /// and any reference changes outside the preview mesh package.
        /// </summary>
        public Func<LiveMaterialEditorMaterial, bool> SaveLiveMaterialToCurrentOverride { get; set; }
        public Func<LiveMaterialEditorMaterial, bool> SaveLiveMaterialAsNewOverride { get; set; }
        private string _liveMaterialSaveCurrentLabel = "Save to current";
        public string LiveMaterialSaveCurrentLabel
        {
            get => _liveMaterialSaveCurrentLabel;
            set => SetProperty(ref _liveMaterialSaveCurrentLabel, value);
        }
        private string _liveMaterialSaveAsNewLabel = "Save as new...";
        public string LiveMaterialSaveAsNewLabel
        {
            get => _liveMaterialSaveAsNewLabel;
            set => SetProperty(ref _liveMaterialSaveAsNewLabel, value);
        }
        private string _liveMaterialSaveHelpText =
            "Only local MaterialInstanceConstants can be overwritten. Use Save as new for base or imported materials.";
        public string LiveMaterialSaveHelpText
        {
            get => _liveMaterialSaveHelpText;
            set => SetProperty(ref _liveMaterialSaveHelpText, value);
        }

        public bool CanSaveSelectedLiveMaterialToCurrent => SelectedLiveMaterial is not null
            && (SaveLiveMaterialToCurrentOverride is not null
                ? SelectedLiveMaterial.SourceEntry is ExportEntry source && source.IsA("MaterialInstanceConstant")
                : SelectedLiveMaterial.CanSaveToCurrent);
        public bool CanSaveSelectedLiveMaterialAsNew => SelectedLiveMaterial is not null
            && (SaveLiveMaterialAsNewOverride is not null
                ? SelectedLiveMaterial.SourceEntry is not null
                : SelectedLiveMaterial.CanCreateNew);

        private bool _showLiveMaterialTintRandomizationControl;
        public bool ShowLiveMaterialTintRandomizationControl
        {
            get => _showLiveMaterialTintRandomizationControl;
            set
            {
                if (SetProperty(ref _showLiveMaterialTintRandomizationControl, value))
                {
                    OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialTints));
                }
            }
        }

        private bool _showLiveMaterialRandomizationControls;
        public bool ShowLiveMaterialRandomizationControls
        {
            get => _showLiveMaterialRandomizationControls;
            set
            {
                if (SetProperty(ref _showLiveMaterialRandomizationControls, value))
                {
                    OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialScalars));
                    OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialVectors));
                }
            }
        }

        private Func<LiveMaterialEditorMaterial, Task> _randomizeLiveMaterialScalarsOverride;
        public Func<LiveMaterialEditorMaterial, Task> RandomizeLiveMaterialScalarsOverride
        {
            get => _randomizeLiveMaterialScalarsOverride;
            set
            {
                if (SetProperty(ref _randomizeLiveMaterialScalarsOverride, value))
                {
                    OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialScalars));
                }
            }
        }

        private Func<LiveMaterialEditorMaterial, Task> _randomizeLiveMaterialVectorsOverride;
        public Func<LiveMaterialEditorMaterial, Task> RandomizeLiveMaterialVectorsOverride
        {
            get => _randomizeLiveMaterialVectorsOverride;
            set
            {
                if (SetProperty(ref _randomizeLiveMaterialVectorsOverride, value))
                {
                    OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialVectors));
                }
            }
        }

        public bool CanRandomizeSelectedLiveMaterialScalars => ShowLiveMaterialRandomizationControls
            && RandomizeLiveMaterialScalarsOverride is not null
            && SelectedLiveMaterial?.ScalarParameters.Count > 0;
        public bool CanRandomizeSelectedLiveMaterialVectors => ShowLiveMaterialRandomizationControls
            && RandomizeLiveMaterialVectorsOverride is not null
            && SelectedLiveMaterial?.VectorParameters.Count > 0;
        public bool CanRandomizeSelectedLiveMaterialTints => ShowLiveMaterialTintRandomizationControl
            && SelectedLiveMaterial?.VectorParameters.Any(IsTintParameter) == true;

        private LiveMaterialEditorMaterial _selectedLiveMaterial;
        public LiveMaterialEditorMaterial SelectedLiveMaterial
        {
            get => _selectedLiveMaterial;
            set
            {
                if (SetProperty(ref _selectedLiveMaterial, value))
                {
                    SelectedLiveScalarParameter = value?.ScalarParameters.FirstOrDefault();
                    SelectedLiveVectorParameter = GetPreferredVectorParameter(value);
                    OnPropertyChanged(nameof(CanSaveSelectedLiveMaterialToCurrent));
                    OnPropertyChanged(nameof(CanSaveSelectedLiveMaterialAsNew));
                    OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialScalars));
                    OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialVectors));
                    OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialTints));
                }
            }
        }

        private LiveScalarMaterialParameter _selectedLiveScalarParameter;
        public LiveScalarMaterialParameter SelectedLiveScalarParameter
        {
            get => _selectedLiveScalarParameter;
            set => SetProperty(ref _selectedLiveScalarParameter, value);
        }

        private LiveVectorMaterialParameter _selectedLiveVectorParameter;
        public LiveVectorMaterialParameter SelectedLiveVectorParameter
        {
            get => _selectedLiveVectorParameter;
            set => SetProperty(ref _selectedLiveVectorParameter, value);
        }

        public bool ShowLiveMaterialEditor => !IsMorphEditorMode && RenderGameShader && LiveMaterials.Count > 0;

        /// <summary>
        /// True for the dedicated BioMorphFace editor. Its in-game shader is enabled by default.
        /// </summary>
        public bool IsMorphEditorMode { get; }

        private bool _isMorphEditorReadOnly;
        /// <summary>
        /// Uses the BioMorphFace rendering pipeline without exposing controls that mutate the
        /// loaded morph. Intended for read-only preview hosts such as Asset Database.
        /// </summary>
        public bool IsMorphEditorReadOnly
        {
            get => _isMorphEditorReadOnly;
            set
            {
                if (SetProperty(ref _isMorphEditorReadOnly, value))
                {
                    OnPropertyChanged(nameof(ShowMorphEditorPanel));
                    OnPropertyChanged(nameof(CanEditMorph));
                    OnPropertyChanged(nameof(CanOverrideMorph));
                }
            }
        }

        public bool ShowMorphEditorPanel => IsMorphEditorMode && !IsMorphEditorReadOnly;

        public bool CanEditMorph => HasMorphEditorData && !IsMorphEditorReadOnly;

        private string PendingLiveMaterialSelectionName;
        private System.Windows.Point? MaterialPickMouseDownPosition;

        /// <summary>
        /// Value is true after _Loaded is called. False after _Unloaded (which if in tab control, is called when different tab is selected)
        /// </summary>
        private bool ControlIsLoaded;
        private WorldMesh STMCollisionMesh;
        private SharpDX.Direct3D11.Buffer SkeletonVertexBuffer;
        private int SkeletonVertexCount;
        private Vector3[] SkeletonBonePositions; // Renderer-space positions for all bones (for label projection)
        private Action ViewportLoadAction = null;

        private void SceneContext_RenderScene(object sender, EventArgs e)
        {
            if (CurrentLOD < 0) { CurrentLOD = 0; }
            foreach (RenderPass renderPass in Enum.GetValues<RenderPass>())
            {
                if (RenderSolid && LEXPreview is not null && CurrentLOD < LEXPreview.LODs.Count)
                {
                    MeshContext.Wireframe = false;
                    LEXPreview.Render(renderPass, MeshContext, CurrentLOD, Matrix4x4.Identity);
                }
                if (RenderGameShader && GameShaderPreview != null && CurrentLOD < GameShaderPreview.LODs.Count)
                {
                    MeshContext.Wireframe = false;
                    GameShaderPreview.Render(renderPass, MeshContext, CurrentLOD, Matrix4x4.Identity);
                }
                RenderMorphHairPreview(renderPass);
            }
            RenderMorphRegions();
            if (RenderWireframe && LEXPreview is not null && CurrentLOD < LEXPreview.LODs.Count)
            {
                MeshContext.Wireframe = true;
                var viewConstants = new MeshRenderContext.WorldConstants(Matrix4x4.Transpose(MeshContext.Camera.ProjectionMatrix), Matrix4x4.Transpose(MeshContext.Camera.ViewMatrix), Matrix4x4.Identity, MeshContext.CurrentTextureViewFlags);
                MeshContext.DefaultEffect.PrepDraw(SceneViewer.Context.ImmediateContext, MeshContext.AlphaBlendState);
                MeshContext.DefaultEffect.RenderObject(SceneViewer.Context.ImmediateContext, viewConstants, LEXPreview.LODs[CurrentLOD].Mesh, [null]);
            }
            RenderMorphHairWireframe();
            if (IsStaticMesh && ShowCollisionMesh && STMCollisionMesh != null)
            {
                MeshContext.Wireframe = true;
                var viewConstants = new MeshRenderContext.WorldConstants(Matrix4x4.Transpose(MeshContext.Camera.ProjectionMatrix), Matrix4x4.Transpose(MeshContext.Camera.ViewMatrix), Matrix4x4.Identity, MeshContext.CurrentTextureViewFlags);
                MeshContext.DefaultEffect.PrepDraw(SceneViewer.Context.ImmediateContext, MeshContext.AlphaBlendState);
                MeshContext.DefaultEffect.RenderObject(SceneViewer.Context.ImmediateContext, viewConstants, STMCollisionMesh, [null]);
            }
            if (IsSkeletalMesh && ShowSkeleton && SkeletonVertexBuffer != null && SkeletonVertexCount > 0)
            {
                RenderSkeleton();
            }
        }

        private void RenderSkeleton()
        {
            var ctx = SceneViewer.Context.ImmediateContext;

            // Clear depth buffer so skeleton renders on top of the mesh
            ctx.ClearDepthStencilView(MeshContext.DepthBufferView, SharpDX.Direct3D11.DepthStencilClearFlags.Depth, 1.0f, 0);

            MeshContext.Wireframe = false;
            var viewConstants = new MeshRenderContext.WorldConstants(Matrix4x4.Transpose(MeshContext.Camera.ProjectionMatrix), Matrix4x4.Transpose(MeshContext.Camera.ViewMatrix), Matrix4x4.Identity, MeshContext.CurrentTextureViewFlags);
            MeshContext.DefaultEffect.PrepDraw(ctx, MeshContext.AlphaBlendState);
            ctx.UpdateSubresource(ref viewConstants, MeshContext.DefaultEffect.ConstantBuffer);

            // Bind skeleton vertex buffer and switch to line topology
            ctx.InputAssembler.SetVertexBuffers(0, new SharpDX.Direct3D11.VertexBufferBinding(SkeletonVertexBuffer, WorldVertex.Stride, 0));
            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.LineList;
            ctx.PixelShader.SetShaderResource(0, MeshContext.WhiteTexView);

            // Draw skeleton lines (non-indexed)
            ctx.Draw(SkeletonVertexCount, 0);

            // Restore triangle topology
            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;

            // Project bone positions to screen space for index labels
            if (SkeletonBonePositions != null)
            {
                var viewProj = MeshContext.Camera.ViewMatrix * MeshContext.Camera.ProjectionMatrix;
                float halfW = MeshContext.Width * 0.5f;
                float halfH = MeshContext.Height * 0.5f;

                for (int i = 0; i < SkeletonBonePositions.Length; i++)
                {
                    var clip = Vector4.Transform(new Vector4(SkeletonBonePositions[i], 1.0f), viewProj);
                    if (clip.W <= 0) continue; // Behind camera

                    float ndcX = clip.X / clip.W;
                    float ndcY = clip.Y / clip.W;

                    // NDC to screen: X maps [-1,1] -> [0,Width], Y maps [1,-1] -> [0,Height]
                    float screenX = (ndcX + 1.0f) * halfW;
                    float screenY = (1.0f - ndcY) * halfH;

                    MeshContext.ScreenLabels.Add(new ScreenLabel(screenX, screenY, i.ToString()));
                }
            }
        }

        private void CenterView()
        {
            if (CurrentLOD >= 0)
            {
                if (GameShaderPreview != null && GameShaderPreview.LODs.Count > 0)
                {
                    var m = GameShaderPreview.LODs[CurrentLOD].Mesh;
                    MeshContext.Camera.Position = m.AABBCenter;
                    MeshContext.Camera.Pitch = -MathF.PI / 7.0f;
                    MeshContext.Camera.Yaw = -MathF.PI / 2.0f;
                    if (MeshContext.Camera.FirstPerson)
                    {
                        MeshContext.Camera.Position -= MeshContext.Camera.CameraForward * MeshContext.Camera.FocusDepth;
                    }
                }
                else if (LEXPreview != null && LEXPreview.LODs.Count > 0)
                {
                    var m = LEXPreview.LODs[CurrentLOD].Mesh;
                    MeshContext.Camera.Position = m.AABBCenter;
                    MeshContext.Camera.Pitch = -MathF.PI / 7.0f;
                    MeshContext.Camera.Yaw = -MathF.PI / 2.0f;
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

        private bool _showSkeleton;
        public bool ShowSkeleton
        {
            get => _showSkeleton;
            set
            {
                if (SetProperty(ref _showSkeleton, value))
                {
                    _animationPoseDirty = true;
                    if (morphFaceFxPoseActive) ApplyMorphFaceFxPose();
                }
            }
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
        public MeshRenderer() : this(false)
        {
        }

        protected MeshRenderer(bool isMorphEditorMode) : base(isMorphEditorMode ? "Morph Editor" : "Mesh Renderer")
        {
            startingUp = true;
            IsMorphEditorMode = isMorphEditorMode;
            _renderGameShader = isMorphEditorMode;
            _renderSolid = !isMorphEditorMode;
            DataContext = this;
            LoadCommands();
            InitializeComponent();
            MeshContext = new MeshRenderContext
            {
                // Meshplorer and Morph Editor need Unreal color textures decoded to linear and
                // their lit output encoded for display. Shared scene tools remain opted out.
                UseSrgbColorManagement = true
            };
            if (IsMorphEditorMode)
            {
                MorphPreviewSettings.Apply(MeshContext);
            }
            if (ColorConverter.ConvertFromString(Settings.Meshplorer_BackgroundColor) is System.Windows.Media.Color color)
            {
                BackgroundColor = IsThemeDefaultBackgroundColor(color)
                    ? GetThemeDefaultBackgroundColor()
                    : color;
            }
            else
            {
                BackgroundColor = GetThemeDefaultBackgroundColor();
            }
            SceneViewer.Context = MeshContext;
            SceneViewer.AddHandler(Mouse.PreviewMouseDownEvent,
                new MouseButtonEventHandler(SceneViewer_PreviewMouseDownForMaterialPicking), true);
            SceneViewer.AddHandler(Mouse.PreviewMouseUpEvent,
                new MouseButtonEventHandler(SceneViewer_PreviewMouseUpForMaterialPicking), true);
            SceneViewer.Loaded += (sender, args) =>
            {
                TryRunViewportLoadAction();
            };

            ThemeManager.ThemeChanged += OnThemeChanged;

            startingUp = false;
        }

        private void OnThemeChanged(object sender, bool isDarkMode)
        {
            BackgroundColor = GetThemeDefaultBackgroundColor();
        }

        public ICommand UModelExportCommand { get; set; }
        public ICommand GltfExportCommand { get; set; }

        private void LoadCommands()
        {
            UModelExportCommand = new GenericCommand(EnsureUModelAndExport, CanExportViaUModel);
            GltfExportCommand = new GenericCommand(ExportToGltf, CanExportViaUModel);
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

        private readonly List<string> alreadyLoadedImportMaterials = new();

        /// <summary>
        /// Used for debugging by listing the used instances
        /// </summary>
        //public ObservableCollectionExtended<PreviewTextureCache.PreviewTextureEntry> SceneViewerProperty => SceneViewer?.Context?.TextureCache?.AssetCache;

        public override void LoadExport(ExportEntry exportEntry)
        {
            PendingLiveMaterialSelectionName ??= SelectedLiveMaterial?.SourceEntry?.ObjectName.Instanced;
            UnloadExport();
            if (exportEntry == null)
                return; // Can reload due to static mesh component looking for static mesh

            if (_previewAnimation != null && _previewAnimation.Export.Game != exportEntry.Game)
                SetPreviewAnimation(null);


            //SceneViewer.Context.BackgroundColor = new SharpDX.Color(128, 128, 128);
            alreadyLoadedImportMaterials.Clear();
            CurrentLoadedExport = exportEntry;
            CurrentLOD = 0;
            CanUseGameShaders = exportEntry.Game.IsMEGame();

            Func<PreloadedModelData> loadMesh = null;
            var assetCache = new PackageCache();

            if (IsMorphEditorMode && exportEntry.ClassName == "BioMorphFace")
            {
                IsSkeletalMesh = true;
                ShowSkeleton = false;
                if (!InitializeMorphEditor(exportEntry, assetCache))
                {
                    assetCache.Dispose();
                    return;
                }
                ExportEntry morphSource = exportEntry;
                ExportEntry morphBaseHead = MorphBaseHeadExport;
                ExportEntry morphHairMesh = MorphHairMeshExport;
                loadMesh = () => CreateMorphPreloadedModelData(assetCache, morphBaseHead, morphHairMesh);
            }
            else if (exportEntry.ClassName is "StaticMeshComponent")
            {
                var cache = new PackageCache();
                var mesh = CurrentLoadedExport.GetProperty<ObjectProperty>("StaticMesh")?.ResolveToExport(exportEntry.FileRef, cache);
                if (mesh != null)
                {
                    var mats = CurrentLoadedExport.GetProperty<ArrayProperty<ObjectProperty>>("Materials");
                    if (mats != null)
                    {
                        OverlayMaterials = mats.Select(x => x.Value != 0 ? x.ResolveToExport(CurrentLoadedExport.FileRef, cache) : null).Cast<IEntry>().ToList();
                    }
                }

                // Reload on the mesh.
                LoadExport(mesh);
                return;
            }

            if (loadMesh is not null)
            {
                // Morph editor mode already built the loader for m_oBaseHead. The class-name dispatch
                // below would not match BioMorphFace and would discard it.
            }
            else if (CurrentLoadedExport.ClassName is "StaticMesh" or "FracturedStaticMesh")
            {
                IsStaticMesh = true;
                loadMesh = () =>
                {
                    BusyText = "Fetching assets";
                    BusyProgressIndeterminate = true;
                    IsBusy = true;

                    var meshObject = ObjectBinary.From<StaticMesh>(CurrentLoadedExport);
                    List<IEntry> overlayMaterials = OverlayMaterials?.ToList()
                                                    ?? LiveMaterialSourceOverrides?.ToList();
                    if (overlayMaterials != null)
                    {
                        meshObject.SetMaterials(overlayMaterials, true);
                        OverlayMaterials = null;
                    }
                    var pmd = new PreloadedModelData
                    {
                        meshObject = meshObject,
                        sections = new List<ModelPreviewSection>(),
                        texturePreviewMaterials = new List<PreloadedTextureData>()
                    };
                    IMEPackage meshFile = meshObject.Export.FileRef;
                    if (meshFile.Game != MEGame.UDK)
                    {
                        int sectionIndex = 0;
                        foreach (StaticMeshElement section in meshObject.LODModels[CurrentLOD].Elements)
                        {
                            int matIndex = section.Material;
                            IEntry overrideMaterial = overlayMaterials?.ElementAtOrDefault(sectionIndex++);
                            if (overrideMaterial is ExportEntry overrideExport)
                            {
                                AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials, overrideExport, assetCache);
                            }
                            else if (meshFile.IsUExport(matIndex))
                            {
                                ExportEntry entry = meshFile.GetUExport(matIndex);
                                AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials, entry, assetCache);
                            }
                            else if (meshFile.IsImport(matIndex))
                            {
                                var extMaterialExport = EntryImporter.ResolveImport(meshFile.GetImport(matIndex), assetCache);
                                if (extMaterialExport != null)
                                {
                                    AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials, extMaterialExport, assetCache);
                                }
                                else
                                {
                                    Debug.WriteLine("Could not find import material from section.");
                                    Debug.WriteLine("Import material: " + meshFile.GetEntryString(matIndex));
                                }
                            }

                            string materialName = overrideMaterial?.ObjectName.Name ?? meshFile.getObjectName(matIndex);
                            pmd.sections.Add(new ModelPreviewSection(materialName, section.FirstIndex, section.NumTriangles));
                        }
                    }
                    return pmd;
                };
            }
            else if (CurrentLoadedExport.IsA("SkeletalMesh"))
            {
                IsSkeletalMesh = true;
                //var sm = new Unreal.Classes.SkeletalMesh(CurrentLoadedExport);
                loadMesh = () =>
                {
                    BusyText = "Fetching assets";
                    IsBusy = true;

                    var lodMatMaps = new List<int[]>();
                    if (CurrentLoadedExport.GetProperty<ArrayProperty<StructProperty>>("LODInfo", assetCache) is { } lodInfo)
                    {
                        foreach (var lod in lodInfo)
                        {
                            var matMapProp = lod.GetProp<ArrayProperty<IntProperty>>("LODMaterialMap");
                            if (matMapProp?.Count > 0)
                            {
                                lodMatMaps.Add([.. matMapProp.Select(x => x.Value)]);
                            }
                            else
                            {
                                lodMatMaps.Add([]);
                            }
                        }
                    }

                    var meshObject = ObjectBinary.From<SkeletalMesh>(CurrentLoadedExport);
                    var pmd = new PreloadedModelData
                    {
                        meshObject = meshObject,
                        sections = new List<ModelPreviewSection>(),
                        texturePreviewMaterials = new List<PreloadedTextureData>(),
                        lodMaterialMaps = lodMatMaps
                    };
                    IMEPackage package = meshObject.Export.FileRef;
                    if (package.Game != MEGame.UDK)
                    {
                        foreach (int material in meshObject.Materials)
                        {
                            if (package.TryGetUExport(material, out ExportEntry matExp))
                            {
                                AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials, matExp, assetCache);
                            }
                            else if (package.TryGetImport(material, out ImportEntry matImp) && alreadyLoadedImportMaterials.All(x => x != matImp.InstancedFullPath))
                            {
                                var extMaterialExport = EntryImporter.ResolveImport(matImp, assetCache);
                                //var extMaterialExport = ModelPreview.FindExternalAsset(matImp, pmd.texturePreviewMaterials.Select(x => x.Mip.Export).ToList(), cachedPackages);
                                if (extMaterialExport != null)
                                {
                                    AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials, extMaterialExport, assetCache);
                                    alreadyLoadedImportMaterials.Add(extMaterialExport.InstancedFullPath);
                                }
                                else
                                {
                                    Debug.WriteLine("Could not find import material from materials list.");
                                    Debug.WriteLine("Import material: " + package.GetEntryString(material));
                                }
                            }
                        }
                    }
                    return pmd;
                };
            }
            else if (CurrentLoadedExport.ClassName == "BrushComponent")
            {
                IsBrush = true;
                loadMesh = () =>
                {
                    var pmd = new PreloadedModelData
                    {
                        meshObject = CurrentLoadedExport.GetProperty<StructProperty>("BrushAggGeom"),
                        sections = new List<ModelPreviewSection>(),
                        texturePreviewMaterials = new List<PreloadedTextureData>(),
                    };
                    return pmd;
                };
            }
            else if (CurrentLoadedExport.ClassName == "ModelComponent")
            {
                IsModel = true;
                BusyText = "Fetching assets";
                BusyProgressIndeterminate = true;
                IsBusy = true;

                loadMesh = () =>
                {
                    var modelComp = ObjectBinary.From<ModelComponent>(CurrentLoadedExport);
                    var pmd = new PreloadedModelData
                    {
                        meshObject = modelComp,
                        sections = new List<ModelPreviewSection>(),
                        texturePreviewMaterials = new List<PreloadedTextureData>(),
                    };

                    foreach (var element in modelComp.Elements)
                    {
                        if (CurrentLoadedExport != null)
                        {
                            if (CurrentLoadedExport.FileRef.TryGetUExport(element.Material, out var matExp))
                            {
                                AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials, matExp, assetCache);
                                pmd.sections.Add(new ModelPreviewSection(matExp.ObjectName, 0, 3)); //???
                            }
                            else if (CurrentLoadedExport.FileRef.TryGetImport(element.Material, out var matImp))
                            {
                                var extMaterialExport = EntryImporter.ResolveImport(matImp, assetCache);
                                //var extMaterialExport = ModelPreview.FindExternalAsset(matImp, pmd.texturePreviewMaterials.Select(x => x.Mip.Export).ToList(), cachedPackages);
                                if (extMaterialExport != null)
                                {
                                    AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials, extMaterialExport, assetCache);
                                }
                                else
                                {
                                    Debug.WriteLine("Could not find import material from section.");
                                    Debug.WriteLine("Import material: " + CurrentLoadedExport.FileRef.GetEntryString(element.Material));
                                }
                            }
                        }
                    }

                    return pmd;
                };
            }
            else if (CurrentLoadedExport.ClassName == "Model")
            {
                IsModel = true;
                loadMesh = () =>
                {
                    BusyText = "Fetching assets";
                    BusyProgressIndeterminate = true;
                    IsBusy = true;
                    var modelComp = ObjectBinary.From<Model>(CurrentLoadedExport);
                    var pmd = new PreloadedModelData
                    {
                        meshObject = modelComp,
                        sections = new List<ModelPreviewSection>(),
                        texturePreviewMaterials = new List<PreloadedTextureData>(),
                    };
                    foreach (var mcExp in modelComp.Export.FileRef.Exports.Where(x =>
                        x.ClassName == "ModelComponent" && !x.IsDefaultObject))
                    {
                        var mc = ObjectBinary.From<ModelComponent>(mcExp);
                        if (mc.Model == modelComp.Self)
                        {
                            foreach (var element in mc.Elements)
                            {
                                if (CurrentLoadedExport == null) return pmd;
                                if (CurrentLoadedExport.FileRef.IsUExport(element.Material))
                                {
                                    ExportEntry entry = CurrentLoadedExport.FileRef.GetUExport(element.Material);
                                    AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials, entry, assetCache);
                                }
                                else if (CurrentLoadedExport.FileRef.TryGetImport(element.Material, out var matImp) &&
                                         alreadyLoadedImportMaterials.All(x => x != matImp.InstancedFullPath))
                                {
                                    var extMaterialExport = EntryImporter.ResolveImport(matImp, assetCache);
                                    if (extMaterialExport != null)
                                    {
                                        AddMaterialBackgroundThreadTextures(pmd.texturePreviewMaterials,
                                            extMaterialExport, assetCache);
                                        alreadyLoadedImportMaterials.Add(extMaterialExport.InstancedFullPath);
                                    }
                                    else
                                    {
                                        Debug.WriteLine("Could not find import material from FModelElement.");
                                        Debug.WriteLine("Import material: " +
                                                        CurrentLoadedExport.FileRef.GetEntryString(element.Material));
                                    }
                                }
                            }
                        }
                    }
                    return pmd;
                };
            }
            else
            {
                return;
            }

            

            ExportEntry requestedExport = exportEntry;
            Task.Run(loadMesh).ContinueWith(prevTask =>
            {
                PreloadedModelData result = prevTask.GetAwaiter().GetResult();
                if (CanUseGameShaders && RenderGameShader)
                {
                    BusyText = "Reading Shader Cache (~15s)";
                    RefShaderCacheReader.PopulateOffsets(Pcc.Game);
                }
                return result;
            }).ContinueWithOnUIThread(prevTask =>
            {
                IsBusy = false;
                if (!ReferenceEquals(CurrentLoadedExport, requestedExport))
                {
                    //in the time since the previous task was started, the export has been unloaded
                    assetCache.Dispose();
                    return;
                }
                if (prevTask.IsFaulted || prevTask.IsCanceled)
                {
                    Exception exception = prevTask.Exception?.GetBaseException();
                    assetCache.Dispose();
                    if (IsMorphEditorMode)
                    {
                        MorphEditorStatus = $"Could not render m_oBaseHead: {exception?.Message ?? "preview loading was canceled"}";
                        MorphTargetStatus = "Morph-target loading was not started.";
                    }
                    else if (exception is not null)
                    {
                        new ExceptionHandlerDialog(exception).Show();
                    }
                    return;
                }
                if (prevTask.Result is PreloadedModelData pmd)
                {
                    Action loadPreviewAction = () =>
                    {
                        string morphPreviewWarning = null;
                        string morphHairStatus = null;
                        bool morphTargetLoadStarted = false;
                        try
                        {
                            LEXPreview?.Dispose();
                            GameShaderPreview?.Dispose();
                            LEXPreview = null;
                            GameShaderPreview = null;
                            DisposeMorphHairPreview();
                            STMCollisionMesh?.Dispose();
                            STMCollisionMesh = null;
                            SkeletonVertexBuffer?.Dispose();
                            SkeletonVertexBuffer = null;
                            SkeletonVertexCount = 0;
                            SkeletonBonePositions = null;
                            switch (pmd.meshObject)
                            {
                                case StaticMesh statM:
                                    STMCollisionMesh = GetMeshFromAggGeom(statM.GetCollisionMeshProperty(Pcc));
                                    if (CanUseGameShaders && RenderGameShader) GameShaderPreview = new ModelPreview<LEVertex>(MeshContext.Device, statM, CurrentLOD, MeshContext.TextureCache, assetCache, pmd);
                                    LEXPreview = new ModelPreview<WorldVertex>(MeshContext.Device, statM, CurrentLOD, MeshContext.TextureCache, assetCache, pmd);
                                    MeshContext.Camera.FocusDepth = statM.Bounds.SphereRadius * 1.2f;
                                    break;
                                case SkeletalMesh skm:
                                    if (CanUseGameShaders && RenderGameShader) GameShaderPreview = new ModelPreview<LEVertex>(MeshContext.Device, skm, MeshContext.TextureCache, assetCache, pmd);
                                    // Keep both previews resident so Morph Editor can switch renderers without
                                    // reloading, and so both receive every live geometry update.
                                    LEXPreview = new ModelPreview<WorldVertex>(MeshContext.Device, skm, MeshContext.TextureCache, assetCache, pmd);
                                    MeshContext.Camera.FocusDepth = skm.Bounds.SphereRadius * 1.2f;
                                    CenterView();
                                    if (IsMorphEditorMode)
                                    {
                                        try
                                        {
                                            BuildSkeletonLineBuffer(skm);
                                        }
                                        catch (Exception exception)
                                        {
                                            morphPreviewWarning = $"Skeleton overlay unavailable: {exception.Message}";
                                        }
                                    }
                                    else
                                    {
                                        BuildSkeletonLineBuffer(skm);
                                        InitializeAnimationPreview(skm);
                                    }
                                    break;
                                case StructProperty structProp: //BrushComponent
                                    LEXPreview = new ModelPreview<WorldVertex>(MeshContext.Device, GetMeshFromAggGeom(structProp), MeshContext.TextureCache, assetCache, pmd);
                                    MeshContext.Camera.FocusDepth = LEXPreview.LODs[0].Mesh.AABBHalfSize.Length() * 1.2f;
                                    break;
                                case ModelComponent mc:
                                    LEXPreview = new ModelPreview<WorldVertex>(MeshContext.Device, GetMeshFromModelComponent(mc), MeshContext.TextureCache, assetCache, pmd);
                                    //SceneViewer.Context.Camera.FocusDepth = Preview.LODs[0].Mesh.AABBHalfSize.Length() * 1.2f;
                                    break;
                                case Model m:
                                    var sections = new List<ModelPreviewSection>();
                                    Mesh<WorldVertex> mesh = GetMeshFromModelSubcomponents(m, sections);
                                    pmd.sections = sections;
                                    if (mesh.Vertices.Any())
                                    {
                                        MeshContext.Camera.Position = mesh.Vertices[0].Position;
                                    }

                                    LEXPreview = new ModelPreview<WorldVertex>(MeshContext.Device, mesh, MeshContext.TextureCache, assetCache, pmd);
                                    //SceneViewer.Context.Camera.FocusDepth = Preview.LODs[0].Mesh.AABBHalfSize.Length() * 1.2f;
                                    break;
                            }
                            if (IsMorphEditorMode)
                            {
                                ClearLiveMaterialEditor();
                                morphHairStatus = LoadMorphHairPreview(
                                    pmd.additionalModels?.FirstOrDefault(), assetCache, pmd.additionalModelLoadError);
                                try
                                {
                                    UpdateMorphGeometryPreview();
                                }
                                catch (Exception exception)
                                {
                                    morphPreviewWarning = $"Final-skeleton skinning unavailable: {exception.Message}";
                                }
                                try
                                {
                                    ApplyMorphMaterialOverridePreview();
                                }
                                catch (Exception exception)
                                {
                                    morphPreviewWarning = $"Material overrides unavailable: {exception.Message}";
                                }
                                int previewLodCount = RenderGameShader
                                    ? GameShaderPreview?.LODs.Count ?? 0
                                    : LEXPreview?.LODs.Count ?? 0;
                                string previewMode = RenderGameShader ? "in-game shader" : "standard textured";
                                MorphEditorStatus = $"Rendered m_oBaseHead with {previewLodCount} {previewMode} LOD(s)."
                                                    + (string.IsNullOrWhiteSpace(morphHairStatus) ? string.Empty : $" {morphHairStatus}")
                                                    + (morphPreviewWarning is null ? string.Empty : $" {morphPreviewWarning}");
                                BeginMorphTargetCatalogLoad(requestedExport, MorphBaseHeadExport);
                                morphTargetLoadStarted = true;
                            }
                            else
                            {
                                PopulateLiveMaterialEditor();
                            }
                            LODPicker.ClearEx();
                            int lodCount = LEXPreview?.LODs.Count ?? GameShaderPreview?.LODs.Count ?? 0;
                            for (int i = 0; i < lodCount; i++)
                            {
                                LODPicker.Add($"LOD{i}");
                            }
                            CenterView();
                        }
                        catch (Exception exception)
                        {
                            LEXPreview?.Dispose();
                            LEXPreview = null;
                            DisposeMorphHairPreview();
                            if (IsMorphEditorMode && GameShaderPreview is not null)
                            {
                                MorphEditorStatus = $"Rendered m_oBaseHead, but editor setup was incomplete: {exception.Message}";
                                if (!morphTargetLoadStarted)
                                {
                                    BeginMorphTargetCatalogLoad(requestedExport, MorphBaseHeadExport);
                                }
                                CenterView();
                            }
                            else if (IsMorphEditorMode)
                            {
                                GameShaderPreview?.Dispose();
                                GameShaderPreview = null;
                                MorphEditorStatus = $"Could not render m_oBaseHead: {exception.Message}";
                                MorphTargetStatus = "Morph-target loading was not started.";
                            }
                            else
                            {
                                GameShaderPreview?.Dispose();
                                GameShaderPreview = null;
                                new ExceptionHandlerDialog(exception).Show();
                            }
                        }
                        finally
                        {
                            assetCache.Dispose();
                        }
                    };

                    LODPicker.ClearEx();
                    //clearing the LODPicker will set CurrentLOD to -1
                    //if it is -1, meshes will not render.
                    CurrentLOD = 0;

                    // We can't call graphics methods until the render control has been loaded by WPF - only then will it have initialized D3D.
                    if (this.MeshContext.IsReady)
                    {
                        loadPreviewAction.Invoke();
                    }
                    else
                    {
                        this.ViewportLoadAction = loadPreviewAction;
                    }
                }
            });
        }

        /// <summary>
        /// Material overrides for a mesh
        /// </summary>
        public List<IEntry> OverlayMaterials { get; set; }

        /// <summary>
        /// Optional source entries represented by <see cref="OverlayMaterials"/>. This is retained
        /// after the one-shot binary override is consumed so a host can edit materials from a
        /// different package than the preview mesh.
        /// </summary>
        public List<IEntry> LiveMaterialSourceOverrides { get; set; }

        public void ExportToGltf()
        {
            var prompt = new DropdownPromptDialog("Select how you want the materials exported.",
                    "Material Export", "Textures", ["Name only", "Diff and Normal Textures"], Window.GetWindow(this));
            prompt.ShowDialog();
            var materialSetting = GLTF.MaterialExportLevel.NameOnly;
            if (prompt.DialogResult == true)
            {
                if (prompt.Response != "Name only")
                {
                    materialSetting = GLTF.MaterialExportLevel.Basic;
                }
            }
            GltfHelper.ExportMeshToGltf(Window.GetWindow(this) as WPFBase, this, this.Pcc, CurrentLoadedExport, materialSetting);
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

        private WorldMesh GetMeshFromAggGeom(StructProperty aggGeom)
        {
            if (aggGeom?.GetProp<ArrayProperty<StructProperty>>("ConvexElems") is ArrayProperty<StructProperty> convexElems)
            {
                var vertices = new List<WorldVertex>();
                var triangles = new List<Triangle>();
                int vertTotal = 0;
                foreach (StructProperty convexElem in convexElems)
                {
                    var faceTriData = convexElem.GetProp<ArrayProperty<IntProperty>>("FaceTriData");
                    for (int i = 0; i < faceTriData.Count; i += 3)
                    {
                        triangles.Add(new Triangle((uint)(faceTriData[i].Value + vertTotal), (uint)(faceTriData[i + 1].Value + vertTotal), (uint)(faceTriData[i + 2].Value + vertTotal)));
                    }

                    var vertexData = convexElem.GetProp<ArrayProperty<StructProperty>>("VertexData");
                    foreach (StructProperty vertex in vertexData)
                    {
                        float x = vertex.GetProp<FloatProperty>("X").Value;
                        float y = vertex.GetProp<FloatProperty>("Y").Value;
                        float z = vertex.GetProp<FloatProperty>("Z").Value;
                        vertices.Add(new WorldVertex(new Vector3(-x, z, y), Vector3.Zero, Vector2.Zero));
                        ++vertTotal;
                    }
                }

                return new WorldMesh(SceneViewer.Context.Device, triangles, vertices);
            }

            return null;
        }

        private WorldMesh GetMeshFromModelSubcomponents(Model model, List<ModelPreviewSection> sections)
        {
            // LOL this will run terribly i'm sure
            var vertexList = new List<WorldVertex>();
            var triangles = new List<Triangle>();

            foreach (var vertex in model.VertexBuffer)
            {
                // We don't know the normal vectors yet
                vertexList.Add(new WorldVertex(new Vector3(-vertex.Position.X, vertex.Position.Z, vertex.Position.Y), Vector3.Zero, new Vector2(vertex.TexCoord.X, vertex.TexCoord.Y)));
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
                            sections.Add(new ModelPreviewSection(model.Export.FileRef.getObjectName(surface.Material), (uint)triangles.Count * 3, ((uint)matchingNode.NumVertices - 2) * 3));

                            for (uint i = 2; i < matchingNode.NumVertices; i++)
                            {
                                triangles.Add(new Triangle((uint)matchingNode.iVertexIndex, (uint)matchingNode.iVertexIndex + i - 1, (uint)matchingNode.iVertexIndex + i));
                            }
                            // Overwrite the normal vectors of the included vertices now that we know them
                            Vector3 normal = model.Vectors[model.Surfs[matchingNode.iSurf].vNormal];
                            for (int i = 0; i < matchingNode.NumVertices; i++)
                            {
                                vertsSpan[matchingNode.iVertexIndex + i].Normal = new Vector3(-normal.X, normal.Z, normal.Y);
                            }
                        }
                    }
                }
            }

            return new WorldMesh(SceneViewer.Context.Device, triangles, vertexList);
        }

        private WorldMesh GetMeshFromModelComponent(ModelComponent mc)
        {
            var parentModel = ObjectBinary.From<Model>(mc.Export.FileRef.GetUExport(mc.Model));
            var vertices = new List<WorldVertex>();

            foreach (var point in parentModel.Points)
            {
                vertices.Add(new WorldVertex(new Vector3(-point.X, point.Z, point.Y), Vector3.Zero, Vector2.Zero));
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

            return new WorldMesh(SceneViewer.Context.Device, triangles, vertices);
        }

        private void BuildSkeletonLineBuffer(SkeletalMesh skm, Vector3[] animatedPositions = null)
        {
            SkeletonVertexBuffer?.Dispose();
            SkeletonVertexBuffer = null;
            SkeletonVertexCount = 0;
            SkeletonBonePositions = null;

            MeshBone[] bones = skm.RefSkeleton;
            if (bones == null || bones.Length == 0) return;

            // Compute bind-pose world positions by walking the bone hierarchy
            // Same algorithm as AnimPlayer constructor
            var bindPose = new Matrix4x4[bones.Length];
            var worldPositions = new Vector3[bones.Length];

            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                var localTransform = Matrix4x4.CreateFromQuaternion(bone.Orientation)
                                   * Matrix4x4.CreateTranslation(bone.Position);

                if (bone.ParentIndex >= 0 && bone.ParentIndex < i)
                {
                    bindPose[i] = localTransform * bindPose[bone.ParentIndex];
                }
                else
                {
                    bindPose[i] = localTransform;
                }

                // Extract world position and convert Unreal (X,Y,Z) -> Renderer (-X, Z, Y)
                worldPositions[i] = new Vector3(-bindPose[i].M41, bindPose[i].M43, bindPose[i].M42);
            }

            worldPositions = animatedPositions ?? worldPositions;
            SkeletonBonePositions = worldPositions;

            // Build line vertex pairs: each bone draws a line to its parent
            var lineVertices = new List<WorldVertex>();
            var boneNormal = new Vector3(1, 1, 1); // Max brightness in shader lambert calculation

            for (int i = 1; i < bones.Length; i++)
            {
                int parentIdx = bones[i].ParentIndex;
                if (parentIdx >= 0 && parentIdx < i)
                {
                    lineVertices.Add(new WorldVertex(worldPositions[parentIdx], boneNormal, Vector2.Zero));
                    lineVertices.Add(new WorldVertex(worldPositions[i], boneNormal, Vector2.Zero));
                }
            }

            if (lineVertices.Count == 0) return;

            SkeletonVertexCount = lineVertices.Count;

            // Serialize vertices into a float array for the GPU buffer
            int floatsPerVertex = WorldVertex.Stride / 4;
            float[] vertexData = new float[floatsPerVertex * lineVertices.Count];
            Span<float> dataSpan = vertexData.AsSpan();
            for (int i = 0, fi = 0; i < lineVertices.Count; i++, fi += floatsPerVertex)
            {
                lineVertices[i].ToFloats(dataSpan[fi..]);
            }

            SkeletonVertexBuffer = SharpDX.Direct3D11.Buffer.Create(
                MeshContext.Device,
                SharpDX.Direct3D11.BindFlags.VertexBuffer,
                vertexData);
        }

        private static void AddMaterialBackgroundThreadTextures(List<PreloadedTextureData> texturePreviewMaterials, ExportEntry entry, PackageCache assetCache)
        {
            if (texturePreviewMaterials.Any(x => x.MaterialExport.InstancedFullPath == entry.InstancedFullPath))
                return; //already cached
            // Keep the material slot even when it has no Texture2D that can be preloaded. ModelPreview
            // derives section/material indices from this list, so omitting an otherwise valid material
            // makes the corresponding mesh sections disappear.
            texturePreviewMaterials.Add(new PreloadedTextureData
            {
                MaterialExport = entry
            });
            Debug.WriteLine("Loading material assets for " + entry.InstancedFullPath);
            foreach (var tex in MaterialInstanceConstant.GetTextures(entry, assetCache))
            {
                Debug.WriteLine("Preloading " + tex.InstancedFullPath);
                if (tex.ClassName.StartsWith("TextureRender"))
                {
                    //can't deal with renderers yet
                    continue;
                }
                if (tex is ImportEntry import)
                {
                    var extAsset = EntryImporter.ResolveImport(import, assetCache);
                    if (extAsset != null) //Apparently some assets are cubemaps, we don't want these.
                    {
                        texturePreviewMaterials.Add(new PreloadedTextureData
                        {
                            TextureExport = extAsset,
                            MaterialExport = entry
                        });
                    }
                }
                else
                {
                    texturePreviewMaterials.Add(new PreloadedTextureData
                    {
                        TextureExport = (ExportEntry)tex,
                        MaterialExport = entry
                    });
                }
            }
        }

        private void MeshRenderer_Unloaded(object sender, RoutedEventArgs e)
        {
            ResetMorphRegionCallouts();
            PauseMorphFaceFx();
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
            TryRunViewportLoadAction();
        }

        private void TryRunViewportLoadAction()
        {
            if (!MeshContext.IsReady || ViewportLoadAction is not { } action)
            {
                return;
            }
            ViewportLoadAction = null;
            action();
        }

        private void SceneContext_UpdateScene(object sender, float timeStep)
        {
            // A preview may finish loading in the narrow interval after WPF Loaded has fired but
            // before the Direct3D context reports ready. Polling the one-shot action here guarantees
            // it is installed on the first render frame instead of remaining queued forever.
            TryRunViewportLoadAction();

            UpdatePreviewAnimation(timeStep);
            UpdateMorphFaceFxPreview(timeStep);
            UpdateMorphRegionLabels();
            UpdateMorphRegionCallouts();

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
            UpdateMorphViewportMarker();
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

        private void PopulateLiveMaterialEditor()
        {
            LiveMaterials.ClearEx();
            SelectedLiveMaterial = null;

            if (GameShaderPreview is null || CurrentLoadedExport is null)
            {
                OnPropertyChanged(nameof(ShowLiveMaterialEditor));
                return;
            }

            List<IEntry> sourceMaterials = GetCurrentMeshMaterialEntries();
            foreach (MaterialRenderProxy proxy in GameShaderPreview.Materials.Values
                         .Select(material => material.Material)
                         .OfType<MaterialRenderProxy>()
                         .Distinct())
            {
                IEntry sourceEntry = sourceMaterials.FirstOrDefault(entry =>
                                         entry.UIndex == proxy.Export.UIndex && entry.FileRef == proxy.Export.FileRef)
                                     ?? sourceMaterials.FirstOrDefault(entry =>
                                         entry.InstancedFullPath.Equals(proxy.Export.InstancedFullPath, StringComparison.OrdinalIgnoreCase))
                                     ?? sourceMaterials.FirstOrDefault(entry =>
                                         entry.ObjectName.Name.Equals(proxy.Export.ObjectName.Name, StringComparison.OrdinalIgnoreCase));
                LiveMaterials.Add(new LiveMaterialEditorMaterial(proxy, sourceEntry));
            }

            SelectedLiveMaterial = LiveMaterials.FirstOrDefault(material =>
                                       material.SourceEntry?.ObjectName.Instanced.Equals(PendingLiveMaterialSelectionName, StringComparison.OrdinalIgnoreCase) == true)
                                   ?? LiveMaterials.FirstOrDefault();
            PendingLiveMaterialSelectionName = null;
            OnPropertyChanged(nameof(ShowLiveMaterialEditor));
        }

        private List<IEntry> GetCurrentMeshMaterialEntries()
        {
            if (LiveMaterialSourceOverrides is { Count: > 0 })
            {
                return LiveMaterialSourceOverrides.Where(entry => entry is not null).ToList();
            }

            if (CurrentLoadedExport is null)
            {
                return [];
            }

            var materialIndexes = new HashSet<int>();
            switch (ObjectBinary.From(CurrentLoadedExport))
            {
                case StaticMesh staticMesh:
                    foreach (StaticMeshElement element in staticMesh.LODModels.SelectMany(lod => lod.Elements))
                    {
                        if (element.Material != 0)
                        {
                            materialIndexes.Add(element.Material);
                        }
                    }
                    break;
                case SkeletalMesh skeletalMesh:
                    materialIndexes.UnionWith(skeletalMesh.Materials.Where(index => index != 0));
                    break;
            }

            return materialIndexes.Select(CurrentLoadedExport.FileRef.GetEntry).Where(entry => entry is not null).ToList();
        }

        private void ClearLiveMaterialEditor()
        {
            LiveMaterials.ClearEx();
            SelectedLiveMaterial = null;
            OnPropertyChanged(nameof(ShowLiveMaterialEditor));
        }

        private void SceneViewer_PreviewMouseDownForMaterialPicking(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left
                && (ShowMorphEditorPanel && HasMorphEditorData || !IsMorphEditorMode && RenderGameShader && GameShaderPreview is not null))
            {
                MaterialPickMouseDownPosition = e.GetPosition(SceneViewer);
            }
        }

        private void SceneViewer_PreviewMouseUpForMaterialPicking(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || MaterialPickMouseDownPosition is not { } mouseDownPosition)
            {
                return;
            }

            MaterialPickMouseDownPosition = null;
            System.Windows.Point mouseUpPosition = e.GetPosition(SceneViewer);
            System.Windows.Vector clickMovement = mouseUpPosition - mouseDownPosition;
            if (clickMovement.LengthSquared > 16) return;
            if (IsMorphEditorMode)
            {
                PickMorphViewport(mouseUpPosition);
                return;
            }
            if (!TryPickGameShaderMaterials(mouseUpPosition, out List<string> materialNames))
            {
                return;
            }

            var hitMaterials = new List<LiveMaterialEditorMaterial>();
            foreach (string materialName in materialNames)
            {
                if (GameShaderPreview.Materials.TryGetValue(materialName, out ModelPreviewMaterial<LEVertex> previewMaterial)
                    && previewMaterial.Material is MaterialRenderProxy renderProxy
                    && LiveMaterials.FirstOrDefault(material => ReferenceEquals(material.RenderProxy, renderProxy)) is { } hitLiveMaterial
                    && !hitMaterials.Contains(hitLiveMaterial))
                {
                    hitMaterials.Add(hitLiveMaterial);
                }
            }
            if (hitMaterials.Count == 0)
            {
                return;
            }

            if (!TryFindInfluencingVectorParameter(mouseUpPosition, hitMaterials,
                    out LiveMaterialEditorMaterial selectedMaterial, out LiveVectorMaterialParameter selectedParameter))
            {
                return;
            }

            selectedMaterial.VectorFilterText = null;
            SelectedLiveMaterial = selectedMaterial;
            SelectedLiveVectorParameter = selectedParameter;
            FocusSelectedLiveVectorParameter();
        }

        private bool TryFindInfluencingVectorParameter(System.Windows.Point screenPosition,
            IReadOnlyCollection<LiveMaterialEditorMaterial> hitMaterials,
            out LiveMaterialEditorMaterial selectedMaterial,
            out LiveVectorMaterialParameter selectedParameter)
        {
            selectedMaterial = null;
            selectedParameter = null;
            if (MeshContext.Backbuffer is null || MeshContext.Width <= 0 || MeshContext.Height <= 0
                || SceneViewer.ActualWidth <= 0 || SceneViewer.ActualHeight <= 0)
            {
                return false;
            }

            List<(LiveMaterialEditorMaterial Material, LiveVectorMaterialParameter Parameter)> candidates =
                hitMaterials.SelectMany(material => material.VectorParameters
                    .Where(parameter => !IsGlobalOverlayParameter(parameter))
                    .Select(parameter => (Material: material, Parameter: parameter)))
                    .ToList();
            if (candidates.Count == 0)
            {
                return false;
            }

            int pixelX = Math.Clamp((int)(screenPosition.X / SceneViewer.ActualWidth * MeshContext.Width), 0, MeshContext.Width - 1);
            int pixelY = Math.Clamp((int)(screenPosition.Y / SceneViewer.ActualHeight * MeshContext.Height), 0, MeshContext.Height - 1);

            MeshContext.Render();
            if (!MeshContext.TryReadBackbufferPixelNeighborhood(pixelX, pixelY, out Vector4 baselineColor))
            {
                return false;
            }

            float strongestResponse = 0;
            try
            {
                foreach ((LiveMaterialEditorMaterial material, LiveVectorMaterialParameter parameter) in candidates)
                {
                    var currentValue = new LinearColor(parameter.R, parameter.G, parameter.B, parameter.A);
                    material.RenderProxy.SetVectorParameter(parameter.ParameterName, CreateVectorParameterProbe(currentValue));
                    try
                    {
                        MeshContext.Render();
                        if (MeshContext.TryReadBackbufferPixelNeighborhood(pixelX, pixelY, out Vector4 probeColor))
                        {
                            Vector3 response = new(probeColor.X - baselineColor.X,
                                probeColor.Y - baselineColor.Y, probeColor.Z - baselineColor.Z);
                            float responseStrength = response.LengthSquared();
                            if (responseStrength > strongestResponse)
                            {
                                strongestResponse = responseStrength;
                                selectedMaterial = material;
                                selectedParameter = parameter;
                            }
                        }
                    }
                    finally
                    {
                        material.RenderProxy.SetVectorParameter(parameter.ParameterName, currentValue);
                    }
                }
            }
            finally
            {
                // Ensure the temporary probe value never remains visible or becomes a user edit.
                MeshContext.Render();
            }

            // Ignore byte-level render noise when no vector parameter materially affects the clicked pixel.
            const float minimumResponse = 3f / (255f * 255f);
            return selectedParameter is not null && strongestResponse >= minimumResponse;
        }

        private static LinearColor CreateVectorParameterProbe(LinearColor value)
        {
            static float FarthestEndpoint(float component) => Math.Abs(component) >= Math.Abs(component - 1f) ? 0f : 1f;
            return new LinearColor(FarthestEndpoint(value.R), FarthestEndpoint(value.G),
                FarthestEndpoint(value.B), FarthestEndpoint(value.A));
        }

        private bool TryPickGameShaderMaterials(System.Windows.Point screenPosition, out List<string> materialNames)
        {
            materialNames = [];
            if (GameShaderPreview is null
                || CurrentLOD < 0
                || CurrentLOD >= GameShaderPreview.LODs.Count
                || SceneViewer.ActualWidth <= 0
                || SceneViewer.ActualHeight <= 0)
            {
                return false;
            }

            float normalizedX = (float)(screenPosition.X / SceneViewer.ActualWidth * 2.0 - 1.0);
            float normalizedY = (float)(1.0 - screenPosition.Y / SceneViewer.ActualHeight * 2.0);
            Matrix4x4 viewProjection = MeshContext.Camera.ViewMatrix * MeshContext.Camera.ProjectionMatrix;
            if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection))
            {
                return false;
            }

            Vector4 nearClip = Vector4.Transform(new Vector4(normalizedX, normalizedY, 0, 1), inverseViewProjection);
            Vector4 farClip = Vector4.Transform(new Vector4(normalizedX, normalizedY, 1, 1), inverseViewProjection);
            if (Math.Abs(nearClip.W) < float.Epsilon || Math.Abs(farClip.W) < float.Epsilon)
            {
                return false;
            }

            Vector3 rayOrigin = new(nearClip.X / nearClip.W, nearClip.Y / nearClip.W, nearClip.Z / nearClip.W);
            Vector3 farPoint = new(farClip.X / farClip.W, farClip.Y / farClip.W, farClip.Z / farClip.W);
            Vector3 rayDirection = Vector3.Normalize(farPoint - rayOrigin);

            ModelPreviewLOD<LEVertex> lod = GameShaderPreview.LODs[CurrentLOD];
            Mesh<LEVertex> mesh = lod.Mesh;
            var nearestDistanceByMaterial = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            foreach (ModelPreviewSection section in lod.Sections)
            {
                int firstTriangle = (int)(section.StartIndex / 3);
                int endTriangle = Math.Min(mesh.Triangles.Count, firstTriangle + (int)section.TriangleCount);
                for (int triangleIndex = firstTriangle; triangleIndex < endTriangle; triangleIndex++)
                {
                    Triangle triangle = mesh.Triangles[triangleIndex];
                    if (triangle.Vertex1 >= (uint)mesh.Vertices.Count
                        || triangle.Vertex2 >= (uint)mesh.Vertices.Count
                        || triangle.Vertex3 >= (uint)mesh.Vertices.Count)
                    {
                        continue;
                    }

                    Vector3 vertex0 = mesh.Vertices[(int)triangle.Vertex1].Position;
                    Vector3 vertex1 = mesh.Vertices[(int)triangle.Vertex2].Position;
                    Vector3 vertex2 = mesh.Vertices[(int)triangle.Vertex3].Position;
                    if (RayIntersectsTriangle(rayOrigin, rayDirection, vertex0, vertex1, vertex2, out float distance)
                        && (!nearestDistanceByMaterial.TryGetValue(section.MaterialName, out float nearestDistance)
                            || distance < nearestDistance))
                    {
                        nearestDistanceByMaterial[section.MaterialName] = distance;
                    }
                }
            }

            materialNames = nearestDistanceByMaterial.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList();
            return materialNames.Count > 0;
        }

        private static bool RayIntersectsTriangle(Vector3 rayOrigin, Vector3 rayDirection,
            Vector3 vertex0, Vector3 vertex1, Vector3 vertex2, out float distance)
        {
            const float epsilon = 0.000001f;
            Vector3 edge1 = vertex1 - vertex0;
            Vector3 edge2 = vertex2 - vertex0;
            Vector3 cross = Vector3.Cross(rayDirection, edge2);
            float determinant = Vector3.Dot(edge1, cross);
            if (Math.Abs(determinant) < epsilon)
            {
                distance = 0;
                return false;
            }

            float inverseDeterminant = 1f / determinant;
            Vector3 originToVertex = rayOrigin - vertex0;
            float u = Vector3.Dot(originToVertex, cross) * inverseDeterminant;
            if (u < 0 || u > 1)
            {
                distance = 0;
                return false;
            }

            Vector3 secondCross = Vector3.Cross(originToVertex, edge1);
            float v = Vector3.Dot(rayDirection, secondCross) * inverseDeterminant;
            if (v < 0 || u + v > 1)
            {
                distance = 0;
                return false;
            }

            distance = Vector3.Dot(edge2, secondCross) * inverseDeterminant;
            return distance > epsilon;
        }

        private static LiveVectorMaterialParameter GetPreferredVectorParameter(LiveMaterialEditorMaterial material)
        {
            return material?.VectorParameters.FirstOrDefault();
        }

        private static bool IsGlobalOverlayParameter(LiveVectorMaterialParameter parameter)
        {
            string name = parameter.ParameterName;
            return name.Contains("Selection", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Highlight", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Overlay", StringComparison.OrdinalIgnoreCase);
        }

        private void FocusSelectedLiveVectorParameter()
        {
            LiveVectorMaterialParameter parameter = SelectedLiveVectorParameter;
            if (parameter is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                LiveVectorParameterList.ScrollIntoView(parameter);
                LiveVectorParameterList.UpdateLayout();
                if (LiveVectorParameterList.ItemContainerGenerator.ContainerFromItem(parameter) is FrameworkElement container)
                {
                    container.BringIntoView();
                    FindVisualDescendant<Xceed.Wpf.Toolkit.ColorCanvas>(container)?.Focus();
                }
            }));
        }

        private static T FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is null)
            {
                return null;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }
                if (FindVisualDescendant<T>(child) is { } descendant)
                {
                    return descendant;
                }
            }
            return null;
        }

        private void MaterialParameterScrubber_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            float speedMultiplier = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10f
                : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1f
                : 1f;

            if (sender is FrameworkElement { DataContext: LiveScalarMaterialParameter scalar })
            {
                scalar.Value += GetScrubberIncrement(scalar.Value, e.HorizontalChange, speedMultiplier);
            }
        }

        private static float GetScrubberIncrement(float value, double horizontalChange, float speedMultiplier)
        {
            float unitsPerPixel = Math.Max(Math.Abs(value) * 0.01f, 0.01f);
            return (float)horizontalChange * unitsPerPixel * speedMultiplier;
        }

        private async void RandomizeLiveMaterialScalars_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedLiveMaterial is not { } material || !CanRandomizeSelectedLiveMaterialScalars)
            {
                return;
            }

            try
            {
                await RandomizeLiveMaterialScalarsOverride(material);
            }
            catch (Exception exception)
            {
                new ExceptionHandlerDialog(exception).ShowDialog();
            }
        }

        private async void RandomizeLiveMaterialVectors_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedLiveMaterial is not { } material || !CanRandomizeSelectedLiveMaterialVectors)
            {
                return;
            }

            try
            {
                await RandomizeLiveMaterialVectorsOverride(material);
            }
            catch (Exception exception)
            {
                new ExceptionHandlerDialog(exception).ShowDialog();
            }
        }

        private void RandomizeLiveMaterialTints_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedLiveMaterial is not { } material || !CanRandomizeSelectedLiveMaterialTints)
            {
                return;
            }

            foreach (LiveVectorMaterialParameter parameter in material.VectorParameters.Where(IsTintParameter))
            {
                parameter.SetValue(
                    Random.Shared.NextSingle(),
                    Random.Shared.NextSingle(),
                    Random.Shared.NextSingle(),
                    parameter.A);
            }
        }

        private static bool IsTintParameter(LiveVectorMaterialParameter parameter) =>
            parameter.ParameterName.StartsWith("TNT_", StringComparison.OrdinalIgnoreCase);

        private void AddLiveMaterialScalar_Click(object sender, RoutedEventArgs e) => AddLiveMaterialParameter(isVector: false);

        private void AddLiveMaterialVector_Click(object sender, RoutedEventArgs e) => AddLiveMaterialParameter(isVector: true);

        private void RemoveLiveMaterialScalar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LiveScalarMaterialParameter parameter }
                || SelectedLiveMaterial is not { } material)
            {
                return;
            }

            int removedIndex = material.ScalarParameters.IndexOf(parameter);
            if (material.RemoveScalarParameter(parameter))
            {
                SelectedLiveScalarParameter = material.ScalarParameters.Count == 0
                    ? null
                    : material.ScalarParameters[Math.Min(removedIndex, material.ScalarParameters.Count - 1)];
                OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialScalars));
            }
        }

        private void RemoveLiveMaterialVector_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LiveVectorMaterialParameter parameter }
                || SelectedLiveMaterial is not { } material)
            {
                return;
            }

            int removedIndex = material.VectorParameters.IndexOf(parameter);
            if (material.RemoveVectorParameter(parameter))
            {
                SelectedLiveVectorParameter = material.VectorParameters.Count == 0
                    ? null
                    : material.VectorParameters[Math.Min(removedIndex, material.VectorParameters.Count - 1)];
                OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialVectors));
                OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialTints));
            }
        }

        private void AddLiveMaterialParameter(bool isVector)
        {
            if (SelectedLiveMaterial is not { } material)
            {
                return;
            }

            IReadOnlyList<string> parameterNames;
            try
            {
                using var cache = new PackageCache();
                var materialInfo = new MaterialInfo { MaterialExport = material.MaterialExport };
                IEnumerable<string> hierarchyNames = isVector
                    ? materialInfo.GetVectorParameterNames(cache)
                    : materialInfo.GetScalarParameterNames(cache);
                IEnumerable<string> shaderNames = isVector
                    ? material.RenderProxy.VectorParameters.Keys
                    : material.RenderProxy.ScalarParameters.Keys;
                parameterNames = hierarchyNames
                    .Concat(shaderNames)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    $"The material's parameter list could not be loaded.\n\n{exception.Message}",
                    "Material parameters unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string parameterType = isVector ? "vector" : "scalar";
            if (parameterNames.Count == 0)
            {
                MessageBox.Show(
                    $"No {parameterType} parameters were found on this material or its parent material.",
                    "No material parameters found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            HashSet<string> existingNames = new(
                isVector
                    ? material.VectorParameters.Select(parameter => parameter.ParameterName)
                    : material.ScalarParameters.Select(parameter => parameter.ParameterName),
                StringComparer.OrdinalIgnoreCase);
            string selectedName = StringSelectorDialog.GetValue(
                this,
                $"Choose a {parameterType} parameter. Type to search the {parameterNames.Count} values supported by this material.",
                $"Add {parameterType} parameter",
                parameterNames.Select(name => new StringSelectorItem(
                    name,
                    name,
                    existingNames.Contains(name) ? "Already present" : $"Available {parameterType} parameter")));
            if (string.IsNullOrWhiteSpace(selectedName))
            {
                return;
            }

            if (isVector)
            {
                SelectedLiveVectorParameter = material.AddVectorParameter(selectedName);
                LiveVectorParameterList.ScrollIntoView(SelectedLiveVectorParameter);
                OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialVectors));
                OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialTints));
            }
            else
            {
                SelectedLiveScalarParameter = material.AddScalarParameter(selectedName);
                LiveScalarParameterList.ScrollIntoView(SelectedLiveScalarParameter);
                OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialScalars));
            }
        }

        private void SaveLiveMaterialToCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedLiveMaterial is not { } material || !CanSaveSelectedLiveMaterialToCurrent)
            {
                return;
            }

            try
            {
                if (SaveLiveMaterialToCurrentOverride is not null)
                {
                    if (SaveLiveMaterialToCurrentOverride(material))
                    {
                        material.MarkSaved();
                    }
                    return;
                }
                WriteLiveMaterialParameters((ExportEntry)material.SourceEntry, material);
                material.MarkSaved();
            }
            catch (Exception exception)
            {
                new ExceptionHandlerDialog(exception).ShowDialog();
            }
        }

        private void SaveLiveMaterialAsNew_Click(object sender, RoutedEventArgs e)
        {
            LiveMaterialEditorMaterial material = SelectedLiveMaterial;
            ExportEntry meshExport = CurrentLoadedExport;
            if (material?.SourceEntry is null || meshExport is null || !CanSaveSelectedLiveMaterialAsNew)
            {
                return;
            }

            if (SaveLiveMaterialAsNewOverride is not null)
            {
                try
                {
                    if (SaveLiveMaterialAsNewOverride(material))
                    {
                        material.MarkSaved();
                    }
                }
                catch (Exception exception)
                {
                    new ExceptionHandlerDialog(exception).ShowDialog();
                }
                return;
            }

            string defaultName = $"{material.SourceEntry.ObjectName.Name}_Edited";
            string newName = PromptDialog.Prompt(this,
                "Name the new MaterialInstanceConstant:",
                "Save live material as new",
                defaultName,
                selectText: true,
                validator: value => ValidateNewMaterialName(meshExport, value));
            if (newName is null)
            {
                return;
            }

            try
            {
                ObjectBinary meshBinary = ObjectBinary.From(meshExport);
                int replacementCount = CountMeshMaterialAssignments(meshBinary, material.SourceEntry.UIndex);
                if (replacementCount == 0)
                {
                    throw new InvalidOperationException("The selected material is no longer assigned to this mesh.");
                }

                IEntry parent = meshExport.Parent;
                ExportEntry newMaterial = meshExport.FileRef.CreateExport(new NameReference(newName.Trim()),
                    "MaterialInstanceConstant", parent, indexed: false);
                var properties = new PropertyCollection
                {
                    new ObjectProperty(material.SourceEntry.UIndex, "Parent"),
                    CommonStructs.GuidProp(Guid.NewGuid(), "m_Guid")
                };
                newMaterial.WriteProperties(properties);
                WriteLiveMaterialParameters(newMaterial, material);

                ReplaceMeshMaterial(meshBinary, material.SourceEntry.UIndex, newMaterial.UIndex);
                PendingLiveMaterialSelectionName = newMaterial.ObjectName.Instanced;
                meshExport.WriteBinary(meshBinary);
                material.MarkSaved();
            }
            catch (Exception exception)
            {
                new ExceptionHandlerDialog(exception).ShowDialog();
            }
        }

        private static (bool, string) ValidateNewMaterialName(ExportEntry meshExport, string value)
        {
            string name = value?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return (false, "Enter a material name.");
            }
            if (!(char.IsLetter(name[0]) || name[0] == '_') || name.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
            {
                return (false, "Use letters, numbers, and underscores; the first character cannot be a number.");
            }

            string path = meshExport.Parent is { } parent ? $"{parent.InstancedFullPath}.{name}" : name;
            return meshExport.FileRef.FindEntry(path) is null
                ? (true, null)
                : (false, "An entry with that name already exists here.");
        }

        public static void WriteLiveMaterialParameters(ExportEntry target, LiveMaterialEditorMaterial material)
        {
            PropertyCollection properties = target.GetProperties();
            properties.RemoveNamedProperty("ScalarParameterValues");
            properties.RemoveNamedProperty("VectorParameterValues");

            var scalarValues = new ArrayProperty<StructProperty>("ScalarParameterValues");
            foreach (LiveScalarMaterialParameter parameter in material.ScalarParameters)
            {
                scalarValues.Add(new StructProperty("ScalarParameterValue", new PropertyCollection
                {
                    new NameProperty(parameter.ParameterName, "ParameterName"),
                    new FloatProperty(parameter.Value, "ParameterValue"),
                    CommonStructs.GuidProp(Guid.Empty, "ExpressionGUID")
                }));
            }

            var vectorValues = new ArrayProperty<StructProperty>("VectorParameterValues");
            foreach (LiveVectorMaterialParameter parameter in material.VectorParameters)
            {
                vectorValues.Add(new StructProperty("VectorParameterValue", new PropertyCollection
                {
                    new NameProperty(parameter.ParameterName, "ParameterName"),
                    CommonStructs.LinearColorProp(parameter.R, parameter.G, parameter.B, parameter.A, "ParameterValue"),
                    CommonStructs.GuidProp(Guid.Empty, "ExpressionGUID")
                }));
            }

            properties.Add(scalarValues);
            properties.Add(vectorValues);
            target.WriteProperties(properties);
        }

        private static int ReplaceMeshMaterial(ObjectBinary meshBinary, int oldUIndex, int newUIndex)
        {
            int replacementCount = 0;
            switch (meshBinary)
            {
                case StaticMesh staticMesh:
                    foreach (StaticMeshElement element in staticMesh.LODModels.SelectMany(lod => lod.Elements))
                    {
                        if (element.Material == oldUIndex)
                        {
                            element.Material = newUIndex;
                            replacementCount++;
                        }
                    }
                    break;
                case SkeletalMesh skeletalMesh:
                    for (int i = 0; i < skeletalMesh.Materials.Length; i++)
                    {
                        if (skeletalMesh.Materials[i] == oldUIndex)
                        {
                            skeletalMesh.Materials[i] = newUIndex;
                            replacementCount++;
                        }
                    }
                    break;
            }
            return replacementCount;
        }

        private static int CountMeshMaterialAssignments(ObjectBinary meshBinary, int materialUIndex) => meshBinary switch
        {
            StaticMesh staticMesh => staticMesh.LODModels.Sum(lod => lod.Elements.Count(element => element.Material == materialUIndex)),
            SkeletalMesh skeletalMesh => skeletalMesh.Materials.Count(index => index == materialUIndex),
            _ => 0
        };

        public override void UnloadExport()
        {
            UnloadAnimationPreview();
            IsBrush = false;
            IsSkeletalMesh = false;
            IsStaticMesh = false;
            IsModel = false;
            CurrentLoadedExport = null;
            ViewportLoadAction = null;
            STMCollisionMesh?.Dispose();
            STMCollisionMesh = null;
            SkeletonVertexBuffer?.Dispose();
            SkeletonVertexBuffer = null;
            SkeletonVertexCount = 0;
            SkeletonBonePositions = null;
            LEXPreview?.Dispose();
            LEXPreview = null;
            GameShaderPreview?.Dispose();
            GameShaderPreview = null;
            MaterialPickMouseDownPosition = null;
            ClearLiveMaterialEditor();
            UnloadMorphEditor();
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
            _previewAnimation = null;
            UnloadAnimationPreview();
            ThemeManager.ThemeChanged -= OnThemeChanged;
            if (Parent is TabItem { Parent: TabControl tc })
            {
                tc.SelectionChanged -= MeshRendererWPF_HostingTabSelectionChanged;
            }
            STMCollisionMesh?.Dispose();
            STMCollisionMesh = null;
            SkeletonVertexBuffer?.Dispose();
            SkeletonVertexBuffer = null;
            SkeletonBonePositions = null;
            LEXPreview?.Dispose();
            LEXPreview = null;
            GameShaderPreview?.Dispose();
            GameShaderPreview = null;
            ClearLiveMaterialEditor();
            UnloadMorphEditor();
            if (SceneViewer is { Context: not null })
            {
                SceneViewer.RemoveHandler(Mouse.PreviewMouseDownEvent,
                    new MouseButtonEventHandler(SceneViewer_PreviewMouseDownForMaterialPicking));
                SceneViewer.RemoveHandler(Mouse.PreviewMouseUpEvent,
                    new MouseButtonEventHandler(SceneViewer_PreviewMouseUpForMaterialPicking));
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
                    PauseMorphFaceFx();
                    SceneViewer?.SetShouldRender(false);
                }
            }
        }

        /// <summary>
        /// Starts the continuous render loop. Must be called when hosting outside a TabControl,
        /// e.g. directly in a Window or Dialog, after the control has been loaded.
        /// </summary>
        public void StartRendering()
        {
            if (SceneViewer is { } sv)
            {
                sv.SetShouldRender(true);
                // Re-apply the current pixel size so the initial frame renders
                // immediately rather than waiting for the next resize event.
                sv.InvalidateMeasure();
            }
        }

        /// <summary>
        /// Stops the continuous render loop. Call before closing the host window.
        /// </summary>
        public void StopRendering()
        {
            ResetMorphRegionCallouts();
            PauseMorphFaceFx();
            SceneViewer?.SetShouldRender(false);
        }

        private void MeshRendererWPF_OnKeyUp(object sender, KeyEventArgs e)
        {
            SceneViewer?.OnKeyUp(sender, e);
        }

        private void MeshRendererWPF_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBoxBase or PasswordBox
                || Keyboard.FocusedElement is TextBoxBase or PasswordBox
                || Keyboard.FocusedElement is ComboBox { IsEditable: true })
            {
                return;
            }

            SceneViewer?.OnKeyDown(sender, e);
        }
    }
}
