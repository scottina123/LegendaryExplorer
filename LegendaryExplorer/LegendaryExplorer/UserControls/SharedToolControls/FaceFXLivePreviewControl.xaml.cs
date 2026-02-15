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
        NeckPitch,
        EyePitch, EyeYaw,
        Blink, EyebrowRaise,
        LipSync
    }
    private struct CurveMapping
    {
        public CurveType Type;
        public float JawContribution; // for LipSync type
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
    /// Called once when the line changes.
    /// </summary>
    private void BuildCurveMappings(FaceFXLivePreviewControl.IFaceFXBinary faceFX, FaceFXLine line)
    {
        int count = line.AnimationNames.Count;
        _curveMappings = new CurveMapping[count];

        for (int i = 0; i < count; i++)
        {
            if (line.AnimationNames[i] >= faceFX.Names.Count)
            {
                _curveMappings[i] = new CurveMapping { Type = CurveType.LipSync, JawContribution = 0 };
                continue;
            }

            string name = faceFX.Names[line.AnimationNames[i]];
            string lower = name.ToLowerInvariant();

            if (lower.Contains("orientation_head_") || lower.Contains("emphasis_head_"))
            {
                if (lower.Contains("pitch")) _curveMappings[i].Type = CurveType.HeadPitch;
                else if (lower.Contains("yaw")) _curveMappings[i].Type = CurveType.HeadYaw;
                else if (lower.Contains("roll")) _curveMappings[i].Type = CurveType.HeadRoll;
            }
            else if (lower.Contains("neck") && lower.Contains("pitch"))
            {
                _curveMappings[i].Type = CurveType.NeckPitch;
            }
            else if (lower.Contains("gaze_eye_"))
            {
                _curveMappings[i].Type = lower.Contains("pitch") ? CurveType.EyePitch : CurveType.EyeYaw;
            }
            else if (lower.Contains("blink"))
            {
                _curveMappings[i].Type = CurveType.Blink;
            }
            else if (lower.Contains("eyebrow") || (lower.Contains("brow") && lower.Contains("raise")))
            {
                _curveMappings[i].Type = CurveType.EyebrowRaise;
            }
            else
            {
                _curveMappings[i].Type = CurveType.LipSync;
                _curveMappings[i].JawContribution = GetJawContribution(lower);
            }
        }

        _curveMappingsBuilt = true;
        _lastLineHashCode = RuntimeHelpers.GetHashCode(line);
    }

    private static float GetJawContribution(string lowerCurveName)
    {
        if (lowerCurveName.Contains("jaw+") || lowerCurveName.Contains("open")) return 1.0f;
        if (lowerCurveName.Contains("jaw-")) return -0.5f;
        if (lowerCurveName.Contains("m_ah") || lowerCurveName.Contains("m_oh") ||
            lowerCurveName.Contains("m_ow") || lowerCurveName.Contains("m_eh") ||
            lowerCurveName.Contains("m_open")) return 0.8f;
        if (lowerCurveName.Contains("m_ee") || lowerCurveName.Contains("m_oo")) return 0.5f;
        if (lowerCurveName.Contains("m_m") || lowerCurveName.Contains("m_n") ||
            lowerCurveName.Contains("m_fv") || lowerCurveName.Contains("m_th")) return 0.2f;
        return 0.4f;
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
        float browRaise = 0f;

        int pointIndex = 0;
        for (int i = 0; i < line.AnimationNames.Count; i++)
        {
            int numKeys = line.NumKeys[i];
            if (numKeys == 0) continue;

            float value = SampleCurve(line.Points, pointIndex, numKeys, time);
            pointIndex += numKeys;

            if (Math.Abs(value) < 0.001f) continue;

            switch (_curveMappings[i].Type)
            {
                case CurveType.HeadPitch: headRotation.X += value; break;
                case CurveType.HeadYaw: headRotation.Z += value; break;
                case CurveType.HeadRoll: headRotation.Y += value; break;
                case CurveType.NeckPitch: neckRotation.X += value; break;
                case CurveType.EyePitch: leftEyeRotation.X += value; rightEyeRotation.X += value; break;
                case CurveType.EyeYaw: leftEyeRotation.Z += value; rightEyeRotation.Z += value; break;
                case CurveType.Blink: blinkAmount += value; break;
                case CurveType.EyebrowRaise: browRaise += value; break;
                case CurveType.LipSync: jawOpen += _curveMappings[i].JawContribution * value; break;
            }
        }

        // Copy bind pose local transforms (Array.Copy is very fast for blittable types)
        Array.Copy(_bindLocalTransforms, _localTransforms, numBones);

        // Apply accumulated rotations to specific bones
        if (_headBoneIndex >= 0 && headRotation != Vector3.Zero)
            ApplyRotationToBone(_headBoneIndex, headRotation * 0.2f);
        if (_neckBoneIndex >= 0 && neckRotation != Vector3.Zero)
            ApplyRotationToBone(_neckBoneIndex, neckRotation * 0.15f);
        if (_leftEyeBoneIndex >= 0 && leftEyeRotation != Vector3.Zero)
            ApplyRotationToBone(_leftEyeBoneIndex, leftEyeRotation * 0.25f);
        if (_rightEyeBoneIndex >= 0 && rightEyeRotation != Vector3.Zero)
            ApplyRotationToBone(_rightEyeBoneIndex, rightEyeRotation * 0.25f);
        if (_jawBoneIndex >= 0 && Math.Abs(jawOpen) > 0.001f)
            ApplyRotationToBone(_jawBoneIndex, new Vector3(Math.Clamp(jawOpen, 0f, 1f) * 0.4f, 0, 0));
        if (Math.Abs(blinkAmount) > 0.001f)
        {
            float lidAngle = Math.Clamp(blinkAmount, 0f, 1f) * 0.5f;
            if (_leftUpperLidIndex >= 0)
                ApplyRotationToBone(_leftUpperLidIndex, new Vector3(lidAngle, 0, 0));
            if (_rightUpperLidIndex >= 0)
                ApplyRotationToBone(_rightUpperLidIndex, new Vector3(lidAngle, 0, 0));
        }
        if (Math.Abs(browRaise) > 0.001f)
        {
            float browAngle = browRaise * 0.25f;
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
