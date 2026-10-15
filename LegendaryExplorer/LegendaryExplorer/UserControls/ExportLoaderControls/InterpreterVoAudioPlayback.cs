using System;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class InterpreterExportLoader
{
    /// <summary>Allows a host to reuse its own audio player for line previews.</summary>
    public RelayCommand VoAudioPreviewCommand
    {
        get => (RelayCommand)GetValue(VoAudioPreviewCommandProperty);
        set => SetValue(VoAudioPreviewCommandProperty, value);
    }

    public static readonly DependencyProperty VoAudioPreviewCommandProperty = DependencyProperty.Register(
        nameof(VoAudioPreviewCommand), typeof(RelayCommand), typeof(InterpreterExportLoader), new PropertyMetadata(null));

    public RelayCommand PlayVoAudioCommand { get; private set; }
    public RelayCommand PlayMaleVoAudioCommand { get; private set; }
    public RelayCommand PlayFemaleVoAudioCommand { get; private set; }
    public RelayCommand StopVoAudioPreviewCommand { get; private set; }

    private Soundpanel voAudioPreviewPlayer;
    private PackageCache voAudioPreviewPackageCache;
    private ContextMenu voAudioMenu;

    private void LoadVoAudioPlaybackCommands()
    {
        PlayMaleVoAudioCommand = CreateVoAudioPlaybackCommand(isFemale: false);
        PlayFemaleVoAudioCommand = CreateVoAudioPlaybackCommand(isFemale: true);
        PlayVoAudioCommand = new RelayCommand(ChooseVoAudio, parameter =>
            parameter is Button { DataContext: UPropertyTreeViewEntry node }
            && (PlayMaleVoAudioCommand.CanExecute(node) || PlayFemaleVoAudioCommand.CanExecute(node)));
        StopVoAudioPreviewCommand = new RelayCommand(_ => ClearVoAudioPreview());
    }

    private RelayCommand CreateVoAudioPlaybackCommand(bool isFemale) => new(
        parameter =>
        {
            if (GetVoTrackAudioNavigationEntry(parameter as UPropertyTreeViewEntry, isFemale, stream: true) is { } audio)
            {
                if (VoAudioPreviewCommand is { } command)
                {
                    if (command.CanExecute(audio))
                    {
                        command.Execute(audio);
                    }
                }
                else
                {
                    PreviewVoTrackAudio(audio);
                }
            }
        },
        parameter => GetVoTrackAudioNavigationEntry(parameter as UPropertyTreeViewEntry, isFemale, stream: true) is { } audio
                     && (VoAudioPreviewCommand == null || VoAudioPreviewCommand.CanExecute(audio)));

    private void ChooseVoAudio(object parameter)
    {
        if (parameter is not Button { DataContext: UPropertyTreeViewEntry node } button)
        {
            return;
        }

        IEntry male = GetVoTrackAudioNavigationEntry(node, isFemale: false, stream: true);
        IEntry female = GetVoTrackAudioNavigationEntry(node, isFemale: true, stream: true);
        if (male == null && female == null)
        {
            return;
        }

        CloseVoAudioMenu();
        if (male == null || female == null || ReferenceEquals(male, female))
        {
            (male != null ? PlayMaleVoAudioCommand : PlayFemaleVoAudioCommand).Execute(node);
            return;
        }

        voAudioMenu = new ContextMenu { PlacementTarget = button };
        voAudioMenu.Items.Add(new MenuItem
        {
            Header = "Play male audio", Command = PlayMaleVoAudioCommand, CommandParameter = node
        });
        voAudioMenu.Items.Add(new MenuItem
        {
            Header = "Play female audio", Command = PlayFemaleVoAudioCommand, CommandParameter = node
        });
        button.ContextMenu = voAudioMenu;
        voAudioMenu.IsOpen = true;
    }

    /// <summary>
    /// Previews a resolved line without changing the host editor's selection.
    /// </summary>
    private void PreviewVoTrackAudio(IEntry audio)
    {
        ClearVoAudioPreview();
        VoAudioPreviewBorder.Visibility = Visibility.Visible;
        VoAudioPreviewDescription.Text = audio.InstancedFullPath;
        VoAudioPreviewDescription.ToolTip = audio.InstancedFullPath;
        try
        {
            ExportEntry audioExport = audio as ExportEntry;
            if (audio is ImportEntry import)
            {
                voAudioPreviewPackageCache = new PackageCache();
                string localization = import.FileRef.Localization == MELocalization.None
                    ? "INT" : import.FileRef.Localization.ToString();
                EntryImporter.TryResolveImport(import, out audioExport, cache: voAudioPreviewPackageCache, localization: localization);
            }

            if (audioExport?.ClassName != "WwiseStream")
            {
                SetVoAudioPreviewStatus("Could not resolve this line's audio stream.");
                return;
            }

            if (voAudioPreviewPlayer == null)
            {
                voAudioPreviewPlayer = new Soundpanel
                {
                    MiniPlayerMode = true, PlayBackOnlyMode = true, GenerateWaveformGraph = false
                };
                VoAudioPreviewHost.Content = voAudioPreviewPlayer;
            }

            voAudioPreviewPlayer.LoadExport(audioExport);
            if (voAudioPreviewPlayer.CurrentLoadedExport == null || !voAudioPreviewPlayer.StartOrPausePlaying())
            {
                SetVoAudioPreviewStatus("Could not play this audio. Check that its audio data and AFC file are available.");
            }
        }
        catch (Exception exception)
        {
            voAudioPreviewPlayer?.StopPlaying();
            SetVoAudioPreviewStatus($"Could not play this audio: {exception.Message}");
        }
    }

    private void SetVoAudioPreviewStatus(string message)
    {
        VoAudioPreviewStatus.Text = message;
        VoAudioPreviewStatus.Visibility = Visibility.Visible;
    }

    private void CloseVoAudioMenu()
    {
        if (voAudioMenu != null)
        {
            voAudioMenu.IsOpen = false;
            if (voAudioMenu.PlacementTarget is Button button && ReferenceEquals(button.ContextMenu, voAudioMenu))
            {
                button.ClearValue(ContextMenuProperty);
            }
            voAudioMenu.PlacementTarget = null;
            voAudioMenu.Items.Clear();
            voAudioMenu = null;
        }
    }

    private void ClearVoAudioPreview()
    {
        CloseVoAudioMenu();
        voAudioPreviewPlayer?.StopPlaying();
        voAudioPreviewPlayer?.UnloadExport();
        voAudioPreviewPackageCache?.Dispose();
        voAudioPreviewPackageCache = null;
        VoAudioPreviewBorder.Visibility = Visibility.Collapsed;
        VoAudioPreviewDescription.Text = string.Empty;
        VoAudioPreviewDescription.ToolTip = null;
        VoAudioPreviewStatus.Text = string.Empty;
        VoAudioPreviewStatus.Visibility = Visibility.Collapsed;
    }

    private void InterpreterVoAudio_Unloaded(object sender, RoutedEventArgs e) => ClearVoAudioPreview();

    private void DisposeVoAudioPreview()
    {
        Unloaded -= InterpreterVoAudio_Unloaded;
        ClearVoAudioPreview();
        voAudioPreviewPlayer?.Dispose();
        voAudioPreviewPlayer = null;
        VoAudioPreviewHost.Content = null;
    }
}
