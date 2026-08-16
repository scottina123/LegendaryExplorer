using System.Windows;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.Views;
using LE1GalaxyMapEditor.Workflows.Ports;
using LE1GalaxyMapEditor.Workflows.Queries;
using Microsoft.Win32;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Presentation;

public sealed class WpfEditorDialogs(
    Func<GalaxyMapRow, IReadOnlyList<GalaxyMapModule>, GalaxyMapModule?>? editTargetSelector = null,
    Func<string, bool>? confirmAction = null,
    Func<PlanetShaderNameRequest, string?>? shaderNameSelector = null,
    Func<CommitPreview, bool>? commitReviewAction = null,
    Func<ClusterLabelRequest, string?>? clusterLabelSelector = null,
    GalaxyMapTlkService? tlkService = null,
    Func<GalaxyMapModule?>? activeModuleProvider = null) : IEditorDialogs
{
    public MELocalization? ConfigureBaseGameLocale(MELocalization currentLocale)
    {
        var dialog = new BaseGameSettingsWindow(currentLocale)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.SelectedLocale : null;
    }

    public ModuleSetupResult? ConfigureModule(ModuleSetupDialogRequest request)
    {
        var dialog = new ModuleSetupWindow(
            request.SelectParentFolder,
            request.FolderPath,
            request.SuggestedName,
            request.SuggestedTag,
            request.SuggestedReservations,
            request.SuggestedLoadOrder,
            request.IsEditing,
            request.SuggestedColor,
            request.CanSetActive,
            request.IsActive,
            request.SetActiveAction,
            request.UnlinkAction,
            request.IdentityReadOnly,
            request.SuggestedTlkLocale,
            request.SuggestedResourcePackages,
            request.ForgetAction)
        {
            Owner = Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public string? PickModuleFolder()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a galaxy-map PCC",
            Filter = "LE1 package files (*.pcc)|*.pcc|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickNewModulePackage()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create galaxy-map PCC in a DLC CookedPCConsole folder",
            Filter = "LE1 package files (*.pcc)|*.pcc",
            DefaultExt = ".pcc",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "GXM_GalaxyMap.pcc"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public PlanetCreationRequest? CreatePlanet()
    {
        if (Application.Current?.MainWindow is not { } owner)
        {
            var defaults = new Planet();
            GalaxyMapDefaults.ApplyPlanetTemplate(defaults, PlanetCreationTemplate.GenericPlanet);
            return new PlanetCreationRequest(
                PlanetCreationTemplate.GenericPlanet,
                GalaxyMapDefaults.DefaultPlanetName(PlanetCreationTemplate.GenericPlanet),
                0,
                defaults.Scale,
                null);
        }

        var dialog = new PlanetCreationWindow(
            tlkService,
            activeModuleProvider?.Invoke()?.TlkLocale ?? MELocalization.INT) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public CloneContentRequest? ConfigureClone(GalaxyMapRow source, int suggestedId, string suggestedLabel)
    {
        var dialog = new CloneContentWindow(source, suggestedId, suggestedLabel)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public GalaxyMapModule? ChooseEditTarget(
        GalaxyMapRow row,
        IReadOnlyList<GalaxyMapModule> candidates,
        GalaxyMapModule? activeModule)
    {
        if (editTargetSelector is not null)
        {
            return editTargetSelector(row, candidates);
        }

        if (ActiveOwner() is { IsLoaded: true } owner)
        {
            var dialog = new ModuleTargetWindow(candidates, activeModule) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.SelectedModule : null;
        }

        return activeModule is not null && candidates.Contains(activeModule)
            ? activeModule
            : candidates.FirstOrDefault();
    }

    public string? ChoosePlanetShaderName(PlanetShaderNameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (shaderNameSelector is not null)
        {
            return shaderNameSelector(request);
        }

        if (ActiveOwner() is { IsLoaded: true } owner)
        {
            var dialog = new PlanetShaderNameWindow(request) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.ShaderName : null;
        }

        return request.SuggestedName;
    }

    public string? ChooseClusterLabel(ClusterLabelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (clusterLabelSelector is not null)
        {
            return clusterLabelSelector(request);
        }

        if (ActiveOwner() is { IsLoaded: true } owner)
        {
            var dialog = new ClusterLabelWindow(request) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.ClusterLabel : null;
        }

        return request.SuggestedLabel;
    }

    public string? PickClusterTexture()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Cluster background texture",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public LandableDestinationRequest? ConfigureLandableDestination(LandableDestinationDefaults defaults)
    {
        var dialog = new LandableDestinationWindow(
            defaults.MapName,
            defaults.StartPoint,
            defaults.EventName,
            defaults.ButtonLabel,
            defaults.CanAddPlotPlanet,
            tlkService,
            activeModuleProvider?.Invoke()?.TlkLocale ?? MELocalization.INT)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public MoveDestinationOption? ChooseMoveDestination(
        GalaxyMapRow source,
        IReadOnlyList<MoveDestinationOption> options)
    {
        var dialog = new MoveDestinationWindow(source, options)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public bool Confirm(string message)
    {
        if (confirmAction is not null)
        {
            return confirmAction(message);
        }

        if (Application.Current?.MainWindow is not { } owner)
        {
            return false;
        }

        var dialog = new ConfirmationWindow(
            "Confirm staged change",
            message,
            "Confirm",
            "Cancel")
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true && dialog.Choice == ConfirmationChoice.Primary;
    }

    public bool ReviewCommit(CommitPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (commitReviewAction is not null)
        {
            return commitReviewAction(preview);
        }

        if (Application.Current?.MainWindow is not { } owner)
        {
            return false;
        }

        var dialog = new CommitPreviewWindow(preview) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private static Window? ActiveOwner()
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
           ?? Application.Current?.MainWindow;
}
