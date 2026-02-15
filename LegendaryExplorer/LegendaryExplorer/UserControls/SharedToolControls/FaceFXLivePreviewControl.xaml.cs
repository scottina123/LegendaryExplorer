using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.Win32;
using Device = SharpDX.Direct3D11.Device;
using SkeletalMesh = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;

namespace LegendaryExplorer.UserControls.SharedToolControls;

/// <summary>
/// FaceFX live preview control that renders a skeletal mesh with FaceFX bone-based
/// facial animations synchronized to audio playback.
/// 
/// ME3/LE3 uses bone-based facial animation where FaceFX curves drive bone transforms.
/// The curve names (m_Jaw+, m_Open, etc.) correspond to facial bones in the skeleton.
/// </summary>
public partial class FaceFXLivePreviewControl : NotifyPropertyChangedControlBase
{
    private MeshRenderContext _meshContext;
    private ModelPreview<WorldVertex> _meshPreview;
    private FaceFXBoneAnimator _faceFXAnimator;
    private SkinnedMeshRenderer _skinnedRenderer;
    private bool _previewInitialized;
    private ExportEntry _loadedMeshExport;
    private bool _isAnimating;
    private float _animTime;       // animation clock advanced by render loop
    private float _animDuration;   // duration of the current line's curves
    private IMEPackage _meshPackage;

    #region Bindable Properties

    private float _currentFaceFXTime;
    public float CurrentFaceFXTime
    {
        get => _currentFaceFXTime;
        set => SetProperty(ref _currentFaceFXTime, value);
    }

    private double _playbackSpeed = 1.0;
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set => SetProperty(ref _playbackSpeed, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    #endregion

    /// <summary>
    /// The package containing the FaceFX data - used to find compatible meshes
    /// </summary>
    public IMEPackage CurrentPackage { get; private set; }

    /// <summary>
    /// Available skeletal meshes found in the current package
    /// </summary>
    public ObservableCollectionExtended<ExportEntry> AvailableMeshes { get; } = new();

    /// <summary>
    /// Current FaceFX line data for animation
    /// </summary>
    private FaceFXLine _currentLine;
    private IFaceFXBinary _faceFXBinary;

    public FaceFXLivePreviewControl()
    {
        InitializeComponent();

        _meshContext = new MeshRenderContext
        {
            BackgroundColor = Colors.Gray
        };
        SceneViewer.Context = _meshContext;
        SceneViewer.Loaded += SceneViewer_Loaded;
    }

    private void SceneViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (_meshContext.IsReady && !_previewInitialized)
        {
            _meshContext.UpdateScene += OnUpdateScene;
            _meshContext.RenderScene += OnRenderScene;
            _previewInitialized = true;
            SceneViewer.SetShouldRender(true);
        }
    }

    #region Public API

    /// <summary>
    /// Sets the package to use for finding skeletal meshes
    /// </summary>
    public void SetPackage(IMEPackage package)
    {
        CurrentPackage = package;
        RefreshAvailableMeshes();
    }

    /// <summary>
    /// Refreshes the list of available skeletal meshes in the current package
    /// </summary>
    public void RefreshAvailableMeshes()
    {
        AvailableMeshes.Clear();
        if (CurrentPackage == null) return;

        var meshExports = CurrentPackage.Exports
            .Where(exp => exp.ClassName == "SkeletalMesh" && !exp.IsDefaultObject)
            .ToList();

        foreach (var mesh in meshExports)
        {
            AvailableMeshes.Add(mesh);
        }

        MeshComboBox.ItemsSource = AvailableMeshes;

        if (AvailableMeshes.Count > 0)
        {
            MeshComboBox.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Loads a skeletal mesh for preview
    /// </summary>
    public void LoadSkeletalMesh(ExportEntry skeletalMeshExport)
    {
        if (!_meshContext.IsReady) return;

        // Dispose old preview
        _meshPreview?.Dispose();
        _meshPreview = null;
        _faceFXAnimator = null;
        _skinnedRenderer = null;
        _loadedMeshExport = null;

        try
        {
            var skm = ObjectBinary.From<SkeletalMesh>(skeletalMeshExport);

            if (skm.LODModels.Length is 0)
            {
                StatusText.Text = "Mesh has no LODs";
                return;
            }

            _meshPreview = new ModelPreview<WorldVertex>(_meshContext, skm);
            _loadedMeshExport = skeletalMeshExport;

            // Initialize skinned mesh renderer for bone-based animation
            _skinnedRenderer = new SkinnedMeshRenderer();
            _skinnedRenderer.BuildFromSkeletalMesh(skeletalMeshExport.FileRef.Game, skm.LODModels[0]);

            // Initialize FaceFX bone animator
            _faceFXAnimator = new FaceFXBoneAnimator();
            _faceFXAnimator.SetSkeleton(skm);

            Mesh<WorldVertex> mesh = _meshPreview.LODs[0].Mesh;
            // Center camera on mesh
            _meshContext.Camera.FocusDepth = mesh.TransformedBounds.SphereRadius * 1.75f;
            _meshContext.Camera.Position = mesh.TransformedBounds.Origin;
            _meshContext.Camera.Pitch = -MathF.PI / 7.0f;

            int boneCount = _faceFXAnimator.BoneCount;
            int facialBoneCount = _faceFXAnimator.FacialBoneCount;
            StatusText.Text = $"Loaded: {skeletalMeshExport.ObjectName.Instanced} ({boneCount} bones, {facialBoneCount} facial)";
        }
        catch (Exception ex)
        {
            _meshContext.ErrorText = $"Error loading mesh: {ex.Message}";
            StatusText.Text = "Error loading mesh";
        }
    }

    /// <summary>
    /// Sets the FaceFX data to use for animation.
    /// Call StartAnimating() separately to begin playback.
    /// </summary>
    public void SetFaceFXData(IFaceFXBinary faceFXBinary, FaceFXLine line)
    {
        _faceFXBinary = faceFXBinary;
        _currentLine = line;
        _animDuration = ComputeLineDuration(line);
    }

    /// <summary>
    /// Starts playing the animation from the beginning.
    /// </summary>
    public void StartAnimating()
    {
        if (_faceFXAnimator == null || _currentLine?.Points == null || _currentLine.Points.Count == 0)
            return;

        _animTime = 0;
        _isAnimating = true;
    }

    /// <summary>
    /// Stops the animation and resets the mesh to bind pose.
    /// </summary>
    public void StopAnimating()
    {
        _isAnimating = false;
        _animTime = 0;
        CurrentFaceFXTime = 0;
        ResetToBindPose();
    }

    /// <summary>
    /// Computes the duration of a FaceFX line from the latest key time across all curves.
    /// </summary>
    private static float ComputeLineDuration(FaceFXLine line)
    {
        if (line?.Points == null || line.Points.Count == 0) return 0;
        float max = 0;
        foreach (var pt in line.Points)
        {
            if (pt.time > max) max = pt.time;
        }
        return max;
    }

    /// <summary>
    /// Resets mesh vertices back to the bind pose (identity skinning).
    /// </summary>
    private void ResetToBindPose()
    {
        if (_faceFXAnimator == null || _meshPreview == null || _skinnedRenderer == null) return;
        if (!_meshContext.IsReady || _meshPreview.LODs.Count == 0) return;

        var identityMatrices = new Matrix4x4[_faceFXAnimator.BoneCount];
        for (int i = 0; i < identityMatrices.Length; i++)
            identityMatrices[i] = Matrix4x4.Identity;

        _skinnedRenderer.UpdateSkinningWithMatrices(_meshContext.Device, _meshPreview.LODs[0].Mesh, identityMatrices);
    }

    /// <summary>
    /// Performs the actual skinning update. Called from render loop for smooth animation.
    /// </summary>
    private void UpdateSkinning()
    {
        if (_faceFXAnimator == null || _meshPreview == null || _skinnedRenderer == null)
            return;

        if (!_meshContext.IsReady || _meshPreview.LODs.Count == 0)
            return;

        if (_faceFXBinary == null || _currentLine == null)
            return;

        // Compute skinning matrices from FaceFX curves
        var skinningMatrices = _faceFXAnimator.ComputeSkinningMatrices(_faceFXBinary, _currentLine, CurrentFaceFXTime);
        if (skinningMatrices == null) return;

        // Apply skinning to mesh vertices
        _skinnedRenderer.UpdateSkinningWithMatrices(_meshContext.Device, _meshPreview.LODs[0].Mesh, skinningMatrices);
    }

    /// <summary>
    /// Clears the current mesh and animation
    /// </summary>
    public void Clear()
    {
        _isAnimating = false;
        _animTime = 0;
        _meshPreview?.Dispose();
        _meshPreview = null;
        _faceFXAnimator = null;
        _skinnedRenderer = null;
        _loadedMeshExport = null;
        _currentLine = null;
        _faceFXBinary = null;
        CurrentFaceFXTime = 0;
        StatusText.Text = "No mesh loaded";
    }

    /// <summary>
    /// Disposes resources used by this control
    /// </summary>
    public void Dispose()
    {
        if (_previewInitialized)
        {
            _meshContext.UpdateScene -= OnUpdateScene;
            _meshContext.RenderScene -= OnRenderScene;
        }
        _meshPreview?.Dispose();
        _meshPackage?.Dispose();
        SceneViewer?.Dispose();
    }

    #endregion

    #region Event Handlers

    private void MeshComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MeshComboBox.SelectedItem is ExportEntry meshExport)
        {
            LoadSkeletalMesh(meshExport);
        }
    }

    private void BrowseMesh_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Package files|*.pcc;*.u;*.upk;*.sfm|All files|*.*",
            Title = "Select a package containing a SkeletalMesh"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                _meshPackage?.Dispose();
                _meshPackage = MEPackageHandler.OpenMEPackage(dlg.FileName);

                var meshExports = _meshPackage.Exports
                    .Where(exp => exp.ClassName == "SkeletalMesh" && !exp.IsDefaultObject)
                    .ToList();

                if (meshExports.Count == 0)
                {
                    StatusText.Text = "No skeletal meshes found in package";
                    return;
                }

                AvailableMeshes.Clear();
                foreach (var mesh in meshExports)
                {
                    AvailableMeshes.Add(mesh);
                }

                MeshComboBox.ItemsSource = AvailableMeshes;
                MeshComboBox.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }
    }

    #endregion

    #region Internal Rendering

    private void OnUpdateScene(object sender, float timestep)
    {
        if (!_isAnimating) return;

        // Advance animation clock using render-loop timestep (high-precision, every frame)
        _animTime += timestep;

        // Stop at the end of the line's curves
        if (_animDuration > 0 && _animTime >= _animDuration)
        {
            _animTime = _animDuration;
            _isAnimating = false;
        }

        CurrentFaceFXTime = _animTime;
        UpdateSkinning();
    }

    private void OnRenderScene(object sender, EventArgs e)
    {
        if (_meshPreview is not { LODs.Count: > 0 })
        {
            return;
        }

        foreach (RenderPass renderPass in Enum.GetValues<RenderPass>())
        {
            _meshPreview.Render(renderPass, _meshContext, 0);
        }
    }

    #endregion

    /// <summary>
    /// Interface for FaceFX binary data - copied from FaceFXAnimSetEditorControl
    /// </summary>
    public interface IFaceFXBinary
    {
        List<string> Names { get; }
        List<FaceFXLine> Lines { get; }
        ObjectBinary Binary { get; }
    }
}

/// <summary>
/// Animator that applies FaceFX bone-based facial animations to a skeleton.
/// FaceFX curves drive bone transforms for facial animation in ME3/LE3.
/// 
/// FaceFX animation names include:
/// - Lip sync: m_EE, m_EH, m_Open, m_OW, m_FV, m_TH, m_M, m_N, m_L, m_OH, m_G, m_ZZ, m_Flap, m_Jaw+, m_Jaw-
/// - Head: Orientation_Head_Pitch/Roll/Yaw, Emphasis_Head_Pitch/Roll/Yaw
/// - Eyes: Gaze_Eye_Pitch/Yaw, Blink
/// - Face: Eyebrow_Raise
/// - Neck: E_D_Neck_Pitch
/// 
/// UE3 Coordinate System: X=Forward, Y=Right, Z=Up
/// - Pitch = rotation around Y axis (nodding up/down)
/// - Yaw = rotation around Z axis (turning left/right)
/// - Roll = rotation around X axis (tilting)
/// </summary>
public class FaceFXBoneAnimator
{
    private MeshBone[] _bones;
    private Matrix4x4[] _inverseBindPose;
    private Matrix4x4[] _skinningMatrices;
    private Matrix4x4[] _bindPose;
    private int _facialBoneCount;

    // Pre-allocated per-frame work arrays (avoid GC pressure)
    private Matrix4x4[] _localTransforms;
    private Matrix4x4[] _animatedCS;
    // Pre-computed bind-pose local transforms
    private Matrix4x4[] _bindLocalTransforms;
    // Pre-computed bind-pose rotations and positions (avoid Decompose per frame)
    private Quaternion[] _bindRotations;
    private Vector3[] _bindPositions;

    // Cached bone indices for common bones
    private int _headBoneIndex = -1;
    private int _neckBoneIndex = -1;
    private int _jawBoneIndex = -1;
    private int _leftEyeBoneIndex = -1;
    private int _rightEyeBoneIndex = -1;
    private int _leftBrowBoneIndex = -1;
    private int _rightBrowBoneIndex = -1;
    private int _leftUpperLidIndex = -1;
    private int _rightUpperLidIndex = -1;
    private int _leftLowerLidIndex = -1;
    private int _rightLowerLidIndex = -1;

    // Pre-classified curve info to avoid per-frame string operations
    private enum CurveType
    {
        HeadPitch, HeadYaw, HeadRoll,
        NeckPitch, NeckYaw, NeckRoll,
        EyePitch, EyeYaw,
        LeftEyePitch, LeftEyeYaw,
        RightEyePitch, RightEyeYaw,
        Blink,
        EyelidSquint,
        EyelidWide,
        BrowRaise,
        BrowLower,
        LipSync,
        Ignored
    }
    private struct CurveMapping
    {
        public CurveType Type;
        public float Multiplier; // jaw contribution for LipSync, direction for emotions
    }
    private CurveMapping[] _curveMappings;
    private bool _curveMappingsBuilt;
    private int _lastLineHashCode;

    public int BoneCount => _bones?.Length ?? 0;
    public int FacialBoneCount => _facialBoneCount;

    /// <summary>
    /// Builds bind-pose transforms from a SkeletalMesh's RefSkeleton.
    /// </summary>
    public void SetSkeleton(SkeletalMesh skeletalMesh)
    {
        _bones = skeletalMesh.RefSkeleton;
        int numBones = _bones.Length;
        _bindPose = new Matrix4x4[numBones];
        _inverseBindPose = new Matrix4x4[numBones];
        _skinningMatrices = new Matrix4x4[numBones];
        _localTransforms = new Matrix4x4[numBones];
        _animatedCS = new Matrix4x4[numBones];
        _bindLocalTransforms = new Matrix4x4[numBones];
        _bindRotations = new Quaternion[numBones];
        _bindPositions = new Vector3[numBones];
        _facialBoneCount = 0;
        _curveMappingsBuilt = false;

        // Reset bone indices
        _headBoneIndex = -1;
        _neckBoneIndex = -1;
        _jawBoneIndex = -1;
        _leftEyeBoneIndex = -1;
        _rightEyeBoneIndex = -1;
        _leftBrowBoneIndex = -1;
        _rightBrowBoneIndex = -1;
        _leftUpperLidIndex = -1;
        _rightUpperLidIndex = -1;
        _leftLowerLidIndex = -1;
        _rightLowerLidIndex = -1;

        for (int i = 0; i < numBones; i++)
        {
            var bone = _bones[i];
            string lowerName = bone.Name.Instanced.ToLowerInvariant();

            // Find and cache important bone indices
            if (_headBoneIndex == -1 && (lowerName == "head" || lowerName.EndsWith("_head")))
                _headBoneIndex = i;
            else if (_neckBoneIndex == -1 && (lowerName == "neck" || lowerName.EndsWith("_neck")))
                _neckBoneIndex = i;
            else if (_jawBoneIndex == -1 && (lowerName.Contains("jaw") && !lowerName.Contains("upper")))
                _jawBoneIndex = i;
            else if (_leftEyeBoneIndex == -1 && lowerName.Contains("eye") && (lowerName.StartsWith("l_") || lowerName.Contains("_l_") || lowerName.EndsWith("_l")) && !lowerName.Contains("lid") && !lowerName.Contains("lash"))
                _leftEyeBoneIndex = i;
            else if (_rightEyeBoneIndex == -1 && lowerName.Contains("eye") && (lowerName.StartsWith("r_") || lowerName.Contains("_r_") || lowerName.EndsWith("_r")) && !lowerName.Contains("lid") && !lowerName.Contains("lash"))
                _rightEyeBoneIndex = i;
            else if (_leftBrowBoneIndex == -1 && lowerName.Contains("brow") && (lowerName.StartsWith("l_") || lowerName.Contains("_l")))
                _leftBrowBoneIndex = i;
            else if (_rightBrowBoneIndex == -1 && lowerName.Contains("brow") && (lowerName.StartsWith("r_") || lowerName.Contains("_r")))
                _rightBrowBoneIndex = i;
            else if (_leftUpperLidIndex == -1 && lowerName.Contains("lid") && lowerName.Contains("up") && (lowerName.StartsWith("l_") || lowerName.Contains("_l")))
                _leftUpperLidIndex = i;
            else if (_rightUpperLidIndex == -1 && lowerName.Contains("lid") && lowerName.Contains("up") && (lowerName.StartsWith("r_") || lowerName.Contains("_r")))
                _rightUpperLidIndex = i;
            else if (_leftLowerLidIndex == -1 && lowerName.Contains("lid") && lowerName.Contains("low") && (lowerName.StartsWith("l_") || lowerName.Contains("_l")))
                _leftLowerLidIndex = i;
            else if (_rightLowerLidIndex == -1 && lowerName.Contains("lid") && lowerName.Contains("low") && (lowerName.StartsWith("r_") || lowerName.Contains("_r")))
                _rightLowerLidIndex = i;

            if (IsFacialBone(lowerName))
                _facialBoneCount++;

            // Cache bind-pose local transform, rotation, and position
            _bindRotations[i] = bone.Orientation;
            _bindPositions[i] = bone.Position;
            var localTransform = Matrix4x4.CreateFromQuaternion(bone.Orientation) * Matrix4x4.CreateTranslation(bone.Position);
            _bindLocalTransforms[i] = localTransform;

            if (bone.ParentIndex >= 0 && bone.ParentIndex < i)
            {
                _bindPose[i] = localTransform * _bindPose[bone.ParentIndex];
            }
            else
            {
                _bindPose[i] = localTransform;
            }

            Matrix4x4.Invert(_bindPose[i], out _inverseBindPose[i]);
            _skinningMatrices[i] = Matrix4x4.Identity;
        }
    }

    private static bool IsFacialBone(string lowerName)
    {
        return lowerName.Contains("jaw") || lowerName.Contains("lip") || lowerName.Contains("eye") ||
               lowerName.Contains("brow") || lowerName.Contains("cheek") || lowerName.Contains("nose") ||
               lowerName.Contains("tongue") || lowerName.Contains("chin") || lowerName.Contains("mouth") ||
               lowerName.Contains("sneer") || lowerName.Contains("smile") || lowerName.Contains("frown") ||
               lowerName.Contains("pucker") || lowerName.Contains("head") || lowerName.Contains("neck") ||
               lowerName.Contains("lid") || lowerName.Contains("lash");
    }

    /// <summary>
    /// Pre-classifies curve names so we never do string operations per-frame.
    /// Covers all FaceFX curve categories: lip sync, head/neck/eye orientation,
    /// emotions (brow/eye/mouth), gestures, blink, and brow details.
    /// </summary>
    private void BuildCurveMappings(FaceFXLivePreviewControl.IFaceFXBinary faceFX, FaceFXLine line)
    {
        int count = line.AnimationNames.Count;
        _curveMappings = new CurveMapping[count];

        for (int i = 0; i < count; i++)
        {
            if (line.AnimationNames[i] >= faceFX.Names.Count)
            {
                _curveMappings[i] = new CurveMapping { Type = CurveType.Ignored };
                continue;
            }

            string name = faceFX.Names[line.AnimationNames[i]];
            _curveMappings[i] = ClassifyCurve(name);
        }

        _curveMappingsBuilt = true;
        _lastLineHashCode = RuntimeHelpers.GetHashCode(line);
    }

    private static CurveMapping ClassifyCurve(string name)
    {
        string lower = name.ToLowerInvariant();

        // === SKIP: driver nodes, correctives, wrinkles, fixers ===
        if (lower.EndsWith("_wrinkle") || lower.EndsWith("_fixer") || lower.EndsWith("_val") ||
            lower.StartsWith("ph_") || lower.StartsWith("w_") || lower.StartsWith("limiter_") ||
            lower == "aibark" || lower == "ph_nullifier" ||
            lower == "winkleconstant" || lower == "wrinkleconstant")
            return new CurveMapping { Type = CurveType.Ignored };
        if (lower.EndsWith("_sum") || lower.EndsWith("_constant"))
            return new CurveMapping { Type = CurveType.Ignored };

        // === HEAD ORIENTATION ===
        if (lower.Contains("orientation_head_") || lower.Contains("emphasis_head_") ||
            lower.Contains("e_d_head_") || lower == "head_pitch" || lower == "head_yaw" || lower == "head_roll" ||
            lower == "aiheadpitch" || lower == "aiheadyaw" || lower == "aiheadroll")
        {
            if (lower.Contains("pitch")) return new CurveMapping { Type = CurveType.HeadPitch, Multiplier = 1f };
            if (lower.Contains("yaw")) return new CurveMapping { Type = CurveType.HeadYaw, Multiplier = 1f };
            if (lower.Contains("roll")) return new CurveMapping { Type = CurveType.HeadRoll, Multiplier = 1f };
        }
        if (lower.StartsWith("head_r"))
        {
            if (lower.StartsWith("head_rx")) return new CurveMapping { Type = CurveType.HeadYaw, Multiplier = lower.EndsWith("+") ? 1f : -1f };
            if (lower.StartsWith("head_ry")) return new CurveMapping { Type = CurveType.HeadRoll, Multiplier = lower.EndsWith("+") ? 1f : -1f };
            if (lower.StartsWith("head_rz")) return new CurveMapping { Type = CurveType.HeadPitch, Multiplier = lower.EndsWith("+") ? 1f : -1f };
        }
        if (lower == "e_gesture_headup") return new CurveMapping { Type = CurveType.HeadPitch, Multiplier = -1f };
        if (lower == "e_gesture_headdown") return new CurveMapping { Type = CurveType.HeadPitch, Multiplier = 1f };
        if (lower == "e_gesture_headleft") return new CurveMapping { Type = CurveType.HeadYaw, Multiplier = 1f };
        if (lower == "e_gesture_headright") return new CurveMapping { Type = CurveType.HeadYaw, Multiplier = -1f };
        if (lower == "e_gesture_headrollleft") return new CurveMapping { Type = CurveType.HeadRoll, Multiplier = 1f };
        if (lower == "e_gesture_headrollright") return new CurveMapping { Type = CurveType.HeadRoll, Multiplier = -1f };

        // === NECK ORIENTATION ===
        if (lower.Contains("neck") && (lower.Contains("pitch") || lower.Contains("yaw") || lower.Contains("roll") ||
            lower.StartsWith("neck_r")))
        {
            if (lower.Contains("pitch") || lower.StartsWith("neck_rz")) return new CurveMapping { Type = CurveType.NeckPitch, Multiplier = lower.EndsWith("-") ? -1f : 1f };
            if (lower.Contains("yaw") || lower.StartsWith("neck_rx")) return new CurveMapping { Type = CurveType.NeckYaw, Multiplier = lower.EndsWith("-") ? -1f : 1f };
            if (lower.Contains("roll") || lower.StartsWith("neck_ry")) return new CurveMapping { Type = CurveType.NeckRoll, Multiplier = lower.EndsWith("-") ? -1f : 1f };
        }
        if (lower.StartsWith("e_gesture_neckforward") || lower.StartsWith("e_gesture_neckback"))
            return new CurveMapping { Type = CurveType.NeckPitch, Multiplier = lower.Contains("forward") ? 1f : -1f };

        // === EYE GAZE (both eyes) ===
        if (lower.Contains("gaze_eye_") || lower == "eye_pitch" || lower == "eye_yaw" ||
            lower.Contains("e_d_eye_pitch") || lower.Contains("e_d_eye_yaw"))
        {
            if (lower.Contains("pitch")) return new CurveMapping { Type = CurveType.EyePitch, Multiplier = 1f };
            if (lower.Contains("yaw")) return new CurveMapping { Type = CurveType.EyeYaw, Multiplier = 1f };
        }
        if (lower == "e_gesture_eyesup") return new CurveMapping { Type = CurveType.EyePitch, Multiplier = -1f };
        if (lower == "e_gesture_eyesdown") return new CurveMapping { Type = CurveType.EyePitch, Multiplier = 1f };
        if (lower == "e_gesture_eyesleft") return new CurveMapping { Type = CurveType.EyeYaw, Multiplier = 1f };
        if (lower == "e_gesture_eyesright") return new CurveMapping { Type = CurveType.EyeYaw, Multiplier = -1f };
        // Krogan: E_LookingUpLoop, E_LookingDownLoop, E_LookingLeftUp, E_LookingRightNeutral, etc.
        if (lower.StartsWith("e_looking"))
        {
            if (lower.Contains("left")) return new CurveMapping { Type = CurveType.EyeYaw, Multiplier = 1f };
            if (lower.Contains("right")) return new CurveMapping { Type = CurveType.EyeYaw, Multiplier = -1f };
            if (lower.Contains("up")) return new CurveMapping { Type = CurveType.EyePitch, Multiplier = -1f };
            if (lower.Contains("down")) return new CurveMapping { Type = CurveType.EyePitch, Multiplier = 1f };
        }

        // === INDIVIDUAL EYE ROTATION (eye_Left_RY+, eye_Right_RZ-, etc.) ===
        if (lower.StartsWith("eye_left_r"))
        {
            float dir = lower.EndsWith("+") ? 1f : -1f;
            if (lower.StartsWith("eye_left_ry") || lower.StartsWith("eye_left_rx")) return new CurveMapping { Type = CurveType.LeftEyeYaw, Multiplier = dir };
            if (lower.StartsWith("eye_left_rz")) return new CurveMapping { Type = CurveType.LeftEyePitch, Multiplier = dir };
        }
        if (lower.StartsWith("eye_right_r"))
        {
            float dir = lower.EndsWith("+") ? 1f : -1f;
            if (lower.StartsWith("eye_right_ry") || lower.StartsWith("eye_right_rx")) return new CurveMapping { Type = CurveType.RightEyeYaw, Multiplier = dir };
            if (lower.StartsWith("eye_right_rz")) return new CurveMapping { Type = CurveType.RightEyePitch, Multiplier = dir };
        }

        // === BLINK / EYELIDS (all species) ===
        if (lower is "blink" or "blinker" or "m_blinker" or "e_d_blink" or "blinknode" or "blinkupdown"
            or "eyeblink" or "eyeblinker")
            return new CurveMapping { Type = CurveType.Blink, Multiplier = 1f };
        if (lower is "blinknegate")
            return new CurveMapping { Type = CurveType.Blink, Multiplier = -1f };
        if (lower is "e_gesture_closeeyes")
            return new CurveMapping { Type = CurveType.Blink, Multiplier = 1f };
        if (lower is "blinkright" or "blinkleft")
            return new CurveMapping { Type = CurveType.Blink, Multiplier = 1f };
        if (lower.Contains("eyelidupper") && lower.Contains("+"))
            return new CurveMapping { Type = CurveType.EyelidWide, Multiplier = 1f };
        if (lower.Contains("eyelidupper") && lower.Contains("-"))
            return new CurveMapping { Type = CurveType.Blink, Multiplier = 1f };
        if (lower.Contains("wideopen_eyelids"))
            return new CurveMapping { Type = CurveType.EyelidWide, Multiplier = 1f };
        if (lower.Contains("squint_eyelids") || lower == "squintnode")
            return new CurveMapping { Type = CurveType.EyelidSquint, Multiplier = 1f };
        // Drell: wideEyeRight, wideEyeLeft
        if (lower.StartsWith("wideeye"))
            return new CurveMapping { Type = CurveType.EyelidWide, Multiplier = 1f };
        // Drell: lowLidRightUp/Down, lowLidLeftUp/Down
        if (lower.StartsWith("lowlid"))
        {
            if (lower.Contains("up")) return new CurveMapping { Type = CurveType.EyelidSquint, Multiplier = 0.5f };
            return new CurveMapping { Type = CurveType.Ignored };
        }
        // Eyelid rotation curves (drell: eyeLidRight_RY+/-)
        if (lower.StartsWith("eyelid") && lower.Contains("_ry"))
            return new CurveMapping { Type = CurveType.Ignored };
        if (lower.StartsWith("blinkfix") || lower.StartsWith("blinkcorrective") || lower.Contains("blinkslookat"))
            return new CurveMapping { Type = CurveType.Ignored };

        // === EYEBROW (all species) ===
        if (lower is "eyebrow_raise")
            return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 1f };
        if (lower.StartsWith("m_brow"))
        {
            if (lower.Contains("_u")) return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 1f };
            if (lower.Contains("_d")) return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 1f };
            if (lower.Contains("in")) return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 0.5f };
            return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.3f };
        }
        if (lower == "m_browmidup") return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 1f };
        if (lower == "m_browmiddown") return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 1f };
        // Drell/other: innerBrowRightUp, innerBrowLeftDown, outBrowRightUp, etc.
        if (lower.StartsWith("innerbrow") || lower.StartsWith("outbrow"))
        {
            if (lower.Contains("up") && !lower.Contains("down")) return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.7f };
            if (lower.Contains("down")) return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 0.7f };
            if (lower.Contains("_rotatein") || lower.Contains("_in")) return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 0.3f };
            if (lower.Contains("_rotateout") || lower.Contains("_out")) return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.3f };
            return new CurveMapping { Type = CurveType.Ignored };
        }
        if (lower.Contains("cockedbrows") || lower.Contains("updownbrow") || lower.Contains("emotionbrows"))
        {
            if (lower.Contains("_u") || lower.Contains("_l")) return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.5f };
            if (lower.Contains("_d") || lower.Contains("_r")) return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 0.5f };
        }

        // === B_ PREFIXED CURVES (check specific patterns before generic emotion brow) ===
        if (lower.StartsWith("b_"))
        {
            if (lower.StartsWith("blink")) return new CurveMapping { Type = CurveType.Ignored };
            // Eye B_ curves (drell: B_EyesWide, B_EyeSquint)
            if (lower.Contains("eyeswide") || lower.Contains("eyewide"))
                return new CurveMapping { Type = CurveType.EyelidWide, Multiplier = 0.5f };
            if (lower.Contains("eyesquint") || lower.Contains("eyequint"))
                return new CurveMapping { Type = CurveType.EyelidSquint, Multiplier = 0.5f };
            if (lower.Contains("cheek"))
                return new CurveMapping { Type = CurveType.Ignored };
            // Drell named brow results (B_ShockBrow, B_SadBrow, B_RageBrow, etc.)
            if (lower.Contains("shockbrow") || lower.Contains("questionbrow") || lower.Contains("perplexedbrow"))
                return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.5f };
            if (lower.Contains("ragebrow") || lower.Contains("squintbrow") || lower.Contains("woundedbrow") || lower.Contains("woundedgrimace"))
                return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 0.5f };
            if (lower.Contains("sadbrow"))
                return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.4f };
            // Generic emotion B_ (human: B_Fear1, B_Anger3, etc.)
            return new CurveMapping { Type = GetEmotionBrowType(lower), Multiplier = 0.5f };
        }

        // === EMOTION BROW CURVES (E_WB_, E_B_, E_D_B_, p_B_) ===
        if (lower.StartsWith("e_wb_") || lower.StartsWith("e_b_") || lower.StartsWith("e_d_b_") || lower.StartsWith("p_b_"))
            return new CurveMapping { Type = GetEmotionBrowType(lower), Multiplier = GetEmotionBrowMultiplier(lower) };

        // === EMOTION EYE CURVES (E_Y_, E_D_Y_, Y_) ===
        if (lower.StartsWith("e_y_") || lower.StartsWith("e_d_y_") || lower.StartsWith("y_"))
            return new CurveMapping { Type = GetEmotionEyeType(lower), Multiplier = GetEmotionEyeMultiplier(lower) };

        // === EMOTION MOUTH/SMILE CURVES (E_S_, E_D_S_, S_, E_D_F_) ===
        if (lower.StartsWith("e_s_") || lower.StartsWith("e_d_s_") || lower.StartsWith("s_") || lower.StartsWith("e_d_f_"))
            return new CurveMapping { Type = CurveType.LipSync, Multiplier = GetEmotionMouthJaw(lower) };

        // === FULL-FACE EMOTION PRESETS ===
        if (lower.StartsWith("e_happy_") || lower.StartsWith("e_flirt_"))
            return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.3f };
        if (lower.StartsWith("e_sad_") || lower.StartsWith("e_wounded_"))
            return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.4f };
        if (lower.StartsWith("e_angry_"))
            return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 0.5f };
        if (lower.StartsWith("e_neutral_"))
            return new CurveMapping { Type = CurveType.Ignored };

        // === PERSISTENT EXPRESSIONS ===
        if (lower.StartsWith("p_mouth_"))
            return new CurveMapping { Type = CurveType.LipSync, Multiplier = lower.Contains("happy") || lower.Contains("smug") ? -0.1f : 0.1f };
        if (lower.StartsWith("p_eye_"))
            return new CurveMapping { Type = CurveType.Ignored };
        if (lower.StartsWith("p_happy_") || lower.StartsWith("p_flirt_"))
            return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.2f };
        if (lower.StartsWith("p_sad_") || lower.StartsWith("p_wounded_"))
            return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.3f };
        if (lower.StartsWith("p_angry_"))
            return new CurveMapping { Type = CurveType.BrowLower, Multiplier = 0.3f };
        if (lower.StartsWith("p_neutral_"))
            return new CurveMapping { Type = CurveType.Ignored };

        // === JAW CURVES (bare names — drell/other species) ===
        if (lower == "jawopen" || lower == "o_mouth")
            return new CurveMapping { Type = CurveType.LipSync, Multiplier = 1.0f };
        if (lower == "jawclench" || lower == "smilejawclench")
            return new CurveMapping { Type = CurveType.LipSync, Multiplier = -0.3f };
        if (lower.StartsWith("jawrotate"))
            return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.2f };
        if (lower is "jawsideleft" or "jawsideright" or "jawforward" or "jawback")
            return new CurveMapping { Type = CurveType.Ignored };

        // === LIP SYNC m_ CURVES (human jaw/mouth shapes) ===
        if (lower.StartsWith("m_"))
        {
            if (lower.StartsWith("m_jaw"))
            {
                if (lower.Contains("+") || lower.Contains("open")) return new CurveMapping { Type = CurveType.LipSync, Multiplier = 1.0f };
                if (lower.Contains("clench")) return new CurveMapping { Type = CurveType.LipSync, Multiplier = -0.3f };
                return new CurveMapping { Type = CurveType.LipSync, Multiplier = -0.5f };
            }
            if (lower.StartsWith("m_open")) return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.6f };
            if (lower.StartsWith("m_closed")) return new CurveMapping { Type = CurveType.LipSync, Multiplier = -0.1f };
            if (lower is "m_oh" or "m_ow" or "m_eh") return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.6f };
            if (lower is "m_ee" or "m_g" or "m_flap") return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.3f };
            if (lower is "m_fv" or "m_th" or "m_m" or "m_n" or "m_l" or "m_zz") return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.1f };
            if (lower.Contains("lipcornerup") || lower.Contains("smile_frown_u") || lower == "m_smilefull")
                return new CurveMapping { Type = CurveType.LipSync, Multiplier = -0.05f };
            if (lower.Contains("lipcornerdown") || lower.Contains("smile_frown_d"))
                return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.05f };
            if (lower.Contains("angry"))
                return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.05f };
            return new CurveMapping { Type = CurveType.Ignored };
        }

        // === BARE SMILE/FROWN (drell: smileRight, smileLeft, frownRight, etc.) ===
        if (lower.StartsWith("smile"))
        {
            if (lower.Contains("omouth")) return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.3f };
            return new CurveMapping { Type = CurveType.LipSync, Multiplier = -0.05f };
        }
        if (lower.StartsWith("frown"))
            return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.05f };
        if (lower.StartsWith("opensmile"))
            return new CurveMapping { Type = CurveType.LipSync, Multiplier = 0.2f };

        // === BARE SNEER, PUCKER, CHEEK, NOSE, TONGUE, LIP DETAIL ===
        if (lower.StartsWith("sneer") || lower.StartsWith("pucker") || lower.StartsWith("cheek") ||
            lower.StartsWith("nose") || lower.StartsWith("tongue") || lower.StartsWith("upperlip") ||
            lower.StartsWith("lowerlip") || lower.StartsWith("lipcurl") || lower.StartsWith("mouthdown") ||
            lower.StartsWith("crowsfoot") || lower == "narrowmouth" || lower == "cheekpuff")
            return new CurveMapping { Type = CurveType.Ignored };

        // === YAHG F_ FACE EMOTION PRESETS ===
        if (lower.StartsWith("f_happy") || lower.StartsWith("f_flirt") || lower.StartsWith("f_flirst"))
            return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.3f };
        if (lower.StartsWith("f_sad") || lower.StartsWith("f_wounded"))
            return new CurveMapping { Type = CurveType.BrowRaise, Multiplier = 0.4f };
        if (lower.StartsWith("f_neutral"))
            return new CurveMapping { Type = CurveType.Ignored };

        // Unrecognized — ignore rather than treat as jaw movement
        return new CurveMapping { Type = CurveType.Ignored };
    }

    /// <summary>Determines brow type for emotion brow curves based on the emotion name.</summary>
    private static CurveType GetEmotionBrowType(string lower)
    {
        // Emotions that RAISE brows
        if (lower.Contains("fear") || lower.Contains("terror") || lower.Contains("shock") ||
            lower.Contains("laughter") || lower.Contains("joy") || lower.Contains("amusement") ||
            lower.Contains("satisfaction") || lower.Contains("concern") ||
            lower.Contains("sadness") || lower.Contains("grief") || lower.Contains("melancholy"))
            return CurveType.BrowRaise;

        // Emotions that LOWER/furrow brows
        if (lower.Contains("anger") || lower.Contains("angry") || lower.Contains("rage") || lower.Contains("indignation") ||
            lower.Contains("stern") || lower.Contains("disgust") || lower.Contains("revulsion") ||
            lower.Contains("aversion") || lower.Contains("disdain") || lower.Contains("dejection") ||
            lower.Contains("anxiety"))
            return CurveType.BrowLower;

        return CurveType.BrowRaise;
    }

    /// <summary>Gets intensity multiplier for emotion brow curves.</summary>
    private static float GetEmotionBrowMultiplier(string lower)
    {
        // Persistent and driver curves are subtler
        if (lower.StartsWith("p_b_")) return 0.3f;
        if (lower.StartsWith("e_d_b_")) return 0.4f;
        // E_WB_ (whole brow) is strongest
        if (lower.StartsWith("e_wb_")) return 0.8f;
        // E_B_ and B_ are moderate
        return 0.5f;
    }

    /// <summary>Determines eye type for emotion eye curves.</summary>
    private static CurveType GetEmotionEyeType(string lower)
    {
        // Wide-eyed emotions
        if (lower.Contains("terror") || lower.Contains("fear") || lower.Contains("shock") ||
            lower.Contains("wide"))
            return CurveType.EyelidWide;

        // Squinting emotions
        if (lower.Contains("laughter") || lower.Contains("joy") || lower.Contains("amusement") ||
            lower.Contains("satisfaction") || lower.Contains("anger") || lower.Contains("rage") ||
            lower.Contains("disgust") || lower.Contains("revulsion") || lower.Contains("squint") ||
            lower.Contains("stern") || lower.Contains("indignation") || lower.Contains("disdain") ||
            lower.Contains("aversion"))
            return CurveType.EyelidSquint;

        // Droopy emotions
        if (lower.Contains("sadness") || lower.Contains("grief") || lower.Contains("melancholy") ||
            lower.Contains("dejection") || lower.Contains("concern") || lower.Contains("droop"))
            return CurveType.EyelidSquint;

        return CurveType.EyelidSquint;
    }

    /// <summary>Gets intensity multiplier for emotion eye curves.</summary>
    private static float GetEmotionEyeMultiplier(string lower)
    {
        if (lower.StartsWith("e_d_y_")) return 0.3f;
        if (lower.StartsWith("y_")) return 0.5f;
        return 0.4f;
    }

    /// <summary>Gets jaw contribution for emotion mouth/smile curves.</summary>
    private static float GetEmotionMouthJaw(string lower)
    {
        if (lower.Contains("laughter")) return 0.3f;
        if (lower.Contains("shock") || lower.Contains("mouthopen")) return 0.4f;
        if (lower.Contains("smile") || lower.Contains("smirk")) return -0.05f;
        return 0.05f;
    }

    /// <summary>
    /// Computes skinning matrices by sampling FaceFX curves at the given time.
    /// Optimized to avoid per-frame allocations and string operations.
    /// </summary>
    public Matrix4x4[] ComputeSkinningMatrices(FaceFXLivePreviewControl.IFaceFXBinary faceFX, FaceFXLine line, float time)
    {
        if (_bones == null || _skinningMatrices == null) return null;

        int numBones = _bones.Length;

        // Build curve mappings once per line (avoid per-frame string ops)
        if (!_curveMappingsBuilt || RuntimeHelpers.GetHashCode(line) != _lastLineHashCode)
        {
            BuildCurveMappings(faceFX, line);
        }

        // Sample curves and accumulate into bone channels (no Dictionary, no string ops)
        Vector3 headRotation = Vector3.Zero;
        Vector3 neckRotation = Vector3.Zero;
        Vector3 leftEyeRotation = Vector3.Zero;
        Vector3 rightEyeRotation = Vector3.Zero;
        float jawOpen = 0f;
        float blinkAmount = 0f;
        float eyelidSquint = 0f;
        float eyelidWide = 0f;
        float browRaise = 0f;
        float browLower = 0f;

        int pointIndex = 0;
        for (int i = 0; i < line.AnimationNames.Count; i++)
        {
            int numKeys = line.NumKeys[i];
            if (numKeys == 0) continue;

            float value = SampleCurve(line.Points, pointIndex, numKeys, time);
            pointIndex += numKeys;

            if (Math.Abs(value) < 0.001f) continue;

            float scaled = value * _curveMappings[i].Multiplier;
            switch (_curveMappings[i].Type)
            {
                case CurveType.HeadPitch: headRotation.X += scaled; break;
                case CurveType.HeadYaw: headRotation.Z += scaled; break;
                case CurveType.HeadRoll: headRotation.Y += scaled; break;
                case CurveType.NeckPitch: neckRotation.X += scaled; break;
                case CurveType.NeckYaw: neckRotation.Z += scaled; break;
                case CurveType.NeckRoll: neckRotation.Y += scaled; break;
                case CurveType.EyePitch: leftEyeRotation.X += scaled; rightEyeRotation.X += scaled; break;
                case CurveType.EyeYaw: leftEyeRotation.Z += scaled; rightEyeRotation.Z += scaled; break;
                case CurveType.LeftEyePitch: leftEyeRotation.X += scaled; break;
                case CurveType.LeftEyeYaw: leftEyeRotation.Z += scaled; break;
                case CurveType.RightEyePitch: rightEyeRotation.X += scaled; break;
                case CurveType.RightEyeYaw: rightEyeRotation.Z += scaled; break;
                case CurveType.Blink: blinkAmount += scaled; break;
                case CurveType.EyelidSquint: eyelidSquint += value * _curveMappings[i].Multiplier; break;
                case CurveType.EyelidWide: eyelidWide += value * _curveMappings[i].Multiplier; break;
                case CurveType.BrowRaise: browRaise += value * _curveMappings[i].Multiplier; break;
                case CurveType.BrowLower: browLower += value * _curveMappings[i].Multiplier; break;
                case CurveType.LipSync: jawOpen += _curveMappings[i].Multiplier * value; break;
                case CurveType.Ignored: break;
            }
        }

        // Copy bind pose local transforms (Array.Copy is very fast for blittable types)
        Array.Copy(_bindLocalTransforms, _localTransforms, numBones);

        // Apply accumulated rotations to specific bones
        if (_headBoneIndex >= 0 && headRotation != Vector3.Zero)
            ApplyRotationToBone(_headBoneIndex, headRotation * 0.05f);
        if (_neckBoneIndex >= 0 && neckRotation != Vector3.Zero)
            ApplyRotationToBone(_neckBoneIndex, neckRotation * 0.03f);
        if (_leftEyeBoneIndex >= 0 && leftEyeRotation != Vector3.Zero)
            ApplyRotationToBone(_leftEyeBoneIndex, leftEyeRotation * 0.08f);
        if (_rightEyeBoneIndex >= 0 && rightEyeRotation != Vector3.Zero)
            ApplyRotationToBone(_rightEyeBoneIndex, rightEyeRotation * 0.08f);
        if (_jawBoneIndex >= 0 && Math.Abs(jawOpen) > 0.001f)
            ApplyRotationToBone(_jawBoneIndex, new Vector3(Math.Clamp(jawOpen, -0.5f, 1f) * 0.15f, 0, 0));

        // Blink + eyelid squint (both close the lids)
        float totalBlink = Math.Clamp(blinkAmount + eyelidSquint * 0.5f, 0f, 1f);
        if (totalBlink > 0.001f)
        {
            float lidAngle = totalBlink * 0.3f;
            if (_leftUpperLidIndex >= 0)
                ApplyRotationToBone(_leftUpperLidIndex, new Vector3(lidAngle, 0, 0));
            if (_rightUpperLidIndex >= 0)
                ApplyRotationToBone(_rightUpperLidIndex, new Vector3(lidAngle, 0, 0));
        }
        // Eyelid wide (opens eyes wider — opposite direction from blink)
        if (eyelidWide > 0.001f)
        {
            float wideAngle = Math.Clamp(eyelidWide, 0f, 1f) * -0.15f;
            if (_leftUpperLidIndex >= 0)
                ApplyRotationToBone(_leftUpperLidIndex, new Vector3(wideAngle, 0, 0));
            if (_rightUpperLidIndex >= 0)
                ApplyRotationToBone(_rightUpperLidIndex, new Vector3(wideAngle, 0, 0));
        }

        // Brow: combine raise and lower
        float netBrow = browRaise - browLower;
        if (Math.Abs(netBrow) > 0.001f)
        {
            float browAngle = netBrow * 0.1f;
            if (_leftBrowBoneIndex >= 0)
                ApplyRotationToBone(_leftBrowBoneIndex, new Vector3(-browAngle, 0, 0));
            if (_rightBrowBoneIndex >= 0)
                ApplyRotationToBone(_rightBrowBoneIndex, new Vector3(-browAngle, 0, 0));
        }

        // Compute component-space transforms and skinning matrices
        for (int i = 0; i < numBones; i++)
        {
            var bone = _bones[i];
            if (bone.ParentIndex >= 0 && bone.ParentIndex < i)
                _animatedCS[i] = _localTransforms[i] * _animatedCS[bone.ParentIndex];
            else
                _animatedCS[i] = _localTransforms[i];

            _skinningMatrices[i] = _inverseBindPose[i] * _animatedCS[i];
        }

        return _skinningMatrices;
    }

    /// <summary>
    /// Applies a rotation to a bone using pre-cached bind rotation/position.
    /// Avoids Matrix4x4.Decompose which is expensive.
    /// </summary>
    private void ApplyRotationToBone(int boneIndex, Vector3 eulerRadians)
    {
        var pitchRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, eulerRadians.X);
        var rollRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, eulerRadians.Y);
        var yawRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, eulerRadians.Z);
        var deltaRot = Quaternion.Normalize(yawRot * pitchRot * rollRot);

        var newRot = Quaternion.Normalize(deltaRot * _bindRotations[boneIndex]);
        _localTransforms[boneIndex] = Matrix4x4.CreateFromQuaternion(newRot) * Matrix4x4.CreateTranslation(_bindPositions[boneIndex]);
    }

    /// <summary>
    /// Samples a single FaceFX curve at the given time using linear interpolation.
    /// </summary>
    private static float SampleCurve(List<FaceFXControlPoint> points, int startIndex, int numKeys, float time)
    {
        if (numKeys == 0) return 0f;
        if (numKeys == 1) return points[startIndex].weight;

        int lastKeyIndex = startIndex + numKeys - 1;

        if (time <= points[startIndex].time)
            return points[startIndex].weight;
        if (time >= points[lastKeyIndex].time)
            return points[lastKeyIndex].weight;

        for (int i = startIndex; i < lastKeyIndex; i++)
        {
            var p0 = points[i];
            var p1 = points[i + 1];

            if (time >= p0.time && time <= p1.time)
            {
                float dt = p1.time - p0.time;
                if (dt <= 0) return p0.weight;
                float t = (time - p0.time) / dt;
                return p0.weight + (p1.weight - p0.weight) * t;
            }
        }

        return 0f;
    }
}
