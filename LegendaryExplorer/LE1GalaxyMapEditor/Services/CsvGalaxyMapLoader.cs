using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LE1GalaxyMapEditor.Models;
using Microsoft.VisualBasic.FileIO;

namespace LE1GalaxyMapEditor.Services;

public sealed class CsvGalaxyMapLoader
{
    public const string BuiltInSourceName = "Built-in LE1 galaxy map (read-only)";
    public const string BuiltInResourcePrefix = "LE1GalaxyMapEditor.Resources.Data.";

    private static readonly string[] TableNames = ["Cluster", "System", "Planet", "PlotPlanet", "Map", "Relay"];
    private static readonly object CanonicalSchemasGate = new();
    private static IReadOnlyDictionary<GalaxyMapTable, CsvTableSchema>? _canonicalSchemas;

    public GalaxyMapDocument LoadBuiltIn()
    {
        InvalidateCanonicalSchemas();
        return LoadSources(BuiltInSources(), BuiltInSourceName, GalaxyMapModule.BaseGame);
    }

    public GalaxyMapLayer LoadBuiltInLayer()
    {
        // Refresh treats the deployed BASEGAME folder as the current canonical
        // source. Clear the successful cache too, so repairing a malformed file
        // does not require restarting the editor.
        InvalidateCanonicalSchemas();
        return LoadLayerSources(BuiltInSources(), GalaxyMapModule.BaseGame, requireCanonicalHeaders: true);
    }

    public GalaxyMapDocument LoadFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new GalaxyMapLoadException($"The CSV folder does not exist: {folderPath}");
        }

        var fullFolderPath = Path.GetFullPath(folderPath);
        var csvFiles = Directory.EnumerateFiles(
            fullFolderPath,
            "*.csv",
            System.IO.SearchOption.TopDirectoryOnly).ToArray();
        var files = TableNames.ToDictionary(
            name => name,
            name => FindTableFile(fullFolderPath, name, csvFiles));
        var sources = files.ToDictionary(pair => pair.Key, pair => CsvTableSource.FromFile(pair.Value));
        return LoadSources(sources, fullFolderPath, GalaxyMapModule.BaseGame);
    }

    /// <summary>
    /// Loads the optional Legendary Explorer partial tables supplied by one
    /// module. A module may contain any subset of the six canonical files.
    /// </summary>
    public GalaxyMapLayer LoadPartFolder(string folderPath, GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new GalaxyMapLoadException($"The module folder does not exist: {folderPath}");
        }

        var fullFolderPath = Path.GetFullPath(folderPath);
        if (module.FolderPath is not null &&
            !string.Equals(fullFolderPath, module.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new GalaxyMapLoadException("A module can only be loaded from its declared folder.");
        }

        var files = Directory.EnumerateFiles(fullFolderPath, "*.csv", System.IO.SearchOption.TopDirectoryOnly).ToArray();
        var sources = new Dictionary<string, CsvTableSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableName in TableNames)
        {
            var canonicalName = $"GalaxyMap_{tableName}_part.csv";
            var matches = files.Where(path =>
                string.Equals(Path.GetFileName(path), canonicalName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
            {
                throw new GalaxyMapLoadException(
                    $"More than one partial CSV matches the {tableName} table in {fullFolderPath}.");
            }

            if (matches.Length == 1)
            {
                sources[tableName] = CsvTableSource.FromFile(matches[0]);
            }
        }

        return LoadLayerSources(sources, module, requireCanonicalHeaders: true);
    }

    internal GalaxyMapLayer LoadProjectedTables(
        IReadOnlyDictionary<GalaxyMapTable, GalaxyMapTextTableData> tables,
        GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(module);
        var sources = tables.ToDictionary(
            pair => pair.Key.ToString(),
            pair => new CsvTableSource(
                pair.Value.DisplayName,
                () => new MemoryStream(pair.Value.Utf8Csv, writable: false)),
            StringComparer.OrdinalIgnoreCase);
        return LoadLayerSources(sources, module, requireCanonicalHeaders: false);
    }

    public static CsvTableSchema GetCanonicalSchema(GalaxyMapTable table)
    {
        var schemas = Volatile.Read(ref _canonicalSchemas);
        if (schemas is null)
        {
            lock (CanonicalSchemasGate)
            {
                schemas = _canonicalSchemas;
                if (schemas is null)
                {
                    // Unlike Lazy<T>, this retryable cache does not permanently
                    // retain a missing/corrupt-resource exception. Refresh can
                    // therefore succeed after the deployment files are restored.
                    schemas = LoadCanonicalSchemas();
                    Volatile.Write(ref _canonicalSchemas, schemas);
                }
            }
        }

        return schemas[table];
    }

    private static void InvalidateCanonicalSchemas()
    {
        lock (CanonicalSchemasGate)
        {
            Volatile.Write(ref _canonicalSchemas, null);
        }
    }

    private static GalaxyMapDocument LoadSources(
        IReadOnlyDictionary<string, CsvTableSource> sources,
        string sourceName,
        GalaxyMapModule module)
    {
        var document = new GalaxyMapDocument { SourceFolder = sourceName };

        foreach (var row in ParseClusters(sources["Cluster"], module, requireCanonicalHeaders: false).Rows)
            document.Clusters.Add(row);
        foreach (var row in ParseSystems(sources["System"], module, requireCanonicalHeaders: false).Rows)
            document.Systems.Add(row);
        foreach (var row in ParsePlanets(sources["Planet"], module, requireCanonicalHeaders: false).Rows)
            document.Planets.Add(row);
        foreach (var row in ParsePlotPlanets(sources["PlotPlanet"], module, requireCanonicalHeaders: false).Rows)
            document.PlotPlanets.Add(row);
        foreach (var row in ParseMaps(sources["Map"], module, requireCanonicalHeaders: false).Rows)
            document.Maps.Add(row);
        foreach (var row in ParseRelays(sources["Relay"], module, requireCanonicalHeaders: false).Rows)
            document.Relays.Add(row);

        EnsureUniqueRowIds(document.Clusters, "Cluster");
        EnsureUniqueRowIds(document.Systems, "System");
        EnsureUniqueRowIds(document.Planets, "Planet");
        EnsureUniqueRowIds(document.PlotPlanets, "PlotPlanet");
        EnsureUniqueRowIds(document.Maps, "Map");
        EnsureUniqueRowIds(document.Relays, "Relay");

        document.RebuildRelationships();
        return document;
    }

    private static GalaxyMapLayer LoadLayerSources(
        IReadOnlyDictionary<string, CsvTableSource> sources,
        GalaxyMapModule module,
        bool requireCanonicalHeaders)
    {
        var layer = new GalaxyMapLayer(module);
        LoadOptional(GalaxyMapTable.Cluster, "Cluster", ParseClusters);
        LoadOptional(GalaxyMapTable.System, "System", ParseSystems);
        LoadOptional(GalaxyMapTable.Planet, "Planet", ParsePlanets);
        LoadOptional(GalaxyMapTable.PlotPlanet, "PlotPlanet", ParsePlotPlanets);
        LoadOptional(GalaxyMapTable.Map, "Map", ParseMaps);
        LoadOptional(GalaxyMapTable.Relay, "Relay", ParseRelays);
        return layer;

        void LoadOptional<T>(
            GalaxyMapTable table,
            string tableName,
            Func<CsvTableSource, GalaxyMapModule, bool, ParsedTable<T>> parse)
            where T : GalaxyMapRow
        {
            if (!sources.TryGetValue(tableName, out var source))
            {
                return;
            }

            var parsed = parse(source, module, requireCanonicalHeaders);
            EnsureUniqueRowIds(parsed.Rows, tableName);
            layer.SetSchema(parsed.Schema);
            layer.SetSourceRowOrder(table, parsed.Rows.Select(row => row.RowId));
            foreach (var row in parsed.Rows)
            {
                layer.Add(row);
            }
        }
    }

    private static IReadOnlyDictionary<string, CsvTableSource> BuiltInSources()
    {
        return TableNames.ToDictionary(
            tableName => tableName,
            tableName =>
            {
                var fileName = $"GalaxyMap_{tableName}.csv";
                var resourceName = BuiltInResourcePrefix + fileName;
                var assembly = typeof(CsvGalaxyMapLoader).Assembly;
                if (assembly.GetManifestResourceInfo(resourceName) is null)
                {
                    throw new GalaxyMapLoadException(
                        $"The required built-in {tableName} CSV resource '{resourceName}' is missing. " +
                        "Reinstall the editor.");
                }

                return new CsvTableSource(
                    $"embedded:{fileName}",
                    () => assembly.GetManifestResourceStream(resourceName)
                          ?? throw new GalaxyMapLoadException(
                              $"The built-in CSV resource '{resourceName}' could not be opened."));
            });
    }

    private static string FindTableFile(
        string folderPath,
        string tableName,
        IReadOnlyList<string> files)
    {
        var preferredNames = new[] { $"GalaxyMap_{tableName}", tableName };

        foreach (var preferredName in preferredNames)
        {
            var matches = files.Where(path =>
                string.Equals(Path.GetFileNameWithoutExtension(path), preferredName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }

            if (matches.Length > 1)
            {
                throw new GalaxyMapLoadException($"More than one CSV matches the {tableName} table.");
            }
        }

        var suffixPattern = new Regex($"(^|[_\\-.]){Regex.Escape(tableName)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var suffixMatches = files.Where(path => suffixPattern.IsMatch(Path.GetFileNameWithoutExtension(path))).ToArray();
        return suffixMatches.Length switch
        {
            1 => suffixMatches[0],
            0 => throw new GalaxyMapLoadException(
                $"Missing {tableName} CSV. Expected 'GalaxyMap_{tableName}.csv' in {folderPath}."),
            _ => throw new GalaxyMapLoadException($"More than one CSV could be the {tableName} table.")
        };
    }

    private static ParsedTable<Cluster> ParseClusters(
        CsvTableSource source,
        GalaxyMapModule module,
        bool requireCanonicalHeaders)
    {
        var known = Fields("Label", "X", "Y", "Name", "NameText", "SphereSize", "Background");
        var table = ReadTable(source, known, GalaxyMapTable.Cluster, requireCanonicalHeaders);
        var models = new List<Cluster>();
        foreach (var row in table.Rows)
        {
            var model = new Cluster
            {
                RowId = row.RequiredIntAt(0, "Row ID"),
                Label = row.Value("Label"),
                X = row.RequiredDouble("X"),
                Y = row.RequiredDouble("Y"),
                Name = row.RequiredInt("Name"),
                NameText = row.Value("NameText"),
                SphereSize = row.RequiredDouble("SphereSize"),
                Background = row.Value("Background")
            };
            AddExtras(model, row, known);
            AttachSource(model, row, module);
            models.Add(model);
        }

        return new ParsedTable<Cluster>(new CsvTableSchema(GalaxyMapTable.Cluster, table.RawHeaders), models);
    }

    private static ParsedTable<GalaxySystem> ParseSystems(
        CsvTableSource source,
        GalaxyMapModule module,
        bool requireCanonicalHeaders)
    {
        var known = Fields("Label", "Cluster", "X", "Y", "Name", "NameText", "Scale", "ShowNebula");
        var table = ReadTable(source, known, GalaxyMapTable.System, requireCanonicalHeaders);
        var models = new List<GalaxySystem>();
        foreach (var row in table.Rows)
        {
            var model = new GalaxySystem
            {
                RowId = row.RequiredIntAt(0, "Row ID"),
                Label = row.Value("Label"),
                ClusterRowId = row.RequiredInt("Cluster"),
                X = row.RequiredDouble("X"),
                Y = row.RequiredDouble("Y"),
                Name = row.RequiredInt("Name"),
                NameText = row.Value("NameText"),
                Scale = row.RequiredDouble("Scale"),
                ShowNebula = row.RequiredInt("ShowNebula")
            };
            AddExtras(model, row, known);
            AttachSource(model, row, module);
            models.Add(model);
        }

        return new ParsedTable<GalaxySystem>(new CsvTableSchema(GalaxyMapTable.System, table.RawHeaders), models);
    }

    private static ParsedTable<Planet> ParsePlanets(
        CsvTableSource source,
        GalaxyMapModule module,
        bool requireCanonicalHeaders)
    {
        var known = Fields("Label", "System", "X", "Y", "Name", "NameText", "ActiveWorld", "Description",
            "ButtonLabel", "Map", "Scale", "RingColor", "OrbitRing", "SystemLevelType", "PlanetLevelType",
            "Event", "ImageIndex");
        var table = ReadTable(source, known, GalaxyMapTable.Planet, requireCanonicalHeaders);
        var models = new List<Planet>();
        foreach (var row in table.Rows)
        {
            var model = new Planet
            {
                RowId = row.RequiredIntAt(0, "Row ID"),
                Label = row.Value("Label"),
                SystemRowId = row.RequiredInt("System"),
                X = row.RequiredDouble("X"),
                Y = row.RequiredDouble("Y"),
                Name = row.RequiredInt("Name"),
                NameText = row.Value("NameText"),
                ActiveWorld = row.RequiredInt("ActiveWorld"),
                Description = row.NullableInt("Description"),
                ButtonLabel = row.NullableInt("ButtonLabel"),
                MapRowId = row.RequiredInt("Map"),
                Scale = row.RequiredDouble("Scale"),
                RingColor = row.RequiredPackedColor("RingColor"),
                OrbitRing = row.RequiredInt("OrbitRing"),
                SystemLevelType = row.RequiredInt("SystemLevelType"),
                PlanetLevelType = row.NullableInt("PlanetLevelType"),
                Event = row.Value("Event"),
                ImageIndex = row.NullableInt("ImageIndex")
            };
            AddExtras(model, row, known);
            AttachSource(model, row, module);
            models.Add(model);
        }

        return new ParsedTable<Planet>(new CsvTableSchema(GalaxyMapTable.Planet, table.RawHeaders), models);
    }

    private static ParsedTable<PlotPlanetEntry> ParsePlotPlanets(
        CsvTableSource source,
        GalaxyMapModule module,
        bool requireCanonicalHeaders)
    {
        var known = Fields("Code", "Name", "NameText");
        var table = ReadTable(source, known, GalaxyMapTable.PlotPlanet, requireCanonicalHeaders);
        var models = new List<PlotPlanetEntry>();
        foreach (var row in table.Rows)
        {
            var model = new PlotPlanetEntry
            {
                RowId = row.RequiredIntAt(0, "Row ID"),
                Code = row.RequiredInt("Code"),
                Name = row.RequiredInt("Name"),
                NameText = row.Value("NameText")
            };
            AddExtras(model, row, known);
            AttachSource(model, row, module);
            models.Add(model);
        }

        return new ParsedTable<PlotPlanetEntry>(
            new CsvTableSchema(GalaxyMapTable.PlotPlanet, table.RawHeaders), models);
    }

    private static ParsedTable<MapEntry> ParseMaps(
        CsvTableSource source,
        GalaxyMapModule module,
        bool requireCanonicalHeaders)
    {
        var known = Fields("Map", "StartPoint");
        var table = ReadTable(source, known, GalaxyMapTable.Map, requireCanonicalHeaders);
        var models = new List<MapEntry>();
        foreach (var row in table.Rows)
        {
            var model = new MapEntry
            {
                RowId = row.RequiredIntAt(0, "Row ID"),
                MapName = row.Value("Map"),
                StartPoint = row.Value("StartPoint")
            };
            AddExtras(model, row, known);
            AttachSource(model, row, module);
            models.Add(model);
        }

        return new ParsedTable<MapEntry>(new CsvTableSchema(GalaxyMapTable.Map, table.RawHeaders), models);
    }

    private static ParsedTable<RelayConnection> ParseRelays(
        CsvTableSource source,
        GalaxyMapModule module,
        bool requireCanonicalHeaders)
    {
        var known = Fields("StartCluster", "EndCluster");
        var table = ReadTable(source, known, GalaxyMapTable.Relay, requireCanonicalHeaders);
        var models = new List<RelayConnection>();
        foreach (var row in table.Rows)
        {
            var model = new RelayConnection
            {
                RowId = row.RequiredIntAt(0, "Row ID"),
                StartClusterEncoded = row.RequiredInt("StartCluster"),
                EndClusterEncoded = row.RequiredInt("EndCluster")
            };
            AddExtras(model, row, known);
            AttachSource(model, row, module);
            models.Add(model);
        }

        return new ParsedTable<RelayConnection>(new CsvTableSchema(GalaxyMapTable.Relay, table.RawHeaders), models);
    }

    private static HashSet<string> Fields(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<GalaxyMapTable, CsvTableSchema> LoadCanonicalSchemas()
    {
        var sources = BuiltInSources();
        var schemas = new Dictionary<GalaxyMapTable, CsvTableSchema>();
        foreach (var table in Enum.GetValues<GalaxyMapTable>())
        {
            var source = sources[table.ToString()];
            try
            {
                using var stream = source.OpenStream();
                using var parser = new TextFieldParser(
                    stream,
                    Encoding.UTF8,
                    detectEncoding: true,
                    leaveOpen: false)
                {
                    TextFieldType = FieldType.Delimited,
                    HasFieldsEnclosedInQuotes = true,
                    TrimWhiteSpace = false
                };
                parser.SetDelimiters(",");
                var headers = parser.ReadFields()
                    ?? throw new GalaxyMapLoadException(
                        $"The built-in {table} CSV at '{source.DisplayName}' is empty.");
                schemas[table] = new CsvTableSchema(
                    table,
                    headers,
                    GalaxyMapPccSchema.DefaultCellTypes(table, headers));
            }
            catch (GalaxyMapLoadException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new GalaxyMapLoadException(
                    $"Could not read the built-in {table} CSV at '{source.DisplayName}': {exception.Message}",
                    exception);
            }
        }

        return schemas;
    }

    private static void AddExtras(GalaxyMapRow model, CsvRow row, HashSet<string> knownFields)
    {
        foreach (var header in row.Headers.Skip(1))
        {
            if (!knownFields.Contains(header))
            {
                model.AddExtraField(header, row.Value(header));
            }
        }
    }

    private static void AttachSource(GalaxyMapRow model, CsvRow row, GalaxyMapModule module)
    {
        model.Origin = new GalaxyMapRowOrigin(module, OverridesLowerLayer: false);
        model.CsvSnapshot = row.CreateSnapshot();
    }

    private static CsvTableData ReadTable(
        CsvTableSource source,
        HashSet<string> requiredFields,
        GalaxyMapTable table,
        bool requireCanonicalHeaders)
    {
        try
        {
            using var stream = source.OpenStream();
            using var parser = new TextFieldParser(
                stream,
                Encoding.UTF8,
                detectEncoding: true,
                leaveOpen: true)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false
            };
            parser.SetDelimiters(",");

            var rawHeaders = parser.ReadFields()
                ?? throw new GalaxyMapLoadException($"{source.DisplayName} is empty.");
            if (rawHeaders.Length < 1)
            {
                throw new GalaxyMapLoadException($"{source.DisplayName} has no columns.");
            }

            if (!string.IsNullOrEmpty(rawHeaders[0]))
            {
                throw new GalaxyMapLoadException(
                    $"{source.DisplayName} must have an unnamed first column for its 2DA Row ID.");
            }

            if (requireCanonicalHeaders)
            {
                ValidateCanonicalHeaders(source.DisplayName, table, rawHeaders);
            }

            var headers = rawHeaders.ToArray();
            headers[0] = "Row ID";
            var duplicateHeader = headers.Skip(1)
                .GroupBy(header => header, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
            if (duplicateHeader is not null)
            {
                throw new GalaxyMapLoadException(
                    $"{source.DisplayName} has a blank or duplicate header after its Row ID column.");
            }

            var missingHeaders = requiredFields
                .Where(required => !headers.Skip(1).Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (missingHeaders.Length > 0)
            {
                throw new GalaxyMapLoadException(
                    $"{source.DisplayName} is missing required column(s): {string.Join(", ", missingHeaders)}.");
            }

            var indexes = headers.Select((header, index) => (header, index))
                .ToDictionary(pair => pair.header, pair => pair.index, StringComparer.OrdinalIgnoreCase);

            var rows = new List<CsvRow>();
            while (!parser.EndOfData)
            {
                var csvLine = checked((int)parser.LineNumber);
                var fields = parser.ReadFields() ?? [];
                if (fields.All(string.IsNullOrEmpty))
                {
                    continue;
                }

                if (fields.Length > headers.Length)
                {
                    throw new GalaxyMapLoadException(
                        $"{source.DisplayName}, CSV row {csvLine} has {fields.Length} values but only {headers.Length} columns.");
                }

                if (fields.Length < headers.Length)
                {
                    Array.Resize(ref fields, headers.Length);
                    for (var index = 0; index < fields.Length; index++)
                    {
                        fields[index] ??= string.Empty;
                    }
                }

                rows.Add(new CsvRow(source.DisplayName, csvLine, rawHeaders, headers, indexes, fields));
            }

            return new CsvTableData(rawHeaders, rows);
        }
        catch (GalaxyMapLoadException)
        {
            throw;
        }
        catch (MalformedLineException exception)
        {
            throw new GalaxyMapLoadException(
                $"Malformed CSV in {source.DisplayName} near line {exception.LineNumber}: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GalaxyMapLoadException($"Could not read {source.DisplayName}: {exception.Message}", exception);
        }
    }

    private static void ValidateCanonicalHeaders(
        string sourceName,
        GalaxyMapTable table,
        IReadOnlyList<string> actualHeaders)
    {
        var expectedHeaders = GetCanonicalSchema(table).Headers;
        if (actualHeaders.Count != expectedHeaders.Count)
        {
            throw new GalaxyMapLoadException(
                $"{sourceName} has {actualHeaders.Count} {table} columns; " +
                $"Legendary Explorer requires exactly {expectedHeaders.Count} canonical columns.");
        }

        for (var index = 0; index < expectedHeaders.Count; index++)
        {
            if (string.Equals(actualHeaders[index], expectedHeaders[index], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expected = index == 0 ? "an unnamed Row ID column" : $"'{expectedHeaders[index]}'";
            var actual = string.IsNullOrEmpty(actualHeaders[index])
                ? "an unnamed column"
                : $"'{actualHeaders[index]}'";
            throw new GalaxyMapLoadException(
                $"{sourceName} has {actual} at {table} column {index + 1}; expected {expected}. " +
                "Column names and order must match the canonical 2DA schema (letter casing is ignored).");
        }
    }

    private static void EnsureUniqueRowIds<T>(IEnumerable<T> rows, string tableName) where T : GalaxyMapRow
    {
        var duplicate = rows.GroupBy(row => row.RowId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new GalaxyMapLoadException($"{tableName} contains duplicate row ID {duplicate.Key}.");
        }
    }

    private sealed record CsvTableSource(string DisplayName, Func<Stream> OpenStream)
    {
        public static CsvTableSource FromFile(string path, bool displayFullPath = false)
        {
            var fullPath = Path.GetFullPath(path);
            return new CsvTableSource(displayFullPath ? fullPath : Path.GetFileName(fullPath), () => File.OpenRead(fullPath));
        }
    }

    private sealed record ParsedTable<T>(CsvTableSchema Schema, IReadOnlyList<T> Rows)
        where T : GalaxyMapRow;

    private sealed record CsvTableData(IReadOnlyList<string> RawHeaders, IReadOnlyList<CsvRow> Rows);

    private sealed class CsvRow
    {
        private readonly string[] _fields;
        private readonly IReadOnlyDictionary<string, int> _indexes;
        private readonly string[] _rawHeaders;

        public CsvRow(
            string path,
            int csvLine,
            string[] rawHeaders,
            string[] headers,
            IReadOnlyDictionary<string, int> indexes,
            string[] fields)
        {
            Path = path;
            CsvLine = csvLine;
            _rawHeaders = rawHeaders;
            Headers = headers;
            _fields = fields;
            _indexes = indexes;
        }

        public string Path { get; }
        public int CsvLine { get; }
        public IReadOnlyList<string> Headers { get; }

        public CsvRowSnapshot CreateSnapshot()
            => CsvRowSnapshot.FromParsedRow(Path, CsvLine, _rawHeaders, _fields);

        public string Value(string fieldName) => _fields[_indexes[fieldName]];

        public int RequiredIntAt(int index, string fieldName)
            => ParseInt(_fields[index], fieldName, nullable: false)!.Value;

        public int RequiredInt(string fieldName) => ParseInt(Value(fieldName), fieldName, nullable: false)!.Value;
        public int? NullableInt(string fieldName) => ParseInt(Value(fieldName), fieldName, nullable: true);

        public long RequiredPackedColor(string fieldName)
        {
            var rawValue = Value(fieldName).Trim();
            if (decimal.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                decimal.Truncate(value) == value &&
                value >= int.MinValue &&
                value <= uint.MaxValue)
            {
                return decimal.ToInt64(value);
            }

            throw Error(fieldName, rawValue, "signed or unsigned packed 32-bit whole number");
        }

        public double RequiredDouble(string fieldName)
        {
            var rawValue = Value(fieldName).Trim();
            if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            throw Error(fieldName, rawValue, "number");
        }

        private int? ParseInt(string rawValue, string fieldName, bool nullable)
        {
            var trimmed = rawValue.Trim();
            if (nullable && trimmed.Length == 0)
            {
                return null;
            }

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            throw Error(fieldName, rawValue, nullable ? "whole number or blank" : "whole number");
        }

        private GalaxyMapLoadException Error(string fieldName, string value, string expected)
            => new($"{System.IO.Path.GetFileName(Path)}, CSV row {CsvLine}, field '{fieldName}' expected a {expected} but found '{value}'.");
    }
}

internal sealed record GalaxyMapTextTableData(string DisplayName, byte[] Utf8Csv);
