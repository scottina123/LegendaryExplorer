using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LegendaryExplorerCore.Gammtek.Extensions.Collections.Generic;

namespace LegendaryExplorer.Tools.AssetDatabase.Filters
{
    public class MaterialFilter : GenericAssetFilter<MaterialRecord>
    {
        private const string OtherTextureType = "Other";

        private static readonly (string TypeName, string[] Tokens)[] TextureTypeMappings =
        [
            ("Diffuse", ["diffuse", "diff", "albedo", "basecolor", "basecolour"]),
            ("Normal", ["normal", "norm"]),
            ("CubeMaps", ["cubemap"]),
            ("Tint", ["tint", "tnt"]),
            ("Mask", ["mask", "msk"]),
            ("Specular", ["specular", "spec"]),
            ("Emissive", ["emissive", "emiss", "emis", "glow"]),
            ("Detail", ["detail"]),
            ("Opacity", ["opacity", "opac"]),
            ("Roughness", ["roughness", "rough"]),
            ("Metallic", ["metallic", "metal"]),
            ("AO", ["ambientocclusion", "ao"]),
            ("Height", ["height", "parallax"])
        ];

        public List<IAssetSpecification<MaterialRecord>> Types { get; private set; } = new();
        public List<IAssetSpecification<MaterialRecord>> BlendModes { get; private set; } = new();
        public ObservableCollection<IAssetSpecification<MaterialRecord>> GeneratedOptions { get; } = new();

        public MaterialFilter(FileListSpecification fileList)
        {
            Search = new SearchSpecification<MaterialRecord>(MaterialSearch);
            PopulateFilterOptions(fileList);
            UpdateFilterCache();
        }

        public void LoadFromDatabase(AssetDB currentDb)
        {
            GeneratedOptions.Clear();
            GeneratedOptions.AddRange(currentDb.MaterialBoolSpecs);
        }

        private void PopulateFilterOptions(FileListSpecification fileList)
        {
            ///////////////////////////////////////
            // Add new custom Material Filters here
            ///////////////////////////////////////
            
            // Options HIDE things.
            Types = new()
            {
                new MaterialClassSpec("Hide materials (+subclasses)", true),
                new MaterialClassSpec("Hide material instances (+subclasses)", false)
            };

            Filters = new()
            {
                fileList,
                new PredicateSpecification<MaterialRecord>("Hide DLC only Materials", mr => !mr.IsDLCOnly),
                new PredicateSpecification<MaterialRecord>("Only Decal Materials",
                    mr => mr.MaterialName.Contains("Decal", StringComparison.OrdinalIgnoreCase)),
                new MaterialSettingSpec("Only Unlit Materials", "LightingModel", param2: "MLM_Unlit"),
                new MaterialSettingSpec("Hide SkeletalMesh exclusive Materials", "bUsedWithSkeletalMesh", param2: "True") {Inverted = true},
                new MaterialSettingSpec("Only 2 sided Materials", "TwoSided", param2: "True"),
                new MaterialSettingSpec("Only Backface culled (1 side)", "TwoSided", param2: "True") {Inverted = true},
                new UISeparator<MaterialRecord>(),
                new MaterialSettingSpec("Must have color setting", "VectorParameter",
                    setting => setting.Parm1.Contains("color", StringComparison.OrdinalIgnoreCase)),
                new MaterialSettingSpec("Must have texture setting", "TextureSampleParameter2D"),
                new MaterialSettingSpec("Must have talk scalar setting", "ScalarParameter",
                    setting => setting.Parm1.Contains("talk", StringComparison.OrdinalIgnoreCase))
            };

            BlendModes = new()
            {
                new MaterialSettingSpec("Translucent or Additive (Opaque)", "BlendMode", (s => s.Parm2 == "BLEND_Translucent" || s.Parm2 == "BLEND_Additive"))
                {
                    Description = "BLEND_Translucent or BLEND_Additive. The 'opaque' filter in previous AssetDB versions."
                },
                new MaterialSettingSpec("Opaque", "BlendMode", param2: "BLEND_Opaque"),
                new MaterialSettingSpec("Masked", "BlendMode", param2: "BLEND_Masked"),
                new MaterialSettingSpec("Translucent", "BlendMode", param2: "BLEND_Translucent"),
                new MaterialSettingSpec("Additive", "BlendMode", param2: "BLEND_Additive"),
                new MaterialSettingSpec("Modulate", "BlendMode", param2: "BLEND_Modulate"),
                new MaterialSettingSpec("Soft Masked", "BlendMode", param2: "BLEND_SoftMasked"),
                new MaterialSettingSpec("Alpha Composite", "BlendMode", param2: "BLEND_AlphaComposite"),
            };
        }

        protected override IEnumerable<IAssetSpecification<MaterialRecord>> GetAdditionalSpecifications()
        {
            var blendModeOr = new OrSpecification<MaterialRecord>(BlendModes); // Matches spec if any of the selected BlendModes are true
            return GeneratedOptions.Concat(Types).Append(blendModeOr);
        }

        public static IEnumerable<MatSetting> GetTextureSettings(MaterialRecord material)
        {
            return material?.MatSettings?.Where(setting => setting is { Name: not null, Parm1: not null }
                && setting.Name.Contains("Texture", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(setting.Parm1)) ?? Enumerable.Empty<MatSetting>();
        }

        public static string GetTextureParameterName(MatSetting setting)
        {
            return string.IsNullOrWhiteSpace(setting?.Parm1) ? null : setting.Parm1;
        }

        public static IEnumerable<string> GetTextureParameterNames(IEnumerable<MaterialRecord> materials, string textureType = null)
        {
            return (materials ?? Enumerable.Empty<MaterialRecord>())
                .SelectMany(GetTextureSettings)
                .Where(setting => string.IsNullOrWhiteSpace(textureType)
                    || string.Equals(textureType, "All", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetTextureParameterType(setting), textureType, StringComparison.OrdinalIgnoreCase))
                .Select(GetTextureParameterName)
                .Where(name => !string.IsNullOrWhiteSpace(name));
        }

        public static IEnumerable<string> GetKnownTextureParameterTypes()
        {
            return TextureTypeMappings.Select(mapping => mapping.TypeName).Append(OtherTextureType);
        }

        public static string GetTextureParameterType(MatSetting setting)
        {
            if (setting is null || string.IsNullOrWhiteSpace(setting.Parm1))
            {
                return null;
            }

            var normalizedName = NormalizeTextureTypeToken(setting.Parm1);
            foreach ((string typeName, string[] tokens) in TextureTypeMappings)
            {
                if (tokens.Any(normalizedName.Contains))
                {
                    return typeName;
                }
            }

            return OtherTextureType;
        }

        public static bool TryGetTextureParameterType(string text, out string textureType)
        {
            textureType = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalizedName = NormalizeTextureTypeToken(text);
            foreach ((string typeName, string[] tokens) in TextureTypeMappings)
            {
                if (tokens.Any(token => normalizedName.Contains(token) || token.Contains(normalizedName)))
                {
                    textureType = typeName;
                    return true;
                }
            }

            if (OtherTextureType.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                textureType = OtherTextureType;
                return true;
            }

            return false;
        }

        public static int GetTextureParameterTypeCount(MaterialRecord material, string textureType = null)
        {
            var textureSettings = GetTextureSettings(material);
            if (string.IsNullOrWhiteSpace(textureType))
            {
                return textureSettings.Count();
            }

            return textureSettings.Count(setting => string.Equals(GetTextureParameterType(setting), textureType, StringComparison.OrdinalIgnoreCase));
        }

        private bool MaterialSearch((string, MaterialRecord) t)
        {
            var (text, mr) = t;
            return mr.MaterialName.ToLower().Contains(text.ToLower()) || mr.ParentPackage.ToLower().Contains(text.ToLower());
        }

        private static string NormalizeTextureTypeToken(string text)
        {
            return new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }
    }
}