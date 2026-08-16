using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LE1GalaxyMapEditor.Models;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Services;

public sealed class GalaxyMapModuleProfileStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GalaxyMapModuleProfileStore(string? profilesDirectory = null)
    {
        ProfilesDirectory = string.IsNullOrWhiteSpace(profilesDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LE1GalaxyMapEditor",
                "modules")
            : Path.GetFullPath(profilesDirectory);
    }

    public string ProfilesDirectory { get; }

    public static string Identity(string dlcTag, string galaxyMapPackage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dlcTag);
        var relative = NormalizeRelativePath(galaxyMapPackage);
        var identity = $"{dlcTag.Trim().ToUpperInvariant()}|{relative.ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public GalaxyMapModuleProfile? Find(string dlcTag, string galaxyMapPackage)
    {
        var profileId = Identity(dlcTag, galaxyMapPackage);
        var path = ProfilePath(profileId);
        return File.Exists(path) ? Load(profileId) : null;
    }

    public GalaxyMapModuleProfile Load(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var path = ProfilePath(profileId);
        if (!File.Exists(path))
        {
            throw new GalaxyMapLoadException($"Module profile '{profileId}' does not exist.");
        }

        try
        {
            using var stream = File.OpenRead(path);
            var dto = JsonSerializer.Deserialize<ProfileDto>(stream, Options)
                ?? throw new GalaxyMapLoadException($"Module profile '{profileId}' is empty.");
            if (dto.SchemaVersion != CurrentSchemaVersion)
            {
                throw new GalaxyMapLoadException(
                    $"Module profile '{profileId}' uses unsupported schema version {dto.SchemaVersion}.");
            }

            if (string.IsNullOrWhiteSpace(dto.DlcTag) ||
                string.IsNullOrWhiteSpace(dto.LastKnownDlcPath) ||
                string.IsNullOrWhiteSpace(dto.GalaxyMapPackage))
            {
                throw new GalaxyMapLoadException(
                    $"Module profile '{profileId}' is missing its DLC/package identity.");
            }
            var dlcTag = dto.DlcTag;
            var lastKnownDlcPath = dto.LastKnownDlcPath;
            var galaxyMapPackage = dto.GalaxyMapPackage;
            if (dto.ModuleColor == ModuleColor.BaseGameBlue)
            {
                throw new GalaxyMapLoadException(
                    $"Module profile '{profileId}' uses the reserved BASEGAME colour.");
            }

            var expectedId = Identity(dlcTag, galaxyMapPackage);
            if (!string.Equals(profileId, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                throw new GalaxyMapLoadException(
                    $"Module profile '{profileId}' does not match its DLC/package identity.");
            }
            if (!GalaxyMapModule.SupportedTlkLocales.Contains(dto.TlkLocale))
            {
                throw new GalaxyMapLoadException(
                    $"Module profile '{profileId}' has unsupported TLK locale '{dto.TlkLocale}'.");
            }

            return new GalaxyMapModuleProfile(
                expectedId,
                dlcTag.Trim(),
                Path.GetFullPath(lastKnownDlcPath),
                NormalizeRelativePath(galaxyMapPackage),
                dto.ModuleColor,
                dto.TlkLocale,
                (dto.ResourcePackages ?? []).Select(NormalizeRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                new ModuleIdReservations(
                    ToRange(dto.Reservations?.Cluster),
                    ToRange(dto.Reservations?.System),
                    ToRange(dto.Reservations?.Planet),
                    ToRange(dto.Reservations?.Map),
                    ToRange(dto.Reservations?.Relay)),
                string.IsNullOrWhiteSpace(dto.DisplayName) ? null : dto.DisplayName.Trim());
        }
        catch (GalaxyMapLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new GalaxyMapLoadException($"Could not read module profile '{profileId}': {exception.Message}", exception);
        }
    }

    public IReadOnlyList<GalaxyMapModuleProfile> LoadAll()
    {
        if (!Directory.Exists(ProfilesDirectory))
        {
            return [];
        }
        return Directory.EnumerateFiles(ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Load(Path.GetFileNameWithoutExtension(path)))
            .ToArray();
    }

    public void Save(GalaxyMapModuleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var expectedId = Identity(profile.DlcTag, profile.GalaxyMapPackage);
        if (!string.Equals(profile.ProfileId, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The module profile ID does not match its DLC/package identity.");
        }
        if (profile.ModuleColor == ModuleColor.BaseGameBlue)
        {
            throw new InvalidOperationException("BASEGAME blue is reserved and cannot be used by a module profile.");
        }
        if (!GalaxyMapModule.SupportedTlkLocales.Contains(profile.TlkLocale))
        {
            throw new InvalidOperationException($"Unsupported TLK locale '{profile.TlkLocale}'.");
        }

        var dto = new ProfileDto
        {
            SchemaVersion = CurrentSchemaVersion,
            DlcTag = profile.DlcTag,
            DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? null : profile.DisplayName.Trim(),
            LastKnownDlcPath = Path.GetFullPath(profile.LastKnownDlcPath),
            GalaxyMapPackage = NormalizeRelativePath(profile.GalaxyMapPackage),
            ModuleColor = profile.ModuleColor,
            TlkLocale = profile.TlkLocale,
            ResourcePackages = profile.ResourcePackages.Select(NormalizeRelativePath).ToList(),
            Reservations = new ReservationDto
            {
                Cluster = FromRange(profile.Reservations.Cluster),
                System = FromRange(profile.Reservations.System),
                Planet = FromRange(profile.Reservations.Planet),
                Map = FromRange(profile.Reservations.Map),
                Relay = FromRange(profile.Reservations.Relay)
            }
        };
        var json = JsonSerializer.Serialize(dto, Options) + "\r\n";
        AtomicFileWriter.Write(ProfilePath(expectedId), new UTF8Encoding(false).GetBytes(json));
    }

    public void Delete(string profileId)
    {
        var path = ProfilePath(profileId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string ProfilePath(string profileId)
        => Path.Combine(ProfilesDirectory, profileId + ".json");

    private static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("Package paths in profiles must be DLC-relative.", nameof(path));
        }
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException("Package paths in profiles must be canonical DLC-relative paths.", nameof(path));
        }
        return normalized;
    }

    private static RowIdRange? ToRange(RangeDto? range)
        => range is null ? null : new RowIdRange(range.Start, range.End);

    private static RangeDto? FromRange(RowIdRange? range)
        => range is null ? null : new RangeDto { Start = range.Value.Start, End = range.Value.End };

    private sealed class ProfileDto
    {
        public int SchemaVersion { get; set; }
        public string? DlcTag { get; set; }
        public string? DisplayName { get; set; }
        public string? LastKnownDlcPath { get; set; }
        public string? GalaxyMapPackage { get; set; }
        public ModuleColor ModuleColor { get; set; }
        public MELocalization TlkLocale { get; set; } = MELocalization.INT;
        public List<string>? ResourcePackages { get; set; }
        public ReservationDto? Reservations { get; set; }
    }

    private sealed class ReservationDto
    {
        public RangeDto? Cluster { get; set; }
        public RangeDto? System { get; set; }
        public RangeDto? Planet { get; set; }
        public RangeDto? Map { get; set; }
        public RangeDto? Relay { get; set; }
    }

    private sealed class RangeDto
    {
        public int Start { get; set; }
        public int End { get; set; }
    }
}
