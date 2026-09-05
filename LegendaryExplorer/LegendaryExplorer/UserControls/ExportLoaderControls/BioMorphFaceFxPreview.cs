using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Audio;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using NAudio.Vorbis;
using NAudio.Wave;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class MeshRenderer
{
    private IMEPackage morphFaceFxPackage;
    private MorphFaceFxPlayer morphFaceFxPlayer;
    private MorphFaceFxPlayer morphHairFaceFxPlayer;
    private MeshBone[] morphFaceFxHairSkeleton;
    private WaveOutEvent morphVoiceOutput;
    private WaveStream morphVoiceReader;
    private Stream morphVoiceStream;
    private double morphVoiceClockOffset;
    private bool morphFaceFxPlaying;
    private bool morphFaceFxPoseActive;
    private bool morphFaceFxLoading;
    private bool selectingMorphFaceFx;
    private int morphFaceFxLoadVersion;

    public ObservableCollectionExtended<MorphFaceFxLine> MorphFaceFxLines { get; } = [];
    public ObservableCollectionExtended<MorphFaceFxRig> MorphFaceFxRigs { get; } = [];
    public IEnumerable<MorphFaceFxLine> FilteredMorphFaceFxLines => MorphFaceFxLines.Where(line => line.Matches(MorphFaceFxSearchText));
    public string MorphFaceFxLineCount => $"{FilteredMorphFaceFxLines.Count()} / {MorphFaceFxLines.Count} lines";
    public bool CanLoadMorphFaceFx => HasMorphEditorData && !morphFaceFxLoading;
    public bool CanPlayMorphFaceFx => morphFaceFxPlayer != null && !morphFaceFxLoading;
    public string MorphFaceFxPlayText => morphFaceFxPlaying ? "Pause" : "Play";
    public double MorphFaceFxStart => Math.Min(0, morphFaceFxPlayer?.StartTime ?? 0);
    public double MorphFaceFxEnd => Math.Max(morphFaceFxPlayer?.EndTime ?? 0, morphVoiceReader?.TotalTime.TotalSeconds ?? 0);
    public string MorphFaceFxPositionText => $"{MorphFaceFxPosition:F2} / {MorphFaceFxEnd:F2} s";

    private string morphFaceFxStatus = "Load a PCC to browse all FaceFX lines. Text uses the game's loaded TLKs.";
    public string MorphFaceFxStatus
    {
        get => morphFaceFxStatus;
        private set => SetProperty(ref morphFaceFxStatus, value);
    }

    private string morphFaceFxSource;
    public string MorphFaceFxSource
    {
        get => morphFaceFxSource;
        private set => SetProperty(ref morphFaceFxSource, value);
    }

    private string morphFaceFxSearchText;
    public string MorphFaceFxSearchText
    {
        get => morphFaceFxSearchText;
        set
        {
            if (!SetProperty(ref morphFaceFxSearchText, value)) return;
            OnPropertyChanged(nameof(FilteredMorphFaceFxLines));
            OnPropertyChanged(nameof(MorphFaceFxLineCount));
        }
    }

    private MorphFaceFxLine selectedMorphFaceFxLine;
    public MorphFaceFxLine SelectedMorphFaceFxLine
    {
        get => selectedMorphFaceFxLine;
        set
        {
            if (SetProperty(ref selectedMorphFaceFxLine, value) && !selectingMorphFaceFx)
                PrepareMorphFaceFxLine();
        }
    }

    private MorphFaceFxRig selectedMorphFaceFxRig;
    public MorphFaceFxRig SelectedMorphFaceFxRig
    {
        get => selectedMorphFaceFxRig;
        set
        {
            if (SetProperty(ref selectedMorphFaceFxRig, value) && !selectingMorphFaceFx)
                PrepareMorphFaceFxLine();
        }
    }

    private double morphFaceFxPosition;
    public double MorphFaceFxPosition
    {
        get => morphFaceFxPosition;
        set
        {
            if (!CanPlayMorphFaceFx || !double.IsFinite(value)) return;
            value = Math.Clamp(value, MorphFaceFxStart, MorphFaceFxEnd);
            if (Math.Abs(value - morphFaceFxPosition) < 0.000001) return;
            morphFaceFxPosition = value;
            SeekMorphVoice();
            morphFaceFxPoseActive = true;
            ApplyMorphFaceFxPose();
            NotifyMorphFaceFxPlayback();
        }
    }

    private async void LoadMorphFaceFxPcc_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLoadMorphFaceFx) return;
        var dialog = AppDirectories.GetOpenPackageDialog();
        dialog.Title = "Load FaceFX lines from a PCC";
        if (DirectoryMemory.ShowDialog(dialog) != true) return;
        int version = ++morphFaceFxLoadVersion;
        MEGame game = CurrentLoadedExport.Game;
        morphFaceFxLoading = true;
        PauseMorphFaceFx();
        NotifyMorphFaceFxPlayback();
        MorphFaceFxStatus = "Loading FaceFX assets and TLK text…";
        IMEPackage loadedPackage = null;
        try
        {
            var result = await Task.Run(() =>
            {
                loadedPackage = MEPackageHandler.OpenMEPackage(dialog.FileName);
                if (loadedPackage.Game != game)
                    throw new InvalidOperationException($"Choose a {game} package to match this morph.");
                var rigs = new List<MorphFaceFxRig>();
                var errors = new List<string>();
                var lines = MorphFaceFxLine.ReadPackage(loadedPackage, rigs, errors);
                string cooked = MEDirectories.GetCookedPath(game);
                string rigPath = Directory.Exists(cooked)
                    ? Directory.EnumerateFiles(cooked, "BIOG_FaceFX_Assets.*").FirstOrDefault() : null;
                if (rigPath != null && !rigPath.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var rigPackage = MEPackageHandler.OpenMEPackage(rigPath);
                        ReadMorphFaceFxRigs(rigPackage, rigs, errors);
                    }
                    catch (Exception ex) { errors.Add($"Game FaceFX rigs: {ex.Message}"); }
                }
                return (lines, rigs, errors);
            });
            if (version != morphFaceFxLoadVersion) return;
            ClearMorphFaceFxSelection();
            morphFaceFxPackage?.Dispose();
            morphFaceFxPackage = loadedPackage;
            loadedPackage = null;
            selectingMorphFaceFx = true;
            try
            {
                MorphFaceFxLines.ReplaceAll(result.lines);
                MorphFaceFxRigs.ReplaceAll(result.rigs);
                SelectedMorphFaceFxRig = ChooseMorphFaceFxRig(result.rigs);
                SelectedMorphFaceFxLine = null;
                MorphFaceFxSource = dialog.FileName;
                MorphFaceFxSearchText = null;
            }
            finally { selectingMorphFaceFx = false; }
            OnPropertyChanged(nameof(FilteredMorphFaceFxLines));
            OnPropertyChanged(nameof(MorphFaceFxLineCount));
            MorphFaceFxStatus = $"Loaded {result.lines.Count} lines from {result.lines.Select(line => line.SourceExport).Distinct().Count()} FaceFX exports."
                                + (result.errors.Count > 0 ? $"\nSkipped/unavailable: {string.Join("\n", result.errors)}" : "")
                                + (result.lines.Count > 0 ? "\nSelect a line to preview." : "");
        }
        catch (Exception ex)
        {
            if (version == morphFaceFxLoadVersion) MorphFaceFxStatus = $"Could not load FaceFX: {ex.Message}";
        }
        finally
        {
            loadedPackage?.Dispose();
            if (version == morphFaceFxLoadVersion)
            {
                morphFaceFxLoading = false;
                NotifyMorphFaceFxPlayback();
            }
        }
    }

    private static void ReadMorphFaceFxRigs(IMEPackage package, List<MorphFaceFxRig> rigs, List<string> errors)
    {
        foreach (var export in package.Exports.Where(export => export.ClassName == "FaceFXAsset" && !export.IsDefaultObject))
        {
            try
            {
                var asset = export.GetBinaryData<FaceFXAsset>();
                if (asset.RefBones.Count > 0 && asset.CompiledFaceGraph.Count > 0)
                    rigs.Add(new MorphFaceFxRig(export.ObjectName.Instanced, package.FilePath, asset));
            }
            catch (Exception ex) { errors.Add($"{export.ObjectName.Instanced}: {ex.Message}"); }
        }
    }

    private MorphFaceFxRig ChooseMorphFaceFxRig(IEnumerable<MorphFaceFxRig> rigs)
    {
        string headName = MorphBaseHeadExport?.ObjectName.Instanced ?? "";
        string preferred = headName.Contains("HMF", StringComparison.OrdinalIgnoreCase) ? "HumanFemale"
            : headName.Contains("HMM", StringComparison.OrdinalIgnoreCase) ? "HumanMale"
            : headName.Contains("ASA", StringComparison.OrdinalIgnoreCase) ? "Asari" : null;
        var boneNames = MorphBindSkeleton.Select(bone => bone.Name.Instanced).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rigs.OrderByDescending(rig => preferred != null && rig.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(rig => rig.Asset.RefBones.Count(bone => boneNames.Contains(rig.Asset.Names[bone.RefBone.BoneName])))
            .FirstOrDefault();
    }

    private void LoadMorphFaceFxRig_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLoadMorphFaceFx) return;
        var dialog = AppDirectories.GetOpenPackageDialog();
        dialog.Title = "Load FaceFX rigs";
        if (DirectoryMemory.ShowDialog(dialog) != true) return;
        try
        {
            using var package = MEPackageHandler.OpenMEPackage(dialog.FileName);
            if (package.Game != CurrentLoadedExport.Game)
                throw new InvalidOperationException("Choose a rig package from the same game as the morph.");
            var rigs = new List<MorphFaceFxRig>();
            var errors = new List<string>();
            ReadMorphFaceFxRigs(package, rigs, errors);
            foreach (var rig in rigs)
                MorphFaceFxRigs.Add(rig);
            if (rigs.Count > 0) SelectedMorphFaceFxRig = ChooseMorphFaceFxRig(rigs);
            else MorphFaceFxStatus = "No playable FaceFX rigs found. " + string.Join("\n", errors);
        }
        catch (Exception ex) { MorphFaceFxStatus = $"Could not load rigs: {ex.Message}"; }
    }

    private void PrepareMorphFaceFxLine()
    {
        ClearMorphFaceFxSelection();
        if (SelectedMorphFaceFxLine is not { } line || MorphPreviewSkeletalMesh == null) return;
        try
        {
            FaceFXAsset rig = SelectedMorphFaceFxRig?.Asset ?? line.Asset;
            if (rig?.RefBones is not { Count: > 0 } || rig.CompiledFaceGraph.Count == 0)
                throw new InvalidOperationException("Select a FaceFX rig, or load one from BIOG_FaceFX_Assets.pcc.");
            if (!rig.RefBones.Any(bone => MorphBindSkeleton.Any(meshBone =>
                    meshBone.Name.Instanced.Equals(rig.Names[bone.RefBone.BoneName], StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("This rig has no bones in common with the morph's base head.");
            // Asset lines can be previewed using another rig; their curve names belong to their source asset.
            FaceFXAnimSet animSet = line.AnimSet ?? new FaceFXAnimSet { Names = line.Asset.Names, Lines = line.Asset.Lines };
            morphFaceFxPlayer = new MorphFaceFxPlayer(MorphBindSkeleton, MorphPreviewSkeletalMesh.RefSkeleton, rig, animSet, line.Line);
            if (MorphHairBindSkeleton.Length > 0)
            {
                morphFaceFxHairSkeleton = CloneSkeleton(MorphHairBindSkeleton);
                morphHairFaceFxPlayer = new MorphFaceFxPlayer(MorphHairBindSkeleton, morphFaceFxHairSkeleton, rig, animSet, line.Line);
            }
            morphFaceFxPosition = MorphFaceFxStart;
            MorphFaceFxStatus = "Ready. Play the line or scrub the timeline.";
            try
            {
                ExportEntry voice = line.FindVoiceExport();
                if (voice != null)
                {
                    // Reuse Soundpanel's Wwise/AFC and ISACT resolution and decoding without creating another viewport.
                    using var decoder = new Soundpanel { PlayBackOnlyMode = true };
                    decoder.LoadExport(voice);
                    if (voice.ClassName == "SoundNodeWave" && decoder.ExportInfoListBox.SelectedItem == null)
                        decoder.ExportInfoListBox.SelectedItem = decoder.ExportInformationList.OfType<ISACTListBankChunk>().FirstOrDefault();
                    morphVoiceStream = decoder.GetPCMStream();
                    if (morphVoiceStream != null)
                    {
                        if (morphVoiceStream.CanSeek) morphVoiceStream.Position = 0;
                        morphVoiceReader = morphVoiceStream is OggWaveStream
                            ? new VorbisWaveReader(morphVoiceStream) : new WaveFileReader(morphVoiceStream);
                        morphVoiceOutput = new WaveOutEvent();
                        morphVoiceOutput.Init(morphVoiceReader);
                    }
                }
                if (morphVoiceOutput == null)
                    MorphFaceFxStatus = "Audio was not found or could not be decoded. Facial animation is available.";
            }
            catch (Exception ex)
            {
                DisposeMorphVoice();
                MorphFaceFxStatus = $"Facial animation is available; audio unavailable: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            morphFaceFxPlayer = null;
            MorphFaceFxStatus = $"Could not preview line: {ex.Message}";
        }
        NotifyMorphFaceFxPlayback();
    }

    private void PlayMorphFaceFx_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPlayMorphFaceFx) return;
        if (morphFaceFxPlaying) { PauseMorphFaceFx(); return; }
        if (morphFaceFxPosition >= MorphFaceFxEnd) morphFaceFxPosition = MorphFaceFxStart;
        morphFaceFxPlaying = true;
        morphFaceFxPoseActive = true;
        SeekMorphVoice();
        ApplyMorphFaceFxPose();
        NotifyMorphFaceFxPlayback();
    }

    private void PauseMorphFaceFx()
    {
        morphFaceFxPlaying = false;
        morphVoiceOutput?.Pause();
        NotifyMorphFaceFxPlayback();
    }

    private void StopMorphFaceFx_Click(object sender, RoutedEventArgs e)
    {
        PauseMorphFaceFx();
        morphFaceFxPoseActive = false;
        morphFaceFxPosition = MorphFaceFxStart;
        SeekMorphVoice();
        if (MorphPreviewSkeletalMesh != null && MeshContext.IsReady) UpdateMorphGeometryPreview();
        NotifyMorphFaceFxPlayback();
    }

    private void SeekMorphVoice()
    {
        if (morphVoiceReader == null || morphVoiceOutput == null) return;
        // Stop flushes buffered samples so seeking does not replay audio from the old position.
        morphVoiceOutput.Stop();
        double position = Math.Clamp(morphFaceFxPosition, 0, morphVoiceReader.TotalTime.TotalSeconds);
        morphVoiceReader.CurrentTime = TimeSpan.FromSeconds(position);
        morphVoiceClockOffset = position - (double)morphVoiceOutput.GetPosition() / morphVoiceReader.WaveFormat.AverageBytesPerSecond;
        if (morphFaceFxPlaying && morphFaceFxPosition >= 0 && position < morphVoiceReader.TotalTime.TotalSeconds)
            morphVoiceOutput.Play();
    }

    private void UpdateMorphFaceFxPreview(float timeStep)
    {
        if (!morphFaceFxPlaying || morphFaceFxPlayer == null) return;
        double previous = morphFaceFxPosition;
        morphFaceFxPosition = morphVoiceOutput?.PlaybackState == PlaybackState.Playing
            ? morphVoiceClockOffset + (double)morphVoiceOutput.GetPosition() / morphVoiceReader.WaveFormat.AverageBytesPerSecond
            : morphFaceFxPosition + timeStep;
        if (previous < 0 && morphFaceFxPosition >= 0) SeekMorphVoice();
        morphFaceFxPosition = Math.Min(morphFaceFxPosition, MorphFaceFxEnd);
        ApplyMorphFaceFxPose();
        if (morphFaceFxPosition >= MorphFaceFxEnd) PauseMorphFaceFx();
        OnPropertyChanged(nameof(MorphFaceFxPosition));
        OnPropertyChanged(nameof(MorphFaceFxPositionText));
    }

    private void ApplyMorphFaceFxPose()
    {
        if (morphFaceFxPlayer == null || !MeshContext.IsReady) return;
        try
        {
            morphFaceFxPlayer.SetCurrentTime((float)morphFaceFxPosition);
            UpdateMorphGeometryPreview(currentLodOnly: true);
        }
        catch (Exception ex)
        {
            PauseMorphFaceFx();
            morphFaceFxPoseActive = false;
            morphFaceFxPlayer = null;
            UpdateMorphGeometryPreview();
            MorphFaceFxStatus = $"Could not animate this line: {ex.Message}";
            NotifyMorphFaceFxPlayback();
        }
    }

    private void DisposeMorphVoice()
    {
        morphVoiceOutput?.Dispose();
        morphVoiceOutput = null;
        morphVoiceReader?.Dispose();
        morphVoiceReader = null;
        morphVoiceStream?.Dispose();
        morphVoiceStream = null;
    }

    private void ClearMorphFaceFxSelection()
    {
        PauseMorphFaceFx();
        DisposeMorphVoice();
        morphFaceFxPlayer = null;
        morphHairFaceFxPlayer = null;
        morphFaceFxHairSkeleton = null;
        bool restore = morphFaceFxPoseActive;
        morphFaceFxPoseActive = false;
        morphFaceFxPosition = 0;
        if (restore && MorphPreviewSkeletalMesh != null && MeshContext.IsReady) UpdateMorphGeometryPreview();
        NotifyMorphFaceFxPlayback();
    }

    private void UnloadMorphFaceFx()
    {
        ++morphFaceFxLoadVersion;
        morphFaceFxLoading = false;
        // The host may already have disposed its GPU previews while unloading.
        morphFaceFxPoseActive = false;
        ClearMorphFaceFxSelection();
        selectingMorphFaceFx = true;
        SelectedMorphFaceFxLine = null;
        SelectedMorphFaceFxRig = null;
        MorphFaceFxLines.ClearEx();
        MorphFaceFxRigs.ClearEx();
        selectingMorphFaceFx = false;
        morphFaceFxPackage?.Dispose();
        morphFaceFxPackage = null;
        MorphFaceFxSource = null;
        MorphFaceFxSearchText = null;
        MorphFaceFxStatus = "Load a PCC to browse all FaceFX lines. Text uses the game's loaded TLKs.";
        OnPropertyChanged(nameof(FilteredMorphFaceFxLines));
        OnPropertyChanged(nameof(MorphFaceFxLineCount));
    }

    private void NotifyMorphFaceFxPlayback()
    {
        OnPropertyChanged(nameof(CanLoadMorphFaceFx));
        OnPropertyChanged(nameof(CanPlayMorphFaceFx));
        OnPropertyChanged(nameof(MorphFaceFxStart));
        OnPropertyChanged(nameof(MorphFaceFxEnd));
        OnPropertyChanged(nameof(MorphFaceFxPosition));
        OnPropertyChanged(nameof(MorphFaceFxPositionText));
        OnPropertyChanged(nameof(MorphFaceFxPlayText));
    }
}
