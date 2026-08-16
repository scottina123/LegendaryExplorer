using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Workflows.Queries;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Workflows.Ports;

public sealed record ModuleSetupDialogRequest(
    bool SelectParentFolder,
    string FolderPath,
    string SuggestedName,
    string SuggestedTag,
    ModuleIdReservations SuggestedReservations,
    int SuggestedLoadOrder,
    bool IsEditing = false,
    ModuleColor SuggestedColor = ModuleColor.Cyan,
    bool CanSetActive = false,
    bool IsActive = false,
    Func<bool>? SetActiveAction = null,
    Func<bool>? UnlinkAction = null,
    bool IdentityReadOnly = false,
    MELocalization SuggestedTlkLocale = MELocalization.INT,
    IReadOnlyList<string>? SuggestedResourcePackages = null,
    Func<bool>? ForgetAction = null);

public sealed record LandableDestinationDefaults(
    string MapName,
    string StartPoint,
    string EventName,
    int? ButtonLabel,
    bool CanAddPlotPlanet);

public sealed record ModuleSetupResult(
    string Name,
    string Tag,
    ModuleColor Color,
    string FolderPath,
    ModuleIdReservations Reservations,
    int LoadOrder,
    MELocalization TlkLocale = MELocalization.INT,
    IReadOnlyList<string>? ResourcePackagePaths = null);

public sealed record LandableDestinationRequest(
    string MapName,
    string StartPoint,
    string Event,
    int? ButtonLabel,
    bool AddPlotPlanet);

public sealed record PlanetCreationRequest(
    PlanetCreationTemplate Template,
    string NameText,
    int Name,
    double Scale,
    LandableDestinationRequest? Destination);

public sealed record PlanetShaderNameRequest(
    string PlanetName,
    int PlanetRowId,
    GalaxyMapModule TargetModule,
    string SuggestedName,
    Func<string, string?> Validate);

public sealed record ClusterLabelRequest(
    string SuggestedLabel,
    IReadOnlyList<string> MountedLabels,
    Func<string, string?> Validate);

public sealed record CloneContentRequest(
    int RowId,
    string Label,
    int Name,
    string NameText,
    bool CloneChildren);

public sealed record MoveDestinationOption(
    int RowId,
    string Label,
    string Detail,
    string CurrentLabel,
    string ResultingLabel)
{
    public override string ToString() => Label;
}

public interface IEditorDialogs
{
    MELocalization? ConfigureBaseGameLocale(MELocalization currentLocale);
    ModuleSetupResult? ConfigureModule(ModuleSetupDialogRequest request);
    string? PickModuleFolder();
    string? PickNewModulePackage();
    PlanetCreationRequest? CreatePlanet();
    CloneContentRequest? ConfigureClone(GalaxyMapRow source, int suggestedId, string suggestedLabel);
    GalaxyMapModule? ChooseEditTarget(
        GalaxyMapRow row,
        IReadOnlyList<GalaxyMapModule> candidates,
        GalaxyMapModule? activeModule);
    string? ChoosePlanetShaderName(PlanetShaderNameRequest request);
    string? ChooseClusterLabel(ClusterLabelRequest request);
    string? PickClusterTexture();
    LandableDestinationRequest? ConfigureLandableDestination(LandableDestinationDefaults defaults);
    MoveDestinationOption? ChooseMoveDestination(
        GalaxyMapRow source,
        IReadOnlyList<MoveDestinationOption> options);
    bool ReviewCommit(CommitPreview preview);
    bool Confirm(string message);
}
