using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public sealed record RelayWorkflowState(
    GalaxyMapRowKey? Source,
    int? ReplacementRowId,
    string? TargetModuleTag)
{
    public static RelayWorkflowState Idle { get; } = new(null, null, null);
    public bool IsActive => Source is not null;
}

public sealed class RelayWorkflow(EditorSession session, EditSessionService edits)
{
    public RelayWorkflowState State { get; private set; } = RelayWorkflowState.Idle;
    public event EventHandler? StateChanged;

    public Cluster? Source => State.Source is { } key ? session.Workspace?.Resolve(key) as Cluster : null;

    public WorkflowResult BeginCreation(Cluster source)
    {
        if (session.Document is not { } document || session.Workspace?.ActiveLayer is null)
        {
            return WorkflowResult.Failure("Create or open a writable module before adding Relay connections.");
        }

        if (!document.TryGetRelayCode(source, out _, out var error))
        {
            return WorkflowResult.Failure(error);
        }

        SetState(new RelayWorkflowState(source.Key, null, session.ActiveModule?.Tag));
        return WorkflowResult.Success(Prompt, source.Key, NavigationTarget.Galaxy);
    }

    public WorkflowResult BeginRedirect(Cluster source, RelayConnection relay, GalaxyMapModule targetModule)
    {
        if (session.Document is not { } document || session.Workspace?.ActiveLayer is null)
        {
            return WorkflowResult.Failure("Create or open a writable module before redirecting Relay connections.");
        }

        if (!document.TryGetRelayCode(source, out var sourceCode, out var error))
        {
            return WorkflowResult.Failure(error);
        }

        if (relay.StartClusterEncoded != sourceCode && relay.EndClusterEncoded != sourceCode)
        {
            return WorkflowResult.Failure($"Relay row {relay.RowId} is not connected to {source.DisplayName}.");
        }

        session.Workspace.SetActiveModule(targetModule);
        SetState(new RelayWorkflowState(source.Key, relay.RowId, targetModule.Tag));
        return WorkflowResult.Success(Prompt, source.Key, NavigationTarget.Galaxy);
    }

    public WorkflowResult AcceptTarget(Cluster target, HistoryPresentationState presentation)
    {
        var source = Source;
        if (source is null || session.Document is null || session.Workspace is null)
        {
            return WorkflowResult.Failure("No Relay edit is active.");
        }

        if (State.ReplacementRowId is { } relayRowId)
        {
            var relay = session.Document.Relays.FirstOrDefault(row => row.RowId == relayRowId);
            return relay is null
                ? WorkflowResult.Failure($"Relay row {relayRowId} is no longer available.")
                : CompleteRedirect(source, relay, target, presentation);
        }

        return CompleteCreation(source, target, presentation);
    }

    public GalaxyMapRowKey? Cancel()
    {
        var source = State.Source;
        SetState(RelayWorkflowState.Idle);
        return source;
    }

    public WorkflowResult Remove(
        RelayConnection relay,
        Cluster selectedCluster,
        HistoryPresentationState presentation)
    {
        if (session.Workspace?.ActiveLayer is not { } layer)
        {
            return WorkflowResult.Failure("Create or open a writable module before removing Relay connections.");
        }

        var physical = layer.Find(relay.Key);
        var chain = session.Workspace.GetOverrideChain(relay.Key);
        if (physical is null || chain.Count != 1)
        {
            return WorkflowResult.Failure(
                "This Relay comes from BASEGAME or a mounted source module. The 2DA partial format has no verified " +
                "deletion tombstone, so the editor will not invent one and risk corrupting the runtime table.");
        }

        var destination = ReferenceEquals(relay.StartCluster, selectedCluster)
            ? relay.EndCluster?.DisplayName ?? $"code {relay.EndClusterEncoded}"
            : relay.StartCluster?.DisplayName ?? $"code {relay.StartClusterEncoded}";
        return edits.ExecuteMutation(new EditMutationRequest(
            [relay.Key],
            [GalaxyMapTable.Relay],
            () => layer.Remove(physical),
            presentation,
            $"Removed Relay connection from {selectedCluster.DisplayName} to {destination}.",
            IsStructural: true));
    }

    public bool CanBreak(RelayConnection relay)
        => session.Workspace?.ActiveLayer?.Find(relay.Key) is not null &&
           session.Workspace.GetOverrideChain(relay.Key).Count == 1;

    public string Prompt => Source is not { } source
        ? string.Empty
        : State.ReplacementRowId is null
            ? $"Select another Cluster to connect to {source.DisplayName}."
            : $"Select the new destination for Relay row {State.ReplacementRowId} from {source.DisplayName}.";

    private WorkflowResult CompleteRedirect(
        Cluster source,
        RelayConnection relay,
        Cluster target,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace!;
        var targetModule = workspace.Modules.FirstOrDefault(module =>
            string.Equals(module.Tag, State.TargetModuleTag, StringComparison.OrdinalIgnoreCase));
        if (targetModule is null)
        {
            return WorkflowResult.Failure("The Relay target module is no longer mounted.");
        }

        workspace.SetActiveModule(targetModule);
        var layer = workspace.ActiveLayer!;
        if (ReferenceEquals(source, target))
        {
            return new WorkflowResult(false, "A Relay connection cannot loop back to the same Cluster.");
        }

        var document = session.Document!;
        if (!document.TryGetRelayCode(source, out var sourceCode, out var error) ||
            !document.TryGetRelayCode(target, out var targetCode, out error))
        {
            return new WorkflowResult(false, error);
        }

        if (document.Relays.Any(candidate => candidate.RowId != relay.RowId &&
                ((candidate.StartClusterEncoded == sourceCode && candidate.EndClusterEncoded == targetCode) ||
                 (candidate.StartClusterEncoded == targetCode && candidate.EndClusterEncoded == sourceCode))))
        {
            return new WorkflowResult(false,
                $"{source.DisplayName} and {target.DisplayName} already have a Relay connection.");
        }

        var physical = layer.Find(relay.Key) is null
            ? GalaxyMapRowCloner.CloneForOverride(relay, targetModule)
            : GalaxyMapRowCloner.Clone(relay);
        string dirtyColumn;
        var replacement = (RelayConnection)physical;
        if (replacement.StartClusterEncoded == sourceCode)
        {
            replacement.EndClusterEncoded = targetCode;
            dirtyColumn = "EndCluster";
        }
        else if (replacement.EndClusterEncoded == sourceCode)
        {
            replacement.StartClusterEncoded = targetCode;
            dirtyColumn = "StartCluster";
        }
        else
        {
            return WorkflowResult.Failure(
                $"Relay row {relay.RowId} no longer contains {source.DisplayName}'s endpoint code.");
        }

        physical.Origin = new GalaxyMapRowOrigin(targetModule, workspace.GetOverrideChain(relay.Key).Any(
            candidate => !string.Equals(candidate.Origin?.ModuleTag, targetModule.Tag, StringComparison.OrdinalIgnoreCase)));
        GalaxyMapRowAuthoring.EnsureSnapshot(physical).MarkDirty(dirtyColumn);
        var result = edits.ExecuteMutation(new EditMutationRequest(
            [relay.Key],
            [GalaxyMapTable.Relay],
            () => layer.Upsert(physical),
            presentation with { SelectionKey = source.Key, Navigation = NavigationTarget.Galaxy },
            $"Redirected Relay row {relay.RowId} from {source.DisplayName} to {target.DisplayName}.",
            IsStructural: true));
        if (result.Succeeded)
        {
            SetState(RelayWorkflowState.Idle);
        }

        return result;
    }

    private WorkflowResult CompleteCreation(
        Cluster source,
        Cluster target,
        HistoryPresentationState presentation)
    {
        var document = session.Document!;
        var workspace = session.Workspace!;
        if (workspace.ActiveLayer is not { } layer || workspace.ActiveModule is not { } activeModule)
        {
            return WorkflowResult.Failure("A writable active module is required for this Relay.");
        }

        if (ReferenceEquals(source, target))
        {
            return new WorkflowResult(false, "A Cluster cannot have a Relay connection to itself.");
        }

        if (!document.TryGetRelayCode(source, out var startCode, out var error) ||
            !document.TryGetRelayCode(target, out var endCode, out error))
        {
            return new WorkflowResult(false, error);
        }

        if (document.Relays.Any(relay =>
                (relay.StartClusterEncoded == startCode && relay.EndClusterEncoded == endCode) ||
                (relay.StartClusterEncoded == endCode && relay.EndClusterEncoded == startCode)))
        {
            return new WorkflowResult(false,
                $"{source.DisplayName} and {target.DisplayName} already have a Relay connection.");
        }

        try
        {
            var relay = new RelayConnection
            {
                RowId = new ModuleIdAllocator(workspace).NextAvailable(activeModule, GalaxyMapTable.Relay),
                StartClusterEncoded = startCode,
                EndClusterEncoded = endCode
            };
            GalaxyMapRowAuthoring.PrepareNewRow(layer, relay);
            var result = edits.ExecuteMutation(new EditMutationRequest(
                [relay.Key],
                [GalaxyMapTable.Relay],
                () => layer.Upsert(relay),
                presentation with { SelectionKey = source.Key, Navigation = NavigationTarget.Galaxy },
                $"Added Relay connection from {source.DisplayName} to {target.DisplayName}.",
                IsStructural: true));
            if (result.Succeeded)
            {
                SetState(RelayWorkflowState.Idle);
            }

            return result;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    private void SetState(RelayWorkflowState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
