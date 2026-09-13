using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Audio;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using NAudio.Vorbis;
using NAudio.Wave;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator;

public partial class FaceFXEmotionPreviewControl : NotifyPropertyChangedControlBase, IDisposable
{
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = new();
    private readonly PackageCache previewPackages = new();
    private MEGame game;
    private FaceFXGenerationDraft draft;
    private FaceFXAsset rig;
    private ExportEntry meshExport;
    private IMEPackage headPackage;
    private ExportEntry audioExport;
    private Func<Window, FaceFXAsset> chooseRig;
    private Stream voiceStream;
    private WaveStream voiceReader;
    private WaveOutEvent voiceOutput;
    private bool audioPrepared;
    private bool playing;
    private bool disposed;
    private double playStart;
    private double voiceClockOffset;
    private double position;
    private double duration;

    public double Position
    {
        get => position;
        set
        {
            if (!double.IsFinite(value)) return;
            double newPosition = Math.Clamp(value, 0, Duration);
            if (newPosition.Equals(position)) return;
            position = newPosition;
            SeekVoice();
            playStart = position;
            clock.Restart();
            UpdatePose();
        }
    }

    public double Duration
    {
        get => duration;
        private set => SetProperty(ref duration, value);
    }

    public string PositionText => $"{Position:F2} / {Duration:F2} s";
    public string PlayText => playing ? "Pause" : "Play";
    internal ExportEntry SelectedHeadMesh => meshExport;
    internal FaceFXAsset SelectedRig => rig;
    public string HeadMeshLabel => meshExport == null ? "Choose a mesh from the head PCC."
        : $"{Path.GetFileName(meshExport.FileRef.FilePath)}: {meshExport.ObjectName.Instanced}";
    public string RigLabel => rig?.Export?.ObjectName.Instanced ?? (rig == null ? "No rig selected" : "FaceFX rig");

    private string status = "Choose a head mesh, then preview an emotion.";
    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public FaceFXEmotionPreviewControl()
    {
        InitializeComponent();
        DataContext = this;
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += PlaybackTick;
    }

    internal void Initialize(MEGame targetGame, FaceFXGenerationDraft initialDraft, ExportEntry voice,
        double timelineDuration, FaceFXAsset actor, ExportEntry headMesh, Func<Window, FaceFXAsset> rigPicker)
    {
        game = targetGame;
        draft = initialDraft;
        audioExport = voice;
        rig = actor;
        meshExport = headMesh;
        chooseRig = rigPicker;
        Duration = Math.Max(0.02, timelineDuration);
        OnPropertyChanged(nameof(HeadMeshLabel));
        OnPropertyChanged(nameof(RigLabel));
    }

    private void Preview_Loaded(object sender, RoutedEventArgs e)
    {
        // The parent Loaded event precedes the viewport's Direct3D initialization.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (disposed || !IsLoaded) return;
            if (meshExport != null) LoadHeadMesh();
            LoadDraft();
        }));
    }

    internal void SetDraft(FaceFXGenerationDraft value)
    {
        Pause();
        draft = value;
        Duration = Math.Max(Duration, draft.Line.Points.Select(point => (double)point.time).DefaultIfEmpty().Max());
        LoadDraft();
    }

    private void LoadHeadMesh()
    {
        headPreview.LoadSkeletalMesh(meshExport);
        headPreview.SetCameraOrientation(-MathF.PI / 2, 0);
    }

    private void LoadDraft()
    {
        headPreview.ClearAnimation();
        if (draft == null) return;
        if (headPreview.CurrentMesh == null)
        {
            Status = "Choose a head mesh to see the animation. You can play the audio to set the timing.";
            return;
        }
        if (rig?.RefBones is not { Count: > 0 } || rig.CompiledFaceGraph.Count == 0)
        {
            Status = "Choose a FaceFX rig for this head to preview facial animation.";
            return;
        }
        if (!rig.RefBones.Any(bone => headPreview.CurrentMesh.RefSkeleton.Any(meshBone =>
                string.Equals(meshBone.Name.Instanced, rig.Names[bone.RefBone.BoneName], StringComparison.OrdinalIgnoreCase))))
        {
            Status = "The rig and head have no matching bones. Choose a rig for this head.";
            return;
        }
        headPreview.LoadFaceFxAnimation(rig, draft.AnimSet, draft.Line);
        // The audio timeline can outlast the last facial key.
        headPreview.AnimSliderMin = 0;
        headPreview.AnimSliderMax = Duration;
        UpdatePose();
        Status = "Ready. Play or scrub to preview the animation on this head.";
    }

    private void OpenHeadPcc_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDirectories.GetOpenPackageDialog();
        dialog.Title = "Open the PCC containing the head mesh";
        if (DirectoryMemory.ShowDialog(dialog) == true) SelectHeadMesh(dialog.FileName);
    }

    private void ChooseHeadMesh_Click(object sender, RoutedEventArgs e)
    {
        string path = headPackage?.FilePath ?? meshExport?.FileRef.FilePath;
        if (path == null)
        {
            string bundledHeads = Path.Combine(AppContext.BaseDirectory, "WwiseTestData", "LE3Morphs.pcc");
            if (File.Exists(bundledHeads)) path = bundledHeads;
        }
        if (path == null) OpenHeadPcc_Click(sender, e);
        else SelectHeadMesh(path);
    }

    private void SelectHeadMesh(string path)
    {
        try
        {
            var package = previewPackages.GetCachedPackage(path);
            var selected = EntrySelector.GetEntry<ExportEntry>(Window.GetWindow(this), package,
                "Choose the head mesh to animate", export => export.ClassName == "SkeletalMesh" && !export.IsDefaultObject);
            if (selected == null) return;
            Pause();
            headPackage = package;
            meshExport = selected;
            LoadHeadMesh();
            if (rig == null)
            {
                var rigs = package.Exports.Where(export => export.ClassName == "FaceFXAsset" && !export.IsDefaultObject).ToList();
                if (rigs.Count == 1) rig = rigs[0].GetBinaryData<FaceFXAsset>();
            }
            OnPropertyChanged(nameof(HeadMeshLabel));
            OnPropertyChanged(nameof(RigLabel));
            LoadDraft();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Head Preview");
        }
    }

    private void ChooseRig_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        if (chooseRig != null)
        {
            rig = chooseRig(Window.GetWindow(this)) ?? rig;
        }
        else
        {
            var dialog = AppDirectories.GetOpenPackageDialog();
            dialog.Title = "Open a PCC containing a FaceFX rig";
            if (DirectoryMemory.ShowDialog(dialog) != true) return;
            try
            {
                var package = previewPackages.GetCachedPackage(dialog.FileName);
                if (package.Game != game) throw new InvalidOperationException("Choose a FaceFX rig from the same game.");
                var selected = EntrySelector.GetEntry<ExportEntry>(Window.GetWindow(this), package,
                    "Choose a FaceFX rig", export => export.ClassName == "FaceFXAsset" && !export.IsDefaultObject);
                if (selected != null) rig = selected.GetBinaryData<FaceFXAsset>();
            }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "FaceFX Rig"); }
        }
        OnPropertyChanged(nameof(RigLabel));
        LoadDraft();
    }

    private void PrepareAudio()
    {
        if (audioPrepared) return;
        audioPrepared = true;
        if (audioExport == null)
        {
            Status = "No linked audio was found. The head animation can still be previewed.";
            return;
        }
        try
        {
            using var decoder = new Soundpanel { PlayBackOnlyMode = true };
            decoder.LoadExport(audioExport);
            if (audioExport.ClassName == "SoundNodeWave" && decoder.ExportInfoListBox.SelectedItem == null)
                decoder.ExportInfoListBox.SelectedItem = decoder.ExportInformationList.OfType<ISACTListBankChunk>().FirstOrDefault();
            voiceStream = decoder.GetPCMStream();
            if (voiceStream == null) throw new InvalidOperationException("The linked audio could not be decoded.");
            if (voiceStream.CanSeek) voiceStream.Position = 0;
            voiceReader = voiceStream is OggWaveStream ? new VorbisWaveReader(voiceStream) : new WaveFileReader(voiceStream);
            voiceOutput = new WaveOutEvent();
            voiceOutput.Init(voiceReader);
            Duration = Math.Max(Duration, voiceReader.TotalTime.TotalSeconds);
        }
        catch (Exception ex)
        {
            DisposeVoice();
            Status = $"Head animation is available; audio could not be played: {ex.Message}";
        }
    }

    internal void PlayFrom(double time)
    {
        Pause();
        Position = time;
        Play();
    }

    private void Play()
    {
        PrepareAudio();
        if (Position >= Duration) Position = 0;
        playing = true;
        playStart = Position;
        clock.Restart();
        SeekVoice();
        timer.Start();
        OnPropertyChanged(nameof(PlayText));
    }

    private void Pause()
    {
        playing = false;
        timer.Stop();
        clock.Stop();
        voiceOutput?.Pause();
        OnPropertyChanged(nameof(PlayText));
    }

    private void SeekVoice()
    {
        if (voiceReader == null || voiceOutput == null) return;
        voiceOutput.Stop();
        double time = Math.Clamp(Position, 0, voiceReader.TotalTime.TotalSeconds);
        voiceReader.CurrentTime = TimeSpan.FromSeconds(time);
        voiceClockOffset = time - (double)voiceOutput.GetPosition() / voiceReader.WaveFormat.AverageBytesPerSecond;
        if (playing && time < voiceReader.TotalTime.TotalSeconds) voiceOutput.Play();
    }

    private void PlaybackTick(object sender, EventArgs e)
    {
        position = Math.Min(Duration, voiceOutput?.PlaybackState == PlaybackState.Playing
            ? voiceClockOffset + (double)voiceOutput.GetPosition() / voiceReader.WaveFormat.AverageBytesPerSecond
            : playStart + clock.Elapsed.TotalSeconds);
        UpdatePose();
        if (Position >= Duration) Pause();
    }

    private void UpdatePose()
    {
        headPreview.AnimSliderValue = Position;
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(PositionText));
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (playing) Pause(); else Play();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        Position = 0;
    }

    private void DisposeVoice()
    {
        voiceOutput?.Dispose();
        voiceOutput = null;
        voiceReader?.Dispose();
        voiceReader = null;
        voiceStream?.Dispose();
        voiceStream = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Pause();
        timer.Tick -= PlaybackTick;
        DisposeVoice();
        headPreview.Dispose();
        previewPackages.Dispose();
    }
}
