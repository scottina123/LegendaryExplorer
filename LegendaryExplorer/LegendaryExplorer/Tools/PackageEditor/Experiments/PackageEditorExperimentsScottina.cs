using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.AssetDatabase.Filters;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Collections;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace LegendaryExplorer.Tools.PackageEditor.Experiments
{
    public static class PackageEditorExperimentsScottina
    {
        private static readonly string[] MaterialPropertiesToPreserve =
        [
            "Expressions",
            "ReferencedTextures"
        ];

        private static readonly string[] MaterialInstancePropertiesToPreserve =
        [
            "Parent",
            "ReferencedTextures",
            "TextureParameterValues",
            "VectorParameterValues",
            "ScalarParameterValues"
        ];

        private sealed record MaterialSearchProfile(
            string ObjectName,
            bool IsInstance,
            string ParentMaterialKey,
            HashSet<string> UsedOn,
            HashSet<string> ScalarParameters,
            HashSet<string> VectorParameters,
            HashSet<string> TextureParameters,
            Dictionary<string, int> TextureTypeCounts);

        private sealed record PreservedMaterialState(
            string[] PropertyNames,
            Dictionary<string, Property> Properties,
            Material OriginalBinary);

        private sealed record RepairSummary(
            int FixedCount,
            int WarningCount,
            List<string> Failures,
            List<string> Warnings);

        public static void FixBrokenMaterialsUsingAssetDatabase(PackageEditorWindow pew)
        {
            if (pew?.Pcc == null)
            {
                return;
            }

            if (!pew.Pcc.Game.IsMEGame())
            {
                MessageBox.Show(pew, "Broken material repair is only supported for Mass Effect package files.", "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(Settings.AssetDBPath) || !File.Exists(Settings.AssetDBPath))
            {
                MessageBox.Show(pew, "Asset Database not found. Configure or build the Asset Database first.", "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string gamePath = MEDirectories.GetDefaultGamePath(pew.Pcc.Game);
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                MessageBox.Show(pew, $"No {pew.Pcc.Game} installation was found. Configure the game path first.", "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<ExportEntry> brokenMaterials = ShaderCacheManipulator.GetBrokenMaterials(pew.Pcc);
            if (brokenMaterials.Count == 0)
            {
                MessageBox.Show(pew, "No broken materials were found in the current package.", "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(pew,
                    $"This will try to repair {brokenMaterials.Count} broken Material or MaterialInstance export(s) in the current package using the {pew.Pcc.Game} asset database.\n\nContinue?",
                    "Fix broken materials",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            pew.BusyText = "Fixing broken materials";
            pew.IsBusy = true;

            Task.Run(() => RepairBrokenMaterials(pew.Pcc, Settings.AssetDBPath, gamePath, brokenMaterials))
                .ContinueWithOnUIThread(task =>
                {
                    pew.IsBusy = false;

                    if (task.Exception != null)
                    {
                        MessageBox.Show(pew, task.Exception.FlattenException(), "Fix broken materials", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    RepairSummary summary = task.Result;
                    if (summary.Failures.Count == 0 && summary.WarningCount == 0)
                    {
                        MessageBox.Show(pew,
                            $"Repaired {summary.FixedCount} broken material export{(summary.FixedCount == 1 ? string.Empty : "s")}.",
                            "Fix broken materials",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var lines = new List<string>();
                    if (summary.Failures.Count > 0)
                    {
                        lines.AddRange(summary.Failures);
                    }

                    if (summary.Warnings.Count > 0)
                    {
                        if (lines.Count > 0)
                        {
                            lines.Add(string.Empty);
                        }

                        lines.AddRange(summary.Warnings);
                    }

                    new ListDialog(lines,
                        $"Broken material repair results ({summary.FixedCount} fixed)",
                        "The following items could not be fully repaired or reported relink warnings.",
                        pew).Show();
                });
        }

        private static RepairSummary RepairBrokenMaterials(IMEPackage package, string assetDbPath, string gamePath, List<ExportEntry> brokenMaterials)
        {
            var assetDb = new AssetDB();
            AssetDatabaseWindow.LoadDatabase(assetDbPath, package.Game, assetDb, CancellationToken.None).GetAwaiter().GetResult();
            if (assetDb.Materials.Count == 0)
            {
                throw new InvalidOperationException($"The asset database does not contain any material records for {package.Game}.");
            }

            int fixedCount = 0;
            var failures = new List<string>();
            var warnings = new List<string>();
            var filePathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (ExportEntry brokenExport in brokenMaterials)
            {
                try
                {
                    PreservedMaterialState preservedState = CapturePreservedState(brokenExport);
                    MaterialSearchProfile profile = BuildSearchProfile(brokenExport);
                    var candidates = GetRankedCandidates(assetDb, profile).Take(64).ToList();
                    if (candidates.Count == 0)
                    {
                        failures.Add($"FAILED #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: no suitable asset database candidate was found.");
                        continue;
                    }

                    using var donorPackage = TryOpenBestDonorPackage(assetDb, package.Game, gamePath, candidates, brokenExport, filePathCache, out var donorExport, out var donorDescription);
                    if (donorPackage == null || donorExport == null)
                    {
                        failures.Add($"FAILED #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: unable to open a usable donor material from the asset database.");
                        continue;
                    }

                    var rop = new RelinkerOptionsPackage
                    {
                        Cache = new PackageCache(),
                        ImportExportDependencies = true,
                        PortImportsMemorySafe = true,
                        PortExportsAsImportsWhenPossible = false
                    };
                    List<EntryStringPair> relinkIssues = EntryImporter.ImportAndRelinkEntries(
                        EntryImporter.PortingOption.ReplaceSingularWithRelink,
                        donorExport,
                        package,
                        brokenExport,
                        true,
                        rop,
                        out _);

                    RestorePreservedState(brokenExport, preservedState);

                    if (ShaderCacheManipulator.IsMaterialBroken(brokenExport))
                    {
                        failures.Add($"FAILED #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: donor '{donorDescription}' was applied, but the material still reports as broken.");
                        continue;
                    }

                    fixedCount++;
                    if (relinkIssues.Count > 0)
                    {
                        warnings.Add($"WARNING #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: repaired using '{donorDescription}', but {relinkIssues.Count} relink issue(s) were reported.");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"FAILED #{brokenExport.UIndex} {brokenExport.InstancedFullPath}: {ex.Message}");
                }
            }

            return new RepairSummary(fixedCount, warnings.Count, failures, warnings);
        }

        private static PreservedMaterialState CapturePreservedState(ExportEntry export)
        {
            string[] propertyNames = export.ClassName == "Material"
                ? MaterialPropertiesToPreserve
                : MaterialInstancePropertiesToPreserve;

            PropertyCollection props = export.GetProperties();
            var preservedProps = new Dictionary<string, Property>(StringComparer.OrdinalIgnoreCase);
            foreach (string propertyName in propertyNames)
            {
                if (props.GetProp<Property>(propertyName) is { } property)
                {
                    preservedProps[propertyName] = property.DeepClone();
                }
            }

            Material originalBinary = export.ClassName == "Material" ? export.GetBinaryData<Material>() : null;
            return new PreservedMaterialState(propertyNames, preservedProps, originalBinary);
        }

        private static void RestorePreservedState(ExportEntry export, PreservedMaterialState state)
        {
            PropertyCollection props = export.GetProperties();
            foreach (string propertyName in state.PropertyNames)
            {
                if (state.Properties.TryGetValue(propertyName, out Property property))
                {
                    props.AddOrReplaceProp(property.DeepClone());
                }
                else
                {
                    props.RemoveNamedProperty(propertyName);
                }
            }

            if (state.OriginalBinary != null)
            {
                Material donorBinary = export.GetBinaryData<Material>();
                RestoreMaterialParameterData(state.OriginalBinary, donorBinary, export.Game);
                export.WritePropertiesAndBinary(props, donorBinary);
            }
            else
            {
                export.WriteProperties(props);
            }
        }

        private static void RestoreMaterialParameterData(Material originalBinary, Material donorBinary, MEGame game)
        {
            if (originalBinary?.SM3MaterialResource != null && donorBinary?.SM3MaterialResource != null)
            {
                CopyMaterialParameterData(originalBinary.SM3MaterialResource, donorBinary.SM3MaterialResource, game);
            }

            if (game != MEGame.UDK && originalBinary?.SM2MaterialResource != null && donorBinary?.SM2MaterialResource != null)
            {
                CopyMaterialParameterData(originalBinary.SM2MaterialResource, donorBinary.SM2MaterialResource, game);
            }
        }

        private static void CopyMaterialParameterData(MaterialResource source, MaterialResource target, MEGame game)
        {
            target.TextureDependencyLengthMap = source.TextureDependencyLengthMap != null
                ? new UMultiMap<int, int>(source.TextureDependencyLengthMap)
                : [];
            target.MaxTextureDependencyLength = source.MaxTextureDependencyLength;
            target.NumUserTexCoords = source.NumUserTexCoords;
            target.UniformExpressionTextures = source.UniformExpressionTextures?.ToArray() ?? [];
            target.UsingTransforms = source.UsingTransforms;
            target.TextureLookups = source.TextureLookups?.Select(textureLookup => new MaterialResource.TextureLookup
            {
                TexCoordIndex = textureLookup.TexCoordIndex,
                TextureIndex = textureLookup.TextureIndex,
                UScale = textureLookup.UScale,
                VScale = textureLookup.VScale,
                Unk = textureLookup.Unk
            }).ToArray() ?? [];

            if (game <= MEGame.ME2)
            {
                target.UniformPixelVectorExpressions = source.UniformPixelVectorExpressions?.ToArray() ?? [];
                target.UniformPixelScalarExpressions = source.UniformPixelScalarExpressions?.ToArray() ?? [];
                target.Uniform2DTextureExpressions = source.Uniform2DTextureExpressions?.ToArray() ?? [];
                target.UniformCubeTextureExpressions = source.UniformCubeTextureExpressions?.ToArray() ?? [];

                if (game == MEGame.ME1)
                {
                    target.Me1MaterialUniformExpressionsList = source.Me1MaterialUniformExpressionsList?.Select(CloneMe1UniformExpressions).ToArray() ?? [];
                }
            }
        }

        private static ME1MaterialUniformExpressionsElement CloneMe1UniformExpressions(ME1MaterialUniformExpressionsElement source)
        {
            return new ME1MaterialUniformExpressionsElement
            {
                UniformPixelVectorExpressions = source.UniformPixelVectorExpressions?.ToArray() ?? [],
                UniformPixelScalarExpressions = source.UniformPixelScalarExpressions?.ToArray() ?? [],
                Uniform2DTextureExpressions = source.Uniform2DTextureExpressions?.ToArray() ?? [],
                UniformCubeTextureExpressions = source.UniformCubeTextureExpressions?.ToArray() ?? [],
                unk2 = source.unk2,
                unk3 = source.unk3,
                unk4 = source.unk4,
                unk5 = source.unk5
            };
        }

        private static MaterialSearchProfile BuildSearchProfile(ExportEntry export)
        {
            bool isInstance = export.IsA("MaterialInstance");
            var usedOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scalarParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vectorParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textureParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textureTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            PropertyCollection props = export.GetProperties();

            foreach (BoolProperty boolProperty in props.OfType<BoolProperty>())
            {
                if (boolProperty.Value && boolProperty.Name.Name.StartsWith("bUsedWith", StringComparison.OrdinalIgnoreCase))
                {
                    usedOn.Add(boolProperty.Name.Name[9..]);
                }
            }

            string parentMaterialKey = null;
            if (isInstance && props.GetProp<ObjectProperty>("Parent") is { } parentProperty && export.FileRef.TryGetEntry(parentProperty.Value, out var parentEntry))
            {
                parentMaterialKey = parentEntry.InstancedFullPath.ToLowerInvariant();
            }

            if (isInstance)
            {
                foreach (ScalarParameter scalar in ScalarParameter.GetScalarParameters(export, true) ?? [])
                {
                    AddParameterName(scalarParameters, scalar.ParameterName);
                }

                foreach (VectorParameter vector in VectorParameter.GetVectorParameters(export, true) ?? [])
                {
                    AddParameterName(vectorParameters, vector.ParameterName);
                }

                foreach (TextureParameter texture in TextureParameter.GetTextureParameters(export, true) ?? [])
                {
                    AddParameterName(textureParameters, texture.ParameterName);
                    AddTextureType(textureTypeCounts, texture.ParameterName);
                }
            }
            else if (props.GetProp<ArrayProperty<ObjectProperty>>("Expressions") is { } expressions)
            {
                foreach (ObjectProperty expressionRef in expressions)
                {
                    if (!export.FileRef.TryGetUExport(expressionRef.Value, out var expression))
                    {
                        continue;
                    }

                    string parameterName = expression.GetProperty<NameProperty>("ParameterName")?.Value.Instanced;
                    switch (expression.ClassName)
                    {
                        case "MaterialExpressionScalarParameter":
                            AddParameterName(scalarParameters, parameterName);
                            break;
                        case "MaterialExpressionVectorParameter":
                            AddParameterName(vectorParameters, parameterName);
                            break;
                        default:
                            if (expression.ClassName.Contains("TextureSampleParameter", StringComparison.OrdinalIgnoreCase))
                            {
                                AddParameterName(textureParameters, parameterName);
                                AddTextureType(textureTypeCounts, parameterName);
                            }
                            break;
                    }
                }
            }

            return new MaterialSearchProfile(
                export.ObjectName.Instanced,
                isInstance,
                parentMaterialKey,
                usedOn,
                scalarParameters,
                vectorParameters,
                textureParameters,
                textureTypeCounts);
        }

        private static IEnumerable<MaterialRecord> GetRankedCandidates(AssetDB assetDb, MaterialSearchProfile profile)
        {
            return assetDb.Materials
                .Select(record => new { Record = record, Score = ScoreCandidate(profile, record) })
                .Where(item => item.Score > int.MinValue)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Record.MaterialName, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Record);
        }

        private static int ScoreCandidate(MaterialSearchProfile profile, MaterialRecord record)
        {
            bool candidateIsInstance = IsInstanceRecord(record);
            if (candidateIsInstance != profile.IsInstance)
            {
                return int.MinValue;
            }

            int score = 0;
            score += ScoreNameSimilarity(profile.ObjectName, record.MaterialName);
            score += ScoreSetOverlap(profile.UsedOn, record.UsedOn ?? [], 22, 8, 3);

            if (!string.IsNullOrWhiteSpace(profile.ParentMaterialKey))
            {
                if (string.Equals(record.ParentMaterialKey, profile.ParentMaterialKey, StringComparison.OrdinalIgnoreCase))
                {
                    score += 250;
                }
                else if (!string.IsNullOrWhiteSpace(record.ParentMaterialKey))
                {
                    string sourceParentName = profile.ParentMaterialKey[(profile.ParentMaterialKey.LastIndexOf('.') + 1)..];
                    string candidateParentName = record.ParentMaterialKey[(record.ParentMaterialKey.LastIndexOf('.') + 1)..];
                    score += ScoreNameSimilarity(sourceParentName, candidateParentName) / 2;
                }
            }

            HashSet<string> candidateScalarParameters = GetSettingNames(record, "ScalarParameter");
            HashSet<string> candidateVectorParameters = GetSettingNames(record, "VectorParameter");
            HashSet<string> candidateTextureParameters = GetTextureParameterNames(record);
            score += ScoreSetOverlap(profile.ScalarParameters, candidateScalarParameters, 30, 10, 4);
            score += ScoreSetOverlap(profile.VectorParameters, candidateVectorParameters, 30, 10, 4);
            score += ScoreSetOverlap(profile.TextureParameters, candidateTextureParameters, 34, 12, 5);

            Dictionary<string, int> candidateTextureTypeCounts = GetTextureTypeCounts(record);
            foreach (string textureType in profile.TextureTypeCounts.Keys.Union(candidateTextureTypeCounts.Keys, StringComparer.OrdinalIgnoreCase))
            {
                int sourceCount = profile.TextureTypeCounts.GetValueOrDefault(textureType);
                int candidateCount = candidateTextureTypeCounts.GetValueOrDefault(textureType);
                score -= Math.Abs(sourceCount - candidateCount) * 7;
                if (sourceCount == candidateCount && sourceCount > 0)
                {
                    score += 15;
                }
            }

            return score;
        }

        private static int ScoreNameSimilarity(string left, string right)
        {
            string normalizedLeft = NormalizeName(left);
            string normalizedRight = NormalizeName(right);
            if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
            {
                return 0;
            }

            int score = 0;
            if (normalizedLeft == normalizedRight)
            {
                score += 240;
            }

            int distance = normalizedLeft.LevenshteinDistance(normalizedRight);
            score += Math.Max(0, 180 - (distance * 8));

            HashSet<string> leftTokens = Tokenize(left);
            HashSet<string> rightTokens = Tokenize(right);
            score += leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count() * 18;
            return score;
        }

        private static int ScoreSetOverlap(HashSet<string> source, IEnumerable<string> candidateValues, int matchWeight, int exactWeight, int mismatchPenalty)
        {
            HashSet<string> candidate = new(candidateValues.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
            if (source.Count == 0 && candidate.Count == 0)
            {
                return 0;
            }

            int matches = source.Intersect(candidate, StringComparer.OrdinalIgnoreCase).Count();
            int score = matches * matchWeight;
            if (matches == source.Count && matches == candidate.Count)
            {
                score += exactWeight;
            }

            int sourceOnly = source.Except(candidate, StringComparer.OrdinalIgnoreCase).Count();
            int candidateOnly = candidate.Except(source, StringComparer.OrdinalIgnoreCase).Count();
            score -= (sourceOnly + candidateOnly) * mismatchPenalty;
            return score;
        }

        private static IMEPackage TryOpenBestDonorPackage(
            AssetDB assetDb,
            MEGame game,
            string gamePath,
            IEnumerable<MaterialRecord> candidates,
            ExportEntry sourceExport,
            Dictionary<string, string> filePathCache,
            out ExportEntry donorExport,
            out string donorDescription)
        {
            donorExport = null;
            donorDescription = null;

            foreach (MaterialRecord candidate in candidates)
            {
                foreach (MatUsage usage in candidate.Usages.OrderBy(u => u.IsInDLC).ThenBy(u => u.FileKey))
                {
                    if (!TryGetUsageInfo(assetDb, usage.FileKey, out string fileName, out string contentDir))
                    {
                        continue;
                    }

                    string filePath = FindUsageFilePath(game, gamePath, fileName, contentDir, filePathCache);
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        continue;
                    }

                    IMEPackage donorPackage = TryOpenUsagePackage(filePath, fileName);
                    if (donorPackage == null)
                    {
                        continue;
                    }

                    if (!donorPackage.TryGetUExport(usage.UIndex, out donorExport))
                    {
                        donorPackage.Dispose();
                        continue;
                    }

                    if (donorExport.IsA("MaterialInstance") != sourceExport.IsA("MaterialInstance")
                        || donorExport.ClassName == sourceExport.ClassName && donorExport.FileRef.FilePath == sourceExport.FileRef.FilePath && donorExport.UIndex == sourceExport.UIndex)
                    {
                        donorPackage.Dispose();
                        continue;
                    }

                    if (ShaderCacheManipulator.IsMaterialBroken(donorExport))
                    {
                        donorPackage.Dispose();
                        continue;
                    }

                    donorDescription = $"{donorExport.InstancedFullPath} in {fileName}";
                    return donorPackage;
                }
            }

            donorExport = null;
            return null;
        }

        private static bool TryGetUsageInfo(AssetDB assetDb, int fileKey, out string fileName, out string contentDir)
        {
            fileName = null;
            contentDir = null;
            if (fileKey < 0 || fileKey >= assetDb.FileList.Count)
            {
                return false;
            }

            FileNameDirKeyPair fileRecord = assetDb.FileList[fileKey];
            fileName = fileRecord.FileName;
            if (fileRecord.DirectoryKey >= 0 && fileRecord.DirectoryKey < assetDb.ContentDir.Count)
            {
                contentDir = assetDb.ContentDir[fileRecord.DirectoryKey];
            }

            return !string.IsNullOrWhiteSpace(fileName);
        }

        private static string FindUsageFilePath(MEGame game, string gamePath, string fileName, string contentDir, Dictionary<string, string> filePathCache)
        {
            string cacheKey = $"{contentDir}|{fileName}";
            if (filePathCache.TryGetValue(cacheKey, out string cachedPath))
            {
                return cachedPath;
            }

            string filePath = Directory.EnumerateFiles(gamePath, fileName, SearchOption.AllDirectories)
                .FirstOrDefault(path => string.IsNullOrWhiteSpace(contentDir) || path.Contains(contentDir, StringComparison.OrdinalIgnoreCase));

            if (filePath == null && game == MEGame.ME3)
            {
                string sfarPath = Directory.EnumerateFiles(gamePath, "Default.sfar", SearchOption.AllDirectories)
                    .FirstOrDefault(path => string.IsNullOrWhiteSpace(contentDir) || path.Contains(contentDir, StringComparison.OrdinalIgnoreCase));
                if (sfarPath != null)
                {
                    DLCPackage dlcPackage = new DLCPackage(sfarPath);
                    if (dlcPackage.FindFileEntry(fileName) != -1)
                    {
                        filePath = sfarPath;
                    }
                }
            }

            filePathCache[cacheKey] = filePath;
            return filePath;
        }

        private static IMEPackage TryOpenUsagePackage(string filePath, string realFileName)
        {
            if (Path.GetFileName(filePath).Equals("Default.sfar", StringComparison.OrdinalIgnoreCase))
            {
                DLCPackage dlcPackage = new DLCPackage(filePath);
                int fileIndex = dlcPackage.FindFileEntry(realFileName);
                if (fileIndex == -1)
                {
                    return null;
                }

                return MEPackageHandler.OpenMEPackageFromStream(dlcPackage.DecompressEntry(fileIndex), realFileName);
            }

            return MEPackageHandler.OpenMEPackage(filePath, forceLoadFromDisk: true);
        }

        private static bool IsInstanceRecord(MaterialRecord record)
        {
            return record.MatSettings.Any(setting => setting.Name == "IsInstance" && string.Equals(setting.Parm1, "true", StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<string> GetSettingNames(MaterialRecord record, string settingName)
        {
            return record.MatSettings
                .Where(setting => string.Equals(setting.Name, settingName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(setting.Parm1))
                .Select(setting => setting.Parm1)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> GetTextureParameterNames(MaterialRecord record)
        {
            return MaterialFilter.GetTextureSettings(record)
                .Select(MaterialFilter.GetTextureParameterName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, int> GetTextureTypeCounts(MaterialRecord record)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (MatSetting setting in MaterialFilter.GetTextureSettings(record))
            {
                string textureType = MaterialFilter.GetTextureParameterType(setting) ?? "Other";
                counts[textureType] = counts.TryGetValue(textureType, out int count) ? count + 1 : 1;
            }

            return counts;
        }

        private static void AddParameterName(HashSet<string> parameters, string parameterName)
        {
            if (!string.IsNullOrWhiteSpace(parameterName) && parameterName != "None")
            {
                parameters.Add(parameterName);
            }
        }

        private static void AddTextureType(Dictionary<string, int> textureTypeCounts, string parameterName)
        {
            if (!MaterialFilter.TryGetTextureParameterType(parameterName, out string textureType))
            {
                textureType = "Other";
            }

            textureTypeCounts[textureType] = textureTypeCounts.TryGetValue(textureType, out int count) ? count + 1 : 1;
        }

        private static string NormalizeName(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static HashSet<string> Tokenize(string value)
        {
            return (value ?? string.Empty)
                .Split(['_', '.', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(token => token.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
