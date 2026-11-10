using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LegendaryExplorer.Tools.AssetDatabase.CutsceneCorpus
{
    // Records written to the cutscene corpus. Property names are serialized camelCase.
    // Every class needs a parameterless constructor and settable properties so per-file
    // results can be cached to disk and read back for incremental rescans.

    /// <summary>Everything pass 1 extracts from a single package file. Cached per file.</summary>
    public class CorpusFileResult
    {
        public int CorpusVersion { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string Dlc { get; set; }
        public bool IsMod { get; set; }
        public long FileSize { get; set; }
        public long FileTimeUtcTicks { get; set; }
        public string Localization { get; set; }
        /// <summary>size:timestamp:version:localization of the package when it was scanned.</summary>
        public string CacheKey { get; set; }
        public List<ConversationRecord> Conversations { get; set; } = [];
        public List<StageRecord> Stages { get; set; } = [];
        public List<ConversationStageRecord> ConversationStages { get; set; } = [];
        public List<AnimationRecord> Animations { get; set; } = [];
        public List<LevelCameraRecord> LevelCameras { get; set; } = [];
        public List<string> Errors { get; set; } = [];
    }

    /// <summary>A CameraActor placed in a level; matinee groups can bind to it by tag instead of owning a TrackMove.</summary>
    public class LevelCameraRecord
    {
        public string File { get; set; }
        public string Path { get; set; }
        public string Class { get; set; }
        public string Tag { get; set; }
        public OriginRecord Origin { get; set; }
        public float? Fov { get; set; }
    }

    public class SpeakerRecord
    {
        public int Index { get; set; }
        public string Tag { get; set; }
    }

    public class StageDirectionRecord
    {
        public int StrRef { get; set; }
        public string LineText { get; set; }
        public string Direction { get; set; }
    }

    public class ConversationRecord
    {
        /// <summary>Stable id: "{package file without extension}:{conversation name}".</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        public string File { get; set; }
        public string Dlc { get; set; }
        public bool IsMod { get; set; }
        public int UIndex { get; set; }
        public string Path { get; set; }
        /// <summary>The non-localized package that normally holds this conversation's StartConversation and BioStage.</summary>
        public string MainPackage { get; set; }
        public bool IsAmbient { get; set; }
        public List<SpeakerRecord> Speakers { get; set; } = [];
        /// <summary>Entry node ids the conversation can start on (m_StartingList).</summary>
        public List<string> StartNodes { get; set; } = [];
        public List<string> Scripts { get; set; } = [];
        public List<StageDirectionRecord> StageDirections { get; set; } = [];
        public int EntryCount { get; set; }
        public int ReplyCount { get; set; }
        public string MatineeSequence { get; set; }
        /// <summary>Filled in pass 2: ids of matching conversation_stages records, best match first.</summary>
        public List<string> StageContextIds { get; set; }
        /// <summary>Filled in pass 2: the stage context used to measure shots.</summary>
        public string ChosenStageContextId { get; set; }
        public string ChosenStageReason { get; set; }

        /// <summary>Kept in the per-file cache; set to null before writing conversations.jsonl (nodes go to nodes.jsonl).</summary>
        public List<NodeRecord> Nodes { get; set; } = [];
    }

    public class ReplyLinkRecord
    {
        public string Target { get; set; }
        public int ParaphraseStrRef { get; set; }
        public string Paraphrase { get; set; }
        public string Category { get; set; }
    }

    public class NodeRecord
    {
        public string ConversationId { get; set; }
        /// <summary>"E{n}" for entry (NPC) nodes, "R{n}" for reply (player) nodes.</summary>
        public string Id { get; set; }
        public string Kind { get; set; }
        public int Index { get; set; }
        public bool IsStart { get; set; }
        public int StrRef { get; set; }
        public string Text { get; set; }
        public string StageDirection { get; set; }
        public int SpeakerIndex { get; set; }
        public string Speaker { get; set; }
        public int ListenerIndex { get; set; }
        public string Listener { get; set; }
        public string ReplyType { get; set; }
        public string GuiStyle { get; set; }
        public int CameraIntimacy { get; set; }
        public Dictionary<string, bool> Flags { get; set; }
        public int ConditionalFunc { get; set; }
        public int ConditionalParam { get; set; }
        public bool FireConditional { get; set; }
        public int StateTransition { get; set; }
        public int StateTransitionParam { get; set; }
        public int ScriptIndex { get; set; }
        public int ExportId { get; set; }
        /// <summary>Entry nodes: the player replies offered after this line.</summary>
        public List<ReplyLinkRecord> Replies { get; set; }
        /// <summary>Reply nodes: the entry nodes this reply can lead to.</summary>
        public List<string> Next { get; set; }
        public string InterpData { get; set; }
        public TimelineRecord Timeline { get; set; }
        /// <summary>Filled in pass 2.</summary>
        public List<ShotRecord> Shots { get; set; }
    }

    public class TimelineRecord
    {
        public float Length { get; set; }
        public List<TrackRecord> Tracks { get; set; } = [];
        /// <summary>Director cuts: time and target InterpGroup name.</summary>
        public List<CutRecord> Cuts { get; set; } = [];
        /// <summary>BioEvtSysTrackSwitchCamera keys: time and BioStage camera tag.</summary>
        public List<CutRecord> StageCameraSwitches { get; set; } = [];
        public List<int> VoStrRefs { get; set; } = [];
        /// <summary>Matinee camera groups (groups the director cuts to, or groups named cam*).</summary>
        public List<string> CameraGroups { get; set; } = [];
    }

    public class CutRecord
    {
        public float T { get; set; }
        public string Target { get; set; }
        public float TransitionTime { get; set; }
        public bool ForceCrossingLineOfAction { get; set; }
    }

    public class TrackRecord
    {
        public string Group { get; set; }
        public string GroupFindActor { get; set; }
        public string Class { get; set; }
        public string Title { get; set; }
        /// <summary>Actor the track drives: m_nmFindActor, else the group's find actor, else the group name.</summary>
        public string Actor { get; set; }
        /// <summary>Short category: gesture, camera_move, actor_move, fov, director, stage_camera, lookat, facing, dof, lighting, vo, facefx, faceonly_vo, prop, interrupt, anim, sound, event, fade, other.</summary>
        public string Kind { get; set; }
        /// <summary>Non-key track properties (starting pose, MoveFrame, PropertyName, ...).</summary>
        public JsonObject Settings { get; set; }
        /// <summary>One object per key; every key has "t" (seconds from node start) plus its data.</summary>
        public List<JsonObject> Keys { get; set; } = [];
    }

    public class ShotRecord
    {
        public float Start { get; set; }
        public float End { get; set; }
        /// <summary>stage (BioStage camera bone) or matinee (InterpGroup with TrackMove).</summary>
        public string Source { get; set; }
        public string Camera { get; set; }
        public float? Fov { get; set; }
        public float? FovEnd { get; set; }
        public string FovSource { get; set; }
        public float[] WorldPosition { get; set; }
        public float[] WorldRotation { get; set; }
        public string Subject { get; set; }
        public string SubjectRole { get; set; }
        /// <summary>Where the subject's position came from: node_track (this node's actor TrackMove), previous_node (carried along the conversation flow), or stage_slot.</summary>
        public string SubjectPositionSource { get; set; }
        /// <summary>Set when no actor is in frame: the actor closest to the view direction and its angle off-centre.</summary>
        public string NearestActor { get; set; }
        public float? NearestActorAngle { get; set; }
        public List<string> InFrame { get; set; }
        public float? Distance { get; set; }
        /// <summary>Height of the frame (cm) at the subject's distance. Bins: ECU &lt;40, CU &lt;75, MCU &lt;115, MS &lt;170, MWS &lt;250, FS &lt;400, WS.</summary>
        public float? FrameHeight { get; set; }
        public string Size { get; set; }
        /// <summary>Camera azimuth around the subject in degrees: 0 = in front of their face, +90 = their left, -90 = their right, 180 = behind.</summary>
        public float? Azimuth { get; set; }
        public float? HeightAboveEyes { get; set; }
        public float? Pitch { get; set; }
        public string Angle { get; set; }
        public string OverShoulderOf { get; set; }
        public bool TwoShot { get; set; }
        /// <summary>Side of the speaker-&gt;listener line: left or right.</summary>
        public string LineSide { get; set; }
        /// <summary>"A|B" with tags sorted, and the side of the A-&gt;B line, so continuity can be compared across nodes.</summary>
        public string LinePair { get; set; }
        public string LineSideCanonical { get; set; }
        /// <summary>Camera relative to the subject's actor origin: forward/right/up (cm) and roll/pitch/yaw (degrees). Same convention as Camera Presets.</summary>
        public float[] LocalPosition { get; set; }
        public float[] LocalRotation { get; set; }
        public List<string> Move { get; set; }
        public Dictionary<string, float> MoveDelta { get; set; }
        public List<string> Notes { get; set; }
    }

    public class OriginRecord
    {
        public float[] Position { get; set; }
        /// <summary>Roll, pitch, yaw in degrees.</summary>
        public float[] Rotation { get; set; }
    }

    public class StageBoneRecord
    {
        public string Name { get; set; }
        public int Index { get; set; }
        public int Parent { get; set; }
        /// <summary>camera, node, puppet, dummy, root, other.</summary>
        public string Kind { get; set; }
        public OriginRecord World { get; set; }
        /// <summary>Relative to the stage origin: forward/right/up and roll/pitch/yaw.</summary>
        public OriginRecord Local { get; set; }
    }

    public class StageCameraRecord
    {
        public string Tag { get; set; }
        public OriginRecord World { get; set; }
        public OriginRecord Local { get; set; }
        public float? Fov { get; set; }
        public float NearPlane { get; set; }
        public bool DisableHeightAdjustment { get; set; }
        /// <summary>Merged BioStageCamera properties (DOF, deltas), instance overrides applied last.</summary>
        public JsonObject Settings { get; set; }
    }

    public class StageRecord
    {
        /// <summary>"{package file}#{uindex}".</summary>
        public string Id { get; set; }
        public string File { get; set; }
        public string Dlc { get; set; }
        public bool IsMod { get; set; }
        public string Path { get; set; }
        public string Tag { get; set; }
        public string Archetype { get; set; }
        public string Mesh { get; set; }
        public OriginRecord Origin { get; set; }
        public List<StageBoneRecord> Bones { get; set; } = [];
        public List<StageCameraRecord> Cameras { get; set; } = [];
    }

    public class ConversationStageRecord
    {
        /// <summary>"{package file}#{StartConversation uindex}@{stage uindex}".</summary>
        public string Id { get; set; }
        public string ConversationName { get; set; }
        public string ConversationPath { get; set; }
        public string File { get; set; }
        public string StartConversation { get; set; }
        public string StartConversationClass { get; set; }
        public string StageId { get; set; }
        public string StageTag { get; set; }
        /// <summary>StartConversation variable link name to what is plugged in (actor tags, Player, stage).</summary>
        public Dictionary<string, string> Links { get; set; } = [];
        /// <summary>Resolved actor origins keyed by tag, slot name (NodeN), Owner and Player.</summary>
        public Dictionary<string, OriginRecord> Actors { get; set; } = [];
    }

    public class AnimationRecord
    {
        public string Set { get; set; }
        public string Sequence { get; set; }
        public float Length { get; set; }
        public int Frames { get; set; }
        public float RateScale { get; set; }
        public string Path { get; set; }
        public string File { get; set; }
    }

    public class CameraMoveRecord
    {
        public string Id { get; set; }
        public string ConversationId { get; set; }
        public string NodeId { get; set; }
        public string Group { get; set; }
        public string Subject { get; set; }
        public string SubjectRole { get; set; }
        public string Size { get; set; }
        public List<string> Move { get; set; }
        public Dictionary<string, float> MoveDelta { get; set; }
        public float Duration { get; set; }
        public string LineText { get; set; }
        public string StageDirection { get; set; }
        /// <summary>Keys relative to the subject actor origin (Camera Preset convention).</summary>
        public List<LocalCameraKeyRecord> LocalKeys { get; set; } = [];
        public List<float[]> FovKeys { get; set; }
    }

    public class LocalCameraKeyRecord
    {
        public float T { get; set; }
        public float[] Position { get; set; }
        public float[] Rotation { get; set; }
        public string Interp { get; set; }
        public float[] ArriveTangent { get; set; }
        public float[] LeaveTangent { get; set; }
    }

    public class CorpusManifest
    {
        public int CorpusVersion { get; set; }
        public string Game { get; set; }
        public string Localization { get; set; }
        public bool IncludesMods { get; set; }
        public string GeneratedUtc { get; set; }
        public double ScanSeconds { get; set; }
        public int FilesConsidered { get; set; }
        public int FilesScanned { get; set; }
        public int FilesFromCache { get; set; }
        public Dictionary<string, int> Counts { get; set; } = [];
        public int ErrorCount { get; set; }
        public List<string> Files { get; set; } = [];
    }
}
