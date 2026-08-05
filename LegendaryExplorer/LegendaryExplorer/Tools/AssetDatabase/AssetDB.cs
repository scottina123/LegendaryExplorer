using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BinaryPack.Attributes;
using LegendaryExplorer.Tools.AssetDatabase.Filters;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.PlotDatabase.PlotElements;

namespace LegendaryExplorer.Tools.AssetDatabase
{
    /*
     * READ THIS BEFORE MODIFYING DATABASE CLASSES!
     * BinaryPack does not work with ValueTuples, and it requires classes to have a parameterless constructor!
     * That is why all the records have seemingly useless contructors.
     */

    /// <summary>
    /// Database of all found records generated from AssetDatabase
    /// </summary>
    public class AssetDB
    {
        public MEGame Game { get; set; }
        public string GenerationDate { get; set; }
        public string DatabaseVersion { get; set; }
        public MELocalization Localization { get; set; }

        public List<FileNameDirKeyPair> FileList { get; set; } = new();
        public List<string> ContentDir { get; set; } = new();

        public List<ClassRecord> ClassRecords { get; set; } = new();

        public List<MaterialRecord> Materials { get; set; } = new();

        public List<MaterialBoolSpec> MaterialBoolSpecs { get; set; } = new();

        public List<AnimationRecord> Animations { get; set; } = new();

        public List<MeshRecord> Meshes { get; set; } = new();

        public List<ParticleSysRecord> Particles { get; set; } = new();

        public List<TextureRecord> Textures { get; set; } = new();

        public List<GUIElement> GUIElements { get; set; } = new();

        public List<Conversation> Conversations { get; set; } = new();

        public List<ConvoLine> Lines { get; set; } = new();

        public List<SequenceEventRecord> SequenceEvents { get; set; } = new();

        public List<TlkStringRecord> TlkStrings { get; set; } = new();

        public List<ActorRecord> Actors { get; set; } = new();

        public List<GestureTrackRecord> GestureTracks { get; set; } = new();

        public List<PropActionRecord> PropActions { get; set; } = new();

        public List<BioMorphFaceRecord> MorphFaces { get; set; } = new();

        public List<BioPlanetMaterialProfileRecord> BioPlanetMaterials { get; set; } = new();

        public PlotUsageDB PlotUsages { get; set; } = new();

        public AssetDB(MEGame meGame, string GenerationDate, string databaseVersion, IEnumerable<FileNameDirKeyPair> FileList, IEnumerable<string> ContentDir)
        {
            this.Game = meGame;
            this.GenerationDate = GenerationDate;
            this.DatabaseVersion = databaseVersion;
            this.FileList.AddRange(FileList);
            this.ContentDir.AddRange(ContentDir);
        }

        public AssetDB()
        { }

        public void Clear()
        {
            GenerationDate = null;
            FileList.Clear();
            ContentDir.Clear();
            ClearRecords();
        }

        public void ClearRecords()
        {
            ClassRecords.Clear();
            Animations.Clear();
            Materials.Clear();
            MaterialBoolSpecs.Clear();
            Meshes.Clear();
            Particles.Clear();
            Textures.Clear();
            GUIElements.Clear();
            Conversations.Clear();
            Lines.Clear();
            SequenceEvents.Clear();
            TlkStrings.Clear();
            Actors.Clear();
            GestureTracks.Clear();
            PropActions.Clear();
            MorphFaces.Clear();
            BioPlanetMaterials.Clear();
            PlotUsages.ClearRecords();
        }

        public void AddRecords(AssetDB from)
        {
            ClassRecords.AddRange(from.ClassRecords);
            Animations.AddRange(from.Animations);
            Materials.AddRange(from.Materials);
            MaterialBoolSpecs.AddRange(from.MaterialBoolSpecs);
            Meshes.AddRange(from.Meshes);
            Particles.AddRange(from.Particles);
            Textures.AddRange(from.Textures);
            GUIElements.AddRange(from.GUIElements);
            Conversations.AddRange(from.Conversations);
            Lines.AddRange(from.Lines);
            SequenceEvents.AddRange(from.SequenceEvents);
            TlkStrings.AddRange(from.TlkStrings);
            Actors.AddRange(from.Actors);
            GestureTracks.AddRange(from.GestureTracks);
            PropActions.AddRange(from.PropActions);
            MorphFaces.AddRange(from.MorphFaces);
            BioPlanetMaterials.AddRange(from.BioPlanetMaterials);
            PlotUsages.AddRecords(from.PlotUsages);
        }
    }

    public interface IAssetRecord
    {
        public IEnumerable<IAssetUsage> AssetUsages { get; }
    }

    public interface IAssetUsage
    {
        public int FileKey { get; init; }
        public int UIndex { get; init; }
    }

    public sealed record FileNameDirKeyPair(string FileName, int DirectoryKey) { public FileNameDirKeyPair() : this(default, default) { } }

    public enum BioMorphSpecies
    {
        Unknown,
        HumanMale,
        HumanFemale,
        Asari,
        Salarian,
        Turian,
        Krogan,
        Batarian
    }

    public static class BioMorphSpeciesExtensions
    {
        public static BioMorphSpecies GetBioMorphSpecies(this string baseHeadName)
        {
            string name = baseHeadName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return BioMorphSpecies.Unknown;
            }

            return name.ToUpperInvariant() switch
            {
                string value when value.StartsWith("HMM_") => BioMorphSpecies.HumanMale,
                string value when value.StartsWith("HMF_") => BioMorphSpecies.HumanFemale,
                string value when value.StartsWith("ASA_") => BioMorphSpecies.Asari,
                string value when value.StartsWith("SAL_") || value.StartsWith("SLM_") => BioMorphSpecies.Salarian,
                string value when value.StartsWith("TUR_") || value.StartsWith("TUF_") => BioMorphSpecies.Turian,
                string value when value.StartsWith("KRO_") => BioMorphSpecies.Krogan,
                string value when value.StartsWith("BAT_") => BioMorphSpecies.Batarian,
                _ => BioMorphSpecies.Unknown
            };
        }

        public static string ToDisplayName(this BioMorphSpecies species) => species switch
        {
            BioMorphSpecies.HumanMale => "human male",
            BioMorphSpecies.HumanFemale => "human female",
            BioMorphSpecies.Asari => "asari",
            BioMorphSpecies.Salarian => "salarian",
            BioMorphSpecies.Turian => "turian",
            BioMorphSpecies.Krogan => "krogan",
            BioMorphSpecies.Batarian => "batarian",
            _ => "unknown"
        };
    }

    public sealed record BioMorphFeatureRecord(string Name, float Value)
    {
        public BioMorphFeatureRecord() : this(default, default) { }
    }

    public sealed record BioMorphScalarRecord(string Name, float Value)
    {
        public BioMorphScalarRecord() : this(default, default) { }
    }

    public sealed record BioMorphColorRecord(string Name, float R, float G, float B, float A)
    {
        public BioMorphColorRecord() : this(default, default, default, default, default) { }
    }

    public sealed record BioMorphFaceRecord(
        string MorphName,
        string BaseHeadName,
        BioMorphSpecies Species,
        int FileKey,
        int UIndex,
        bool IsMod,
        BioMorphFeatureRecord[] Features,
        BioMorphScalarRecord[] ScalarOverrides,
        BioMorphColorRecord[] ColorOverrides)
    {
        public BioMorphFaceRecord() : this(default, default, default, default, default, default, [], [], []) { }
    }

    public enum BioPlanetMaterialLayer : byte
    {
        Planet,
        Cloud
    }

    public sealed record BioPlanetScalarParameterRecord(string Name, float Value)
    {
        public BioPlanetScalarParameterRecord() : this(default, default) { }
    }

    public sealed record BioPlanetVectorParameterRecord(string Name, float R, float G, float B, float A)
    {
        public BioPlanetVectorParameterRecord() : this(default, default, default, default, default) { }
    }

    /// <summary>
    /// Effective MIC override values referenced by one BioPlanet material layer. Profiles generated
    /// from official packages provide bounded reference populations for Galaxy Map randomization.
    /// </summary>
    public sealed record BioPlanetMaterialProfileRecord(
        string PlanetName,
        string MaterialName,
        BioPlanetMaterialLayer Layer,
        int FileKey,
        int PlanetUIndex,
        int MaterialUIndex,
        bool IsMod,
        bool IsDlc,
        BioPlanetScalarParameterRecord[] Scalars,
        BioPlanetVectorParameterRecord[] Vectors)
    {
        public BioPlanetMaterialProfileRecord() : this(default, default, default, default, default, default,
            default, default, [], []) { }
    }

    public class PlotUsageDB
    {
        public List<PlotRecord> Bools { get; set; } = new();
        public List<PlotRecord> Ints { get; set; } = new();
        public List<PlotRecord> Floats { get; set; } = new();
        public List<PlotRecord> Conditionals { get; set; } = new();
        public List<PlotRecord> Transitions { get; set; } = new();
        public PlotUsageDB()
        {
        }

        public void ClearRecords()
        {
            Bools.Clear();
            Ints.Clear();
            Floats.Clear();
            Conditionals.Clear();
            Transitions.Clear();
        }

        public void AddRecords(PlotUsageDB fromDb)
        {
            Bools.AddRange(fromDb.Bools);
            Ints.AddRange(fromDb.Ints);
            Floats.AddRange(fromDb.Floats);
            Conditionals.AddRange(fromDb.Conditionals);
            Transitions.AddRange(fromDb.Transitions);
        }

        public bool Any()
        {
            return Bools.Any() || Ints.Any() || Floats.Any() || Conditionals.Any() || Transitions.Any();
        }

        public void LoadPlotPaths(MEGame game)
        {
            foreach (var plot in Bools.Concat(Ints).Concat(Floats).Concat(Conditionals).Concat(Transitions))
            {
                plot.LoadPath(game);
            }
        }
    }

    public class ClassRecord : IAssetRecord
    {
        public string Class { get; set; }

        public int DefinitionFile { get; set; }

        public int DefinitionUIndex { get; set; }

        public string SuperClass { get; set; }

        public bool IsModOnly { get; set; }

        public PropertyRecord[] PropertyRecords { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages.Cast<IAssetUsage>(); // Boxes ClassUsages to IAssetRecord
        public ClassUsage[] Usages { get; set; }

        public ClassRecord(string @class, int definitionFile, int definitionUIndex, string superClass, PropertyRecord[] propertyRecords, ClassUsage[] usages)
        {
            this.Class = @class;
            this.DefinitionFile = definitionFile;
            this.DefinitionUIndex = definitionUIndex;
            this.SuperClass = superClass;
            this.PropertyRecords = propertyRecords;
            this.Usages = usages;
        }

        public ClassRecord()
        {
            DefinitionFile = -1;
            PropertyRecords = Array.Empty<PropertyRecord>();
            Usages = Array.Empty<ClassUsage>();
        }
    }
    public readonly record struct PropertyRecord(string Property, string Type) { public PropertyRecord() : this(default, default) { } }

    public struct ClassUsage : IAssetUsage
    {
        public int FileKey { get; init; }

        //There are millions of ClassUsage instances in a typical db, so bitpacking here can result in major memory savings (>100mb on a lightly modded LE3).
        //UIndex is stored as a 30 bit integer which is still way more bits than are neccesary for any possible file.
        [IgnoredMember]
        private uint _data;
        private const uint ISDEFAULT_MASK = (uint)1 << 31;
        private const uint ISMOD_MASK = (uint)1 << 30;
        private const uint UINDEX_MASK = ~(ISDEFAULT_MASK | ISMOD_MASK);
        public int UIndex
        {
            get => (int)(_data << 2) >> 2;
            init => _data |= (uint)value & UINDEX_MASK;
        }

        public bool IsDefault
        {
            get => (_data & ISDEFAULT_MASK) != 0;
            set
            {
                if (value)
                {
                    _data |= ISDEFAULT_MASK;
                }
                else
                {
                    _data &= ~ISDEFAULT_MASK;
                }
            }
        }

        public bool IsMod
        {
            get => (_data & ISMOD_MASK) != 0;
            set
            {
                if (value)
                {
                    _data |= ISMOD_MASK;
                }
                else
                {
                    _data &= ~ISMOD_MASK;
                }
            }
        }

        public ClassUsage(int fileKey, int uIndex, bool isDefault, bool isMod)
        {
            FileKey = fileKey;
            _data = default;
            UIndex = uIndex;
            IsDefault = isDefault;
            IsMod = isMod;
        }

        public ClassUsage()
        {
            FileKey = default;
            _data = default;
        }
    }

    public class MaterialRecord : IAssetRecord
    {
        public string MaterialName { get; set; }

        /// <summary>
        /// Containing memory package
        /// </summary>
        public string ParentPackage { get; set; }

        public bool IsDLCOnly { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;
        public List<MatUsage> Usages { get; set; } = new();

        public List<MatSetting> MatSettings { get; set; } = new();

        public string[] UsedOn { get; set; } = [];

        public string ParentMaterialKey { get; set; }

        public string DisplayString => $"{MaterialName} ({ParentPackage})";

        public MaterialRecord(string MaterialName, string ParentPackage, bool IsDLCOnly, IEnumerable<MatSetting> MatSettings, IEnumerable<string> UsedOn = null, string ParentMaterialKey = null)
        {
            this.MaterialName = MaterialName;
            this.ParentPackage = ParentPackage;
            this.IsDLCOnly = IsDLCOnly;
            this.MatSettings.AddRange(MatSettings);
            this.UsedOn = UsedOn?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
            this.ParentMaterialKey = ParentMaterialKey;
        }

        public MaterialRecord()
        { }
    }

    public sealed record MatUsage(int FileKey, int UIndex, bool IsInDLC) : IAssetUsage
    {
        public MatUsage() : this(default, default, default) { }
    }

    public sealed record MatSetting(string Name, string Parm1, string Parm2)
    {
        public MatSetting() : this(default, default, default) { }
    }

    public class AnimationRecord : IAssetRecord
    {
        public string AnimSequence { get; set; }

        public string SeqName { get; set; }

        public string AnimData { get; set; }

        public float Length { get; set; }

        public int Frames { get; set; }

        public string Compression { get; set; }

        public string KeyFormat { get; set; }

        public bool IsAmbPerf { get; set; }

        public bool IsModOnly { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<AnimUsage> Usages { get; set; } = new();

        public AnimationRecord(string AnimSequence, string SeqName, string AnimData, float Length, int Frames, string Compression, string KeyFormat, bool IsAmbPerf, bool IsModOnly)
        {
            this.AnimSequence = AnimSequence;
            this.SeqName = SeqName;
            this.AnimData = AnimData;
            this.Length = Length;
            this.Frames = Frames;
            this.Compression = Compression;
            this.KeyFormat = KeyFormat;
            this.IsAmbPerf = IsAmbPerf;
            this.IsModOnly = IsModOnly;
        }

        public AnimationRecord()
        { }
    }

    public sealed record AnimUsage(int FileKey, int UIndex, bool IsInMod) : IAssetUsage
    {
        public AnimUsage() : this(default, default, default) { }
    }

    public class MeshRecord : IAssetRecord
    {
        public string MeshName { get; set; }

        public bool IsSkeleton { get; set; }

        public int BoneCount { get; set; }

        public bool IsModOnly { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;
        public List<MeshUsage> Usages { get; set; } = new();

        public string DisplayString
        {
            get
            {
                if (IsSkeleton)
                {
                    return $"{MeshName} (Skeletal Mesh, Bone Count: {BoneCount})";
                }

                return MeshName;
            }
        }

        public MeshRecord(string MeshName, bool IsSkeleton, bool IsModOnly, int BoneCount)
        {
            this.MeshName = MeshName;
            this.IsSkeleton = IsSkeleton;
            this.BoneCount = BoneCount;
            this.IsModOnly = IsModOnly;
        }

        public MeshRecord()
        { }
    }

    public sealed record MeshUsage(int FileKey, int UIndex, bool IsInMod) : IAssetUsage
    {
        public MeshUsage() : this(default, default, default) { }
    }

    public class ParticleSysRecord : IAssetRecord
    {
        public enum VFXClass
        {
            ParticleSystem,
            RvrClientEffect,
            BioVFXTemplate
        }

        public string DisplayString => $"{PSName} ({VFXType})";

        public string PSName { get; set; }

        public string ParentPackage { get; set; }

        public bool IsDLCOnly { get; set; }

        public bool IsModOnly { get; set; }

        public int EffectCount { get; set; }

        public VFXClass VFXType { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;
        public List<ParticleSysUsage> Usages { get; set; } = new();

        public ParticleSysRecord(string PSName, string ParentPackage, bool IsDLCOnly, bool IsModOnly, int EffectCount, VFXClass VFXType)
        {
            this.PSName = PSName;
            this.ParentPackage = ParentPackage;
            this.IsDLCOnly = IsDLCOnly;
            this.IsModOnly = IsModOnly;
            this.EffectCount = EffectCount;
            this.VFXType = VFXType;
        }

        public ParticleSysRecord()
        { }
    }

    public sealed record ParticleSysUsage(int FileKey, int UIndex, bool IsInDLC, bool IsInMod) : IAssetUsage
    {
        public ParticleSysUsage() : this(default, default, default, default) { }
    }

    public class TextureRecord : IAssetRecord
    {
        public string TextureName { get; set; }

        public string ParentPackage { get; set; }

        public bool IsDLCOnly { get; set; }

        public bool IsModOnly { get; set; }

        public string CFormat { get; set; }

        public string TexGrp { get; set; }

        public int SizeX { get; set; }

        public int SizeY { get; set; }

        public string CRC { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<TextureUsage> Usages { get; set; } = new();

        public TextureRecord(string TextureName, string ParentPackage, bool IsDLCOnly, bool IsModOnly, string CFormat, string TexGrp, int SizeX, int SizeY, string CRC)
        {
            this.TextureName = TextureName;
            this.ParentPackage = ParentPackage;
            this.IsDLCOnly = IsDLCOnly;
            this.IsModOnly = IsModOnly;
            this.CFormat = CFormat;
            this.TexGrp = TexGrp;
            this.SizeX = SizeX;
            this.SizeY = SizeY;
            this.CRC = CRC;
        }

        public TextureRecord()
        { }
    }

    public sealed record TextureUsage(int FileKey, int UIndex, bool IsInDLC, bool IsInMod) : IAssetUsage
    {
        public TextureUsage() : this(default, default, default, default) { }
    }

    public class GUIElement : IAssetRecord
    {
        public string GUIName { get; set; }

        public int DataSize { get; set; }

        public bool IsModOnly { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<GUIUsage> Usages { get; set; } = new(); //File reference then export

        public GUIElement(string GUIName, int DataSize, bool IsModOnly)
        {
            this.GUIName = GUIName;
            this.DataSize = DataSize;
            this.IsModOnly = IsModOnly;
        }

        public GUIElement()
        { }
    }

    public sealed record GUIUsage(int FileKey, int UIndex, bool IsInMod) : IAssetUsage
    {
        public GUIUsage() : this(default, default, default) { }
    }

    public class Conversation
    {
        public string ConvName { get; set; }

        public bool IsAmbient { get; set; }

        public FileKeyExportPair ConvFile { get; set; } //file, export

        public string PackageName { get; set; }

        public int ConversationExportIndex { get; set; }

        public int OwnerObjectRef { get; set; }

        public string OwnerFriendlyName { get; set; }

        public Conversation(string ConvName, bool IsAmbient, FileKeyExportPair ConvFile, string packageName = null, int conversationExportIndex = 0, int ownerObjectRef = 0, string ownerFriendlyName = null)
        {
            this.ConvName = ConvName;
            this.IsAmbient = IsAmbient;
            this.ConvFile = ConvFile;
            PackageName = packageName;
            ConversationExportIndex = conversationExportIndex;
            OwnerObjectRef = ownerObjectRef;
            OwnerFriendlyName = ownerFriendlyName;
        }

        public Conversation()
        { }
    }

    public sealed record FileKeyExportPair(int FileKey, int UIndex) : IAssetUsage
    {
        public FileKeyExportPair() : this(default, default) { }
    }

    public class ConvoLine
    {
        public int StrRef { get; set; }

        public string Speaker { get; set; }

        public string Line { get; set; }

        public string DisplayLine => StripWrappingQuotes(Line);

        public string Convo { get; set; }

        public ConvoLine(int StrRef, string Speaker, string Convo)
        {
            this.StrRef = StrRef;
            this.Speaker = Speaker;
            this.Convo = Convo;
        }

        public ConvoLine()
        { }

        private static string StripWrappingQuotes(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
            {
                return text;
            }

            return text[0] switch
            {
                '"' when text[^1] == '"' => text[1..^1],
                '“' when text[^1] == '”' => text[1..^1],
                _ => text
            };
        }
    }

    public enum SequenceEventType
    {
        ActivateRemoteEvent,
        ConsoleCommand,
        RemoteEvent,
        ConsoleEvent
    }

    public class SequenceEventRecord : IAssetRecord
    {
        public string SequenceEventValue { get; set; }

        public SequenceEventType EventType { get; set; }

        public bool IsDLCOnly { get; set; }

        public bool IsModOnly { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<SequenceEventUsage> Usages { get; set; } = new();

        [IgnoredMember]
        public string TypeDisplay => EventType switch
        {
            SequenceEventType.ActivateRemoteEvent => "Activate Remote Event",
            SequenceEventType.ConsoleCommand => "Console Command",
            SequenceEventType.RemoteEvent => "Remote Event",
            SequenceEventType.ConsoleEvent => "Console Event",
            _ => EventType.ToString()
        };

        [IgnoredMember]
        public string DisplayString => $"{SequenceEventValue} ({TypeDisplay})";

        public SequenceEventRecord(string sequenceEventValue, SequenceEventType eventType, bool isDLCOnly, bool isModOnly)
        {
            SequenceEventValue = sequenceEventValue;
            EventType = eventType;
            IsDLCOnly = isDLCOnly;
            IsModOnly = isModOnly;
        }

        public SequenceEventRecord()
        { }
    }

    public sealed record SequenceEventUsage(int FileKey, int UIndex, bool IsInDLC, bool IsInMod) : IAssetUsage
    {
        public SequenceEventUsage() : this(default, default, default, default) { }
    }

    public enum TlkUsageContext
    {
        Package,
        Quest,
        Codex,
        Coalesced
    }

    public sealed record TlkUsage(int FileKey, int UIndex, bool IsInDLC, bool IsInMod, TlkUsageContext Context, int? ContainerID = null, string InnerFileName = null, string ReferenceName = null) : IAssetUsage
    {
        public TlkUsage() : this(default, default, default, default, default, default, default, default) { }

        [IgnoredMember]
        public string ContextDisplayString => Context switch
        {
            TlkUsageContext.Package => "Package",
            TlkUsageContext.Quest => "Quest Map",
            TlkUsageContext.Codex => "Codex Map",
            TlkUsageContext.Coalesced => "Coalesced",
            _ => Context.ToString()
        };

        [IgnoredMember]
        public string ReferenceDisplay => string.IsNullOrWhiteSpace(ReferenceName)
            ? ContextDisplayString
            : $"{ContextDisplayString} - {ReferenceName}";
    }

    public class TlkStringRecord : IAssetRecord
    {
        public int StringID { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<TlkUsage> Usages { get; set; } = new();

        public TlkStringRecord(int stringID)
        {
            StringID = stringID;
        }

        public TlkStringRecord()
        { }
    }

    public enum PlotRecordType
    {
        Bool,
        Int,
        Float,
        Conditional,
        Transition
    }

    public enum PlotUsageContext
    {
        Package,
        Sequence,
        Dialogue,
        Conditional,
        CndFile,
        Transition,
        Quest,
        Codex,
        BoolTaskEval,
        IntTaskEval,
        FloatTaskEval,
        Bio2DA
    }
    public static class PlotRecordEnumExtensions
    {
        public static string ToTool(this PlotUsageContext puc) => puc switch
        {
            PlotUsageContext.Sequence => "SeqEd",
            PlotUsageContext.Dialogue => "DlgEd",
            PlotUsageContext.Package => "PackageEd",
            PlotUsageContext.Conditional => "PackageEd",
            PlotUsageContext.CndFile => "CndEd",
            PlotUsageContext.Bio2DA => "PackageEd",
            _ => "PlotEd"
        };

        public static string ToDisplayString(this PlotUsageContext puc) => puc switch
        {
            PlotUsageContext.Dialogue => "Dialogue - StrRef",
            PlotUsageContext.BoolTaskEval => "Bool Task Eval",
            PlotUsageContext.IntTaskEval => "Int Task Eval",
            PlotUsageContext.FloatTaskEval => "Float Task Eval",
            PlotUsageContext.CndFile => "Conditional",
            _ => puc.ToString()
        };

        public static PlotElementType ToPlotElementType(this PlotRecordType prt) => prt switch
        {
            PlotRecordType.Bool => PlotElementType.State,
            PlotRecordType.Int => PlotElementType.Integer,
            PlotRecordType.Float => PlotElementType.Float,
            PlotRecordType.Conditional => PlotElementType.Conditional,
            PlotRecordType.Transition => PlotElementType.Transition,
            _ => PlotElementType.None
        };
    }

    public class PlotRecord : IAssetRecord
    {
        public PlotRecordType ElementType { get; set; }

        public int ElementID { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<PlotUsage> Usages { get; set; } = new();

        public PlotUsage BaseUsage { get; set; }

        [IgnoredMember]
        public string Path { get; set; }

        [IgnoredMember]
        public string DisplayText => $"{ElementType} {ElementID}{(string.IsNullOrEmpty(Path) ? "" : $" - {Path}")}";

        public PlotRecord(PlotRecordType type, int id)
        {
            this.ElementType = type;
            this.ElementID = id;
        }

        public PlotRecord()
        { }

        public void LoadPath(MEGame game) =>
            Path = PlotDatabases.FindPlotPathFromID(ElementID, ElementType.ToPlotElementType(), game);
    }

    public class PlotUsage : IAssetUsage
    {
        public int? ContainerID { get; set; }
        public int FileKey { get; init; }
        public int UIndex { get; init; }
        public bool IsMod { get; set; }
        public PlotUsageContext Context { get; set; }
        [IgnoredMember] 
        public string ContextDisplayString => Context.ToDisplayString();

        public PlotUsage(int filekey, int uindex, bool ismod, PlotUsageContext context = PlotUsageContext.Package, int? containerID = null)
        {
            FileKey = filekey;
            UIndex = uindex;
            IsMod = ismod;
            Context = context;
            ContainerID = containerID;
        }
        public PlotUsage()
        {
        }
    }

    public enum ActorType
    {
        StuntActor,
        SkeletalMeshActor,
        Pawn,
        PointOfInterest
    }

    public class ActorRecord : IAssetRecord
    {
        public string ActorName { get; set; }

        public string Tag { get; set; }

        public int GameNameStrRef { get; set; }

        public ActorType ActorType { get; set; }

        public bool IsModOnly { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<ActorUsage> Usages { get; set; } = new();

        [IgnoredMember]
        public string TypeDisplay => ActorType switch
        {
            ActorType.StuntActor => "SFXStuntActor",
            ActorType.SkeletalMeshActor => "SFXSkeletalMeshActor",
            ActorType.Pawn => "SFXPawn",
            ActorType.PointOfInterest => "SFXPointOfInterest",
            _ => ActorType.ToString()
        };

        [IgnoredMember]
        public string DisplayString => string.IsNullOrWhiteSpace(Tag)
            ? $"{ActorName} ({TypeDisplay})"
            : $"{ActorName} [{Tag}] ({TypeDisplay})";

        public ActorRecord(string actorName, string tag, int gameNameStrRef, ActorType actorType, bool isModOnly)
        {
            ActorName = actorName;
            Tag = tag;
            GameNameStrRef = gameNameStrRef;
            ActorType = actorType;
            IsModOnly = isModOnly;
        }

        public ActorRecord()
        { }
    }

    public sealed record ActorUsage(int FileKey, int UIndex, bool IsInMod) : IAssetUsage
    {
        public ActorUsage() : this(default, default, default) { }
    }

    public class GestureTrackRecord : IAssetRecord, INotifyPropertyChanged
    {
        private string _nodeTlkString;

        public event PropertyChangedEventHandler PropertyChanged;

        public string TrackName { get; set; }

        public int NodeStrRef { get; set; }

        [IgnoredMember]
        public string NodeTlkString
        {
            get => _nodeTlkString;
            set
            {
                if (_nodeTlkString != value)
                {
                    _nodeTlkString = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NodeTlkString)));
                }
            }
        }

        public string StartingPoseSet { get; set; }

        public string StartingPoseAnim { get; set; }

        public List<GestureDataRecord> Gestures { get; set; } = new();

        public bool IsModOnly { get; set; }

        [IgnoredMember] public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<GestureTrackUsage> Usages { get; set; } = new();

        public GestureTrackRecord(string trackName, int nodeStrRef, string startingPoseSet, string startingPoseAnim, IEnumerable<GestureDataRecord> gestures, bool isModOnly)
        {
            TrackName = trackName;
            NodeStrRef = nodeStrRef;
            StartingPoseSet = startingPoseSet;
            StartingPoseAnim = startingPoseAnim;
            Gestures.AddRange(gestures);
            IsModOnly = isModOnly;
        }

        public GestureTrackRecord()
        { }
    }

    public class GestureDataRecord
    {
        public string PoseSet { get; set; }

        public string PoseAnim { get; set; }

        public string GestureSet { get; set; }

        public string GestureAnim { get; set; }

        public string TransitionSet { get; set; }

        public string TransitionAnim { get; set; }

        public GestureDataRecord(string poseSet, string poseAnim, string gestureSet, string gestureAnim, string transitionSet, string transitionAnim)
        {
            PoseSet = poseSet;
            PoseAnim = poseAnim;
            GestureSet = gestureSet;
            GestureAnim = gestureAnim;
            TransitionSet = transitionSet;
            TransitionAnim = transitionAnim;
        }

        public GestureDataRecord()
        { }
    }

    public sealed record GestureTrackUsage(int FileKey, int UIndex, bool IsInMod) : IAssetUsage
    {
        public GestureTrackUsage() : this(default, default, default) { }
    }

    public class PropActionRecord : IAssetRecord
    {
        public string PropName { get; set; }
        public string ActionName { get; set; }
        public int SourceFileKey { get; set; }
        public int SourceTrackUIndex { get; set; }
        public int SourceKeyIndex { get; set; } = -1;
        public int SourceWeaponUIndex { get; set; }
        public int SourceClientEffectUIndex { get; set; }
        public string ClientEffectPath { get; set; }
        public bool HasEffects { get; set; }

        [IgnoredMember]
        public IEnumerable<IAssetUsage> AssetUsages => Usages;

        public List<PropActionUsage> Usages { get; set; } = new();

        public PropActionRecord(
            string propName,
            string actionName,
            int sourceFileKey,
            int sourceTrackUIndex,
            int sourceKeyIndex,
            int sourceWeaponUIndex,
            int sourceClientEffectUIndex,
            string clientEffectPath,
            bool hasEffects,
            bool isMod)
        {
            PropName = propName;
            ActionName = actionName;
            SourceFileKey = sourceFileKey;
            SourceTrackUIndex = sourceTrackUIndex;
            SourceKeyIndex = sourceKeyIndex;
            SourceWeaponUIndex = sourceWeaponUIndex;
            SourceClientEffectUIndex = sourceClientEffectUIndex;
            ClientEffectPath = clientEffectPath;
            HasEffects = hasEffects;
            int sourceUIndex = sourceTrackUIndex != 0
                ? sourceTrackUIndex
                : sourceWeaponUIndex != 0
                    ? sourceWeaponUIndex
                    : sourceClientEffectUIndex;
            Usages.Add(new PropActionUsage(sourceFileKey, sourceUIndex, isMod));
        }

        public PropActionRecord()
        { }
    }

    public sealed record PropActionUsage(int FileKey, int UIndex, bool IsInMod) : IAssetUsage
    {
        public PropActionUsage() : this(default, default, default) { }
    }
}
