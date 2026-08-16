using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public sealed class PlanetDesignerSession
{
    public PlanetDesignerSession(Planet planet)
    {
        ArgumentNullException.ThrowIfNull(planet);
        Key = planet.Key;
        ModuleTag = planet.Origin?.ModuleTag ?? GalaxyMapModule.BaseGameTag;
        DisplayName = planet.DisplayName;
        Original = PlanetAppearanceCodec.Decode(planet);
        Draft = Original.Clone();
        IsNewPlanet = planet.CsvSnapshot?.SourceRowNumber == 0;
    }

    public GalaxyMapRowKey Key { get; }
    public string ModuleTag { get; private set; }
    public string DisplayName { get; }
    public PlanetAppearance Original { get; private set; }
    public PlanetAppearance Draft { get; }
    public bool IsNewPlanet { get; private set; }
    public bool HasChanges => PlanetAppearanceCodec.ChangedColumns(Original, Draft).Count > 0;

    public void AcceptAppliedDraft(string moduleTag)
    {
        ModuleTag = moduleTag;
        Original = Draft.Clone();
        IsNewPlanet = false;
    }

    public void Reload(Planet planet)
    {
        ModuleTag = planet.Origin?.ModuleTag ?? GalaxyMapModule.BaseGameTag;
        var current = PlanetAppearanceCodec.Decode(planet);
        Original = current.Clone();
        Draft.ReplaceFrom(current);
    }
}

public sealed class PlanetDesignerWorkflow(EditorSession session, EditSessionService edits)
{
    public PlanetDesignerSession Open(Planet planet)
    {
        if (!PlanetAppearanceCodec.IsAppearanceCapable(planet))
        {
            throw new InvalidOperationException("The Planet Designer is available only for planet material rows.");
        }

        return new PlanetDesignerSession(planet);
    }

    public WorkflowResult Apply(
        PlanetDesignerSession designer,
        HistoryPresentationState presentation,
        GalaxyMapModule? targetModule = null)
    {
        ArgumentNullException.ThrowIfNull(designer);
        var workspace = session.Workspace;
        if (workspace is null)
        {
            return WorkflowResult.Failure("Select a writable module before applying a Planet appearance.", designer.Key);
        }

        var sourceLayer = workspace.Layers.FirstOrDefault(candidate =>
            string.Equals(candidate.Module.Tag, designer.ModuleTag, StringComparison.OrdinalIgnoreCase));
        var sourcePlanet = sourceLayer?.Find(designer.Key) as Planet ?? workspace.Resolve(designer.Key) as Planet;
        if (sourcePlanet is null)
        {
            return WorkflowResult.Failure("The Planet row is no longer present in the workspace.", designer.Key);
        }

        var layer = sourceLayer is { Module.IsReadOnly: false, Module.IsBaseGame: false }
            ? sourceLayer
            : targetModule is null
                ? workspace.ActiveLayer
                : workspace.ModuleLayers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Module.Tag, targetModule.Tag, StringComparison.OrdinalIgnoreCase));
        if (layer is null || layer.Module.IsReadOnly || layer.Module.IsBaseGame)
        {
            return WorkflowResult.Failure("Select a writable module before applying a Planet appearance.", designer.Key);
        }
        if (!ReferenceEquals(workspace.ActiveLayer, layer))
        {
            workspace.SetActiveModule(layer.Module);
        }

        var changedColumns = PlanetAppearanceCodec.ChangedColumns(designer.Original, designer.Draft);
        if (changedColumns.Count == 0 && !designer.IsNewPlanet)
        {
            return WorkflowResult.Success("No Planet appearance changes to apply.", designer.Key);
        }

        var shaderValidation = PlanetShaderNameValidator.Validate(
            workspace,
            designer.Key,
            designer.Draft,
            layer.Module.Tag);
        if (!shaderValidation.IsValid)
        {
            return WorkflowResult.Failure(shaderValidation.Message, designer.Key);
        }

        if (changedColumns.Count == 0)
        {
            return WorkflowResult.Success("The Planet appearance is already current.", designer.Key);
        }

        var replacement = layer.Find(designer.Key) is Planet physical
            ? (Planet)GalaxyMapRowCloner.Clone(physical)
            : (Planet)GalaxyMapRowCloner.CloneForOverride(sourcePlanet, layer.Module);
        foreach (var column in changedColumns)
        {
            replacement.SetExtraField(column, designer.Draft[column]);
            GalaxyMapRowAuthoring.EnsureSnapshot(replacement).MarkDirty(column);
        }

        var result = edits.ExecuteMutation(new EditMutationRequest(
            [designer.Key],
            [GalaxyMapTable.Planet],
            () => layer.Upsert(replacement),
            presentation with { SelectionKey = designer.Key },
            $"Updated the Planet appearance for {designer.DisplayName}.",
            IsStructural: false));
        if (result.Succeeded)
        {
            designer.AcceptAppliedDraft(layer.Module.Tag);
        }

        return result;
    }
}
