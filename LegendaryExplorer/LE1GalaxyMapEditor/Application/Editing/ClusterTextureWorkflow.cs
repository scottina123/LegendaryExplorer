using System.IO;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public sealed record ClusterTextureSource(
    byte[]? PendingContents,
    string? CacheKey,
    GalaxyMapModule? Module,
    int? LinkedClusterRowId,
    string Background);

public sealed class ClusterTextureWorkflow(
    EditorSession session,
    EditSessionService edits,
    GalaxyMapTextureService textures)
{
    public WorkflowResult Stage(
        Cluster cluster,
        GalaxyMapModule target,
        string sourcePath,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace is null || target.IsReadOnly || target.IsBaseGame || target.FolderPath is null)
        {
            return WorkflowResult.Failure("Cluster textures must be stored in a writable module.");
        }

        try
        {
            var contents = File.ReadAllBytes(sourcePath);
            var fileName = Path.GetFileName(sourcePath);
            if (!GalaxyMapTextureService.IsSupportedImagePath(fileName) ||
                !textures.CanDecodeImageBytes(contents))
            {
                throw new InvalidOperationException("Choose a valid PNG, JPEG, BMP, GIF, or TIFF image.");
            }

            var relativePath = $"textures/Cluster_{cluster.RowId}_{fileName}";
            var links = new Dictionary<int, string>(target.ClusterTextureLinks)
            {
                [cluster.RowId] = relativePath
            };
            var replacement = target.With(clusterTextureLinks: links);
            var impact = ChangeImpact.For([GalaxyMapTable.Cluster], [cluster.Key], isStructural: false);
            var originalActiveTag = workspace.ActiveModule?.Tag;
            return edits.ExecuteSessionMutation(new SessionMutationRequest(
                () =>
                {
                    workspace.ReplaceModule(target, replacement);
                    edits.StageFile(new PendingFileWrite(
                        replacement.Tag,
                        relativePath,
                        contents,
                        "Cluster texture",
                        cluster.Key,
                        Guid.NewGuid().ToString("N")));
                    workspace.SetActiveModule(replacement);
                    edits.MarkMetadataDirty(replacement);
                },
                () =>
                {
                    var current = workspace.Modules.FirstOrDefault(module =>
                        ReferenceEquals(module, replacement));
                    if (current is not null)
                    {
                        workspace.ReplaceModule(replacement, target);
                    }
                    if (workspace.Modules.FirstOrDefault(module =>
                            string.Equals(module.Tag, originalActiveTag, StringComparison.OrdinalIgnoreCase)) is
                        { IsReadOnly: false } originalActive)
                    {
                        workspace.SetActiveModule(originalActive);
                    }
                },
                impact,
                presentation with { SelectionKey = cluster.Key },
                $"module texture {fileName} for {cluster.DisplayName} in {replacement.Tag}."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or ArgumentException)
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    public ClusterTextureSource ResolveSource(Cluster cluster, GalaxyMapDocument? document)
    {
        if (session.Workspace is not null)
        {
            foreach (var layer in session.Workspace.Layers.Reverse())
            {
                var module = layer.Module;
                if (module.IsBaseGame)
                {
                    continue;
                }

                if (TryGetPending(module.Tag, cluster.RowId, out var pending))
                {
                    return new ClusterTextureSource(
                        pending.Contents,
                        $"pending:{module.Tag}:{cluster.RowId}:{pending.CacheKey}",
                        module,
                        cluster.RowId,
                        cluster.Background);
                }

                if (module.ClusterTextureLinks.ContainsKey(cluster.RowId))
                {
                    return new ClusterTextureSource(null, null, module, cluster.RowId, cluster.Background);
                }

                foreach (var linkedClusterId in module.ClusterTextureLinks.Keys)
                {
                    if (document?.ClustersByRowId.GetValueOrDefault(linkedClusterId) is not { } linkedCluster ||
                        !string.Equals(linkedCluster.Background, cluster.Background, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryGetPending(module.Tag, linkedClusterId, out var sharedPending))
                    {
                        return new ClusterTextureSource(
                            sharedPending.Contents,
                            $"pending:{module.Tag}:{linkedClusterId}:{sharedPending.CacheKey}",
                            module,
                            linkedClusterId,
                            cluster.Background);
                    }

                    return new ClusterTextureSource(null, null, module, linkedClusterId, cluster.Background);
                }
            }
        }

        return new ClusterTextureSource(null, null, null, null, cluster.Background);
    }

    private bool TryGetPending(string moduleTag, int clusterRowId, out PendingFileWrite pending)
    {
        pending = session.Changes.PendingFiles.FirstOrDefault(file =>
            string.Equals(file.ModuleTag, moduleTag, StringComparison.OrdinalIgnoreCase) &&
            file.RelatedRow == new GalaxyMapRowKey(GalaxyMapTable.Cluster, clusterRowId))!;
        return pending is not null;
    }
}
