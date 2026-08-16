using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

public sealed record PlanetShaderValidationResult(bool IsValid, string Message)
{
    public static PlanetShaderValidationResult Valid { get; } = new(true, string.Empty);
}

public static class PlanetShaderNameValidator
{
    public static PlanetShaderValidationResult Validate(
        GalaxyMapWorkspace workspace,
        GalaxyMapRowKey target,
        PlanetAppearance appearance,
        string targetModuleTag)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(appearance);
        if (string.Equals(targetModuleTag, GalaxyMapModule.BaseGameTag, StringComparison.OrdinalIgnoreCase))
        {
            return PlanetShaderValidationResult.Valid;
        }

        var candidate = appearance.Shader.Trim();
        if (candidate.Length == 0)
        {
            return new(false, "Enter a unique Shader name before applying this appearance.");
        }

        var matches = workspace.Layers
            .SelectMany(layer => layer.Planets.Select(planet => (Layer: layer, Planet: planet)))
            .Where(item => item.Planet.Key != target ||
                           !string.Equals(item.Layer.Module.Tag, targetModuleTag, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(
                item.Planet.ExtraFields.GetValueOrDefault("Shader")?.Trim(),
                candidate,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            return PlanetShaderValidationResult.Valid;
        }

        var match = matches[0];
        return new(false,
            $"Shader '{candidate}' is already used by {match.Planet.DisplayName} " +
            $"(Planet row {match.Planet.RowId}, {match.Layer.Module.Tag}). " +
            "Third-party module Planets must use unique Shader names.");
    }
}
