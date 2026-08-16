using System.IO;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public sealed record PlanetTextureLinkRequest(
    string InMemoryPath,
    string SourcePath,
    PlanetTextureCategory Categories);

public sealed record PlanetTexturePreviewSource(string CacheKey, byte[] Contents);

public sealed class PlanetTextureWorkflow(
    EditorSession session,
    EditSessionService edits,
    GalaxyMapTextureService textures)
{
    private readonly Dictionary<string, PlanetTexturePreviewSource> _previewSources =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkflowResult Stage(
        Planet planet,
        GalaxyMapModule target,
        PlanetTextureLinkRequest request,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace is null || target.IsReadOnly || target.IsBaseGame || target.FolderPath is null)
        {
            return WorkflowResult.Failure("Planet textures must be stored in a writable module.");
        }

        var inMemoryPath = request.InMemoryPath.Trim();
        if (inMemoryPath.Length == 0)
        {
            return WorkflowResult.Failure("Enter the texture's full in-memory path from its seek-free package.");
        }
        if (request.Categories == PlanetTextureCategory.None)
        {
            return WorkflowResult.Failure("Select at least one material category for this texture.");
        }

        try
        {
            var contents = File.ReadAllBytes(request.SourcePath);
            var fileName = Path.GetFileName(request.SourcePath);
            if (!GalaxyMapTextureService.IsSupportedImagePath(fileName) ||
                !textures.CanDecodeImageBytes(contents))
            {
                throw new InvalidOperationException("Choose a valid PNG, JPEG, BMP, GIF, or TIFF image.");
            }

            var links = target.PlanetTextureLinks.ToList();
            var existingIndex = links.FindIndex(link =>
                string.Equals(link.InMemoryPath, inMemoryPath, StringComparison.OrdinalIgnoreCase));
            var id = existingIndex >= 0 ? links[existingIndex].Id : Guid.NewGuid().ToString("N");
            var relativePath = existingIndex >= 0
                ? links[existingIndex].RelativePath
                : $"textures/Planet_{id}_{fileName}";
            var link = new PlanetTextureLink(id, inMemoryPath, relativePath, request.Categories);
            if (existingIndex >= 0)
            {
                links[existingIndex] = link;
            }
            else
            {
                links.Add(link);
            }

            var replacement = target.With(planetTextureLinks: links);
            var impact = ChangeImpact.For([GalaxyMapTable.Planet], [planet.Key], isStructural: false);
            var originalActiveTag = workspace.ActiveModule?.Tag;
            return edits.ExecuteSessionMutation(new SessionMutationRequest(
                () =>
                {
                    workspace.ReplaceModule(target, replacement);
                    edits.StageFile(new PendingFileWrite(
                        replacement.Tag,
                        relativePath,
                        contents,
                        "Planet texture",
                        planet.Key,
                        Guid.NewGuid().ToString("N")));
                    workspace.SetActiveModule(replacement);
                    edits.MarkMetadataDirty(replacement);
                },
                () =>
                {
                    var current = workspace.Modules.FirstOrDefault(module => ReferenceEquals(module, replacement));
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
                presentation with { SelectionKey = planet.Key },
                $"linked Planet texture {inMemoryPath} in {replacement.Tag}."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or ArgumentException)
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    public PlanetTexturePreviewSource? ResolvePreview(string inMemoryPath)
    {
        if (session.Workspace is null || string.IsNullOrWhiteSpace(inMemoryPath))
        {
            return null;
        }

        foreach (var layer in session.Workspace.Layers.Reverse())
        {
            var module = layer.Module;
            var link = module.PlanetTextureLinks.FirstOrDefault(candidate =>
                string.Equals(candidate.InMemoryPath, inMemoryPath.Trim(), StringComparison.OrdinalIgnoreCase));
            if (link is null)
            {
                continue;
            }

            var pending = session.Changes.PendingFiles.LastOrDefault(file =>
                string.Equals(file.ModuleTag, module.Tag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(file.RelativePath, link.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (pending is not null)
            {
                var cacheKey = $"pending:{module.Tag}:{link.Id}:{pending.CacheKey}";
                if (!_previewSources.TryGetValue(cacheKey, out var source))
                {
                    source = new PlanetTexturePreviewSource(cacheKey, pending.Contents);
                    _previewSources[cacheKey] = source;
                }
                return source;
            }

            var fullPath = GalaxyMapTextureService.ResolveModuleTexturePath(module, link.RelativePath);
            if (fullPath is not null && File.Exists(fullPath))
            {
                var cacheKey = $"file:{module.Tag}:{link.Id}";
                if (!_previewSources.TryGetValue(cacheKey, out var source))
                {
                    source = new PlanetTexturePreviewSource(cacheKey, File.ReadAllBytes(fullPath));
                    _previewSources[cacheKey] = source;
                }
                return source;
            }
        }

        return null;
    }

    public WorkflowResult Unlink(
        Planet planet,
        GalaxyMapModule target,
        string linkId,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace is null || target.IsReadOnly || target.IsBaseGame)
        {
            return WorkflowResult.Failure("Planet textures can be unlinked only from a writable module.");
        }

        var link = target.PlanetTextureLinks.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, linkId, StringComparison.OrdinalIgnoreCase));
        if (link is null)
        {
            return WorkflowResult.Failure("The linked Planet texture is no longer present in that module.");
        }

        var replacement = target.With(planetTextureLinks: target.PlanetTextureLinks
            .Where(candidate => !string.Equals(candidate.Id, link.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray());
        var impact = ChangeImpact.For([GalaxyMapTable.Planet], [planet.Key], isStructural: false);
        return edits.ExecuteSessionMutation(new SessionMutationRequest(
            () =>
            {
                workspace.ReplaceModule(target, replacement);
                edits.RemovePendingFile(target.Tag, link.RelativePath);
                edits.MarkMetadataDirty(replacement);
            },
            () =>
            {
                var current = workspace.Modules.FirstOrDefault(module => ReferenceEquals(module, replacement));
                if (current is not null)
                {
                    workspace.ReplaceModule(replacement, target);
                }
            },
            impact,
            presentation with { SelectionKey = planet.Key },
            $"unlinked Planet texture {link.InMemoryPath} from {target.Tag}. Its preview file was left untouched."));
    }

    public static GalaxyMapModule? CreateRenamedReference(
        GalaxyMapModule module,
        string oldPath,
        string newPath,
        out string? error)
    {
        error = null;
        var normalizedOld = oldPath.Trim();
        var normalizedNew = newPath.Trim();
        if (normalizedOld.Length == 0 || normalizedNew.Length == 0 ||
            string.Equals(normalizedOld, normalizedNew, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var index = module.PlanetTextureLinks.ToList().FindIndex(link =>
            string.Equals(link.InMemoryPath, normalizedOld, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }
        if (module.PlanetTextureLinks.Where((_, candidateIndex) => candidateIndex != index).Any(link =>
                string.Equals(link.InMemoryPath, normalizedNew, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"A linked Planet texture already uses the in-memory path '{normalizedNew}'.";
            return null;
        }

        var links = module.PlanetTextureLinks.ToList();
        links[index] = links[index] with { InMemoryPath = normalizedNew };
        return module.With(planetTextureLinks: links);
    }
}
