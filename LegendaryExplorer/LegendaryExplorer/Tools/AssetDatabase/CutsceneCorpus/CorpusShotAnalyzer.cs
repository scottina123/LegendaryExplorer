using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.CutsceneCorpus
{
    /// <summary>
    /// Pass 2: turns a node's cuts and cameras into measured shots relative to the actors of the chosen stage context.
    /// Actor positions come from the node's own actor TrackMoves when present, otherwise from the stage slots.
    /// </summary>
    internal sealed class CorpusShotAnalyzer
    {
        /// <summary>Eye height above a pawn origin (the pawn origin sits about 88cm above the floor).</summary>
        public const float EyeHeightAboveActorOrigin = 75f;
        /// <summary>Stage slot bones sit on the floor; a pawn standing on one has its origin this far above.</summary>
        public const float PawnOriginAboveFloor = 88f;
        public const float DefaultFov = 70f;
        private const float AspectRatio = 16f / 9f;

        private sealed class ActorEntry
        {
            public string Tag;
            public readonly HashSet<string> Aliases = new(StringComparer.OrdinalIgnoreCase);
            public CameraOrigin Base;
        }

        private readonly record struct ActorPose(ActorEntry Entry, CameraOrigin Origin, string PositionSource)
        {
            public string Tag => Entry.Tag;
        }

        private readonly List<ActorEntry> stageActors = [];
        private readonly Dictionary<string, StageCameraRecord> stageCameras = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LevelCameraRecord> levelCameras = new(StringComparer.OrdinalIgnoreCase);

        public CorpusShotAnalyzer(ConversationStageRecord context, StageRecord stage, IEnumerable<LevelCameraRecord> levelCameras = null)
        {
            foreach (LevelCameraRecord camera in levelCameras ?? [])
            {
                this.levelCameras.TryAdd(camera.Tag, camera);
            }
            if (context is not null)
            {
                var origins = new Dictionary<string, CameraOrigin>(StringComparer.OrdinalIgnoreCase);
                foreach ((string tag, OriginRecord origin) in context.Actors)
                {
                    origins[tag] = ToCameraOrigin(origin);
                }

                // Raw slot entries (Node1, Puppet1_1, ...) and actors that resolved to exactly a slot are floor-level;
                // lift them to pawn-origin height so they match the Player origin and actor TrackMoves.
                var slotKeys = origins.Keys.Where(key => context.Links.ContainsKey(key)
                                                         && key is not ("Owner" or "Player" or "Stage")).ToList();
                foreach (string key in origins.Keys.ToList())
                {
                    if (slotKeys.Any(slot => Vector3.DistanceSquared(origins[slot].Location, origins[key].Location) < 1f))
                    {
                        CameraOrigin raw = origins[key];
                        origins[key] = new CameraOrigin(raw.Location + new Vector3(0, 0, PawnOriginAboveFloor), raw.Rotation);
                    }
                }

                // One entry per physical position; prefer real tags over Owner/Player aliases and NodeN slots.
                foreach (var group in origins.GroupBy(pair => Vector3Key(pair.Value.Location)))
                {
                    var entry = new ActorEntry
                    {
                        Tag = group.Select(pair => pair.Key)
                            .OrderBy(name => name.StartsWith("Node", StringComparison.OrdinalIgnoreCase)
                                             || name.StartsWith("Puppet", StringComparison.OrdinalIgnoreCase) ? 2
                                : name is "Owner" or "Player" ? 1 : 0)
                            .First(),
                        Base = group.First().Value
                    };
                    foreach (var pair in group)
                    {
                        entry.Aliases.Add(pair.Key);
                    }
                    stageActors.Add(entry);
                }

                // The Owner link names the owner's tag; make sure that tag aliases the Owner entry.
                if (context.Links.TryGetValue("Owner", out string ownerTag) && !string.IsNullOrWhiteSpace(ownerTag)
                    && stageActors.FirstOrDefault(entry => entry.Aliases.Contains("Owner")) is { } owner
                    && !ownerTag.Contains(' '))
                {
                    owner.Aliases.Add(ownerTag);
                }
            }

            foreach (StageCameraRecord camera in stage?.Cameras ?? [])
            {
                stageCameras[camera.Tag] = camera;
            }
        }

        /// <summary>
        /// Measures the shots of one node. <paramref name="carriedPoses"/> are actor positions left by the previous
        /// node in the conversation flow (from its actor TrackMoves); <paramref name="exitPoses"/> returns the
        /// positions this node leaves behind for the next one.
        /// </summary>
        public List<ShotRecord> Analyze(NodeRecord node, List<CameraMoveRecord> moves, string conversationId,
            IReadOnlyDictionary<string, CameraOrigin> carriedPoses, out Dictionary<string, CameraOrigin> exitPoses)
        {
            exitPoses = carriedPoses is null
                ? new Dictionary<string, CameraOrigin>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CameraOrigin>(carriedPoses, StringComparer.OrdinalIgnoreCase);
            TimelineRecord timeline = node.Timeline;
            if (timeline is null)
            {
                return null;
            }

            // Actors for this node: stage actors, plus anyone moved here or earlier that the stage does not know.
            var nodeActors = new List<(ActorEntry Entry, TrackRecord Move, CameraOrigin? Carried)>();
            foreach (ActorEntry entry in stageActors)
            {
                nodeActors.Add((entry, null, null));
            }
            foreach ((string tag, CameraOrigin origin) in carriedPoses ?? new Dictionary<string, CameraOrigin>())
            {
                int index = nodeActors.FindIndex(item => item.Entry.Aliases.Contains(tag));
                if (index >= 0)
                {
                    nodeActors[index] = (nodeActors[index].Entry, null, origin);
                }
                else
                {
                    var entry = new ActorEntry { Tag = tag, Base = origin };
                    entry.Aliases.Add(tag);
                    nodeActors.Add((entry, null, origin));
                }
            }
            foreach (TrackRecord track in timeline.Tracks.Where(track => track.Kind == "actor_move" && track.Keys.Count > 0))
            {
                int index = nodeActors.FindIndex(item => item.Entry.Aliases.Contains(track.Actor ?? "")
                                                         || item.Entry.Aliases.Contains(track.Group ?? ""));
                if (index >= 0)
                {
                    if (nodeActors[index].Move is null)
                    {
                        nodeActors[index] = (nodeActors[index].Entry, track, nodeActors[index].Carried);
                    }
                }
                else if (!IsRelativeMove(track))
                {
                    var entry = new ActorEntry { Tag = track.Actor ?? track.Group, Base = EvaluateMove(track, 0) };
                    entry.Aliases.Add(entry.Tag);
                    if (track.Group is not null) entry.Aliases.Add(track.Group);
                    nodeActors.Add((entry, track, null));
                }
                if (!IsRelativeMove(track))
                {
                    exitPoses[track.Actor ?? track.Group] = EvaluateMove(track, Math.Max(timeline.Length, GetFloat(track.Keys[^1], "t")));
                }
            }

            List<(float T, string Source, string Camera)> events = BuildCameraEvents(timeline);

            var shots = new List<ShotRecord>();
            var subjects = new List<CameraOrigin?>();
            for (int i = 0; i < events.Count; i++)
            {
                (float start, string source, string cameraName) = events[i];
                float end = i + 1 < events.Count ? events[i + 1].T : Math.Max(timeline.Length, start);
                // Stage-camera shots put actors back on their stage marks, so positions carried from earlier
                // matinee nodes only apply to matinee/level shots.
                bool stageShot = source == "stage";
                List<ActorPose> poses = nodeActors
                    .Where(item => !stageShot || item.Move is not null || stageActors.Contains(item.Entry))
                    .Select(item => item.Move is not null && !IsRelativeMove(item.Move)
                        ? new ActorPose(item.Entry, EvaluateMove(item.Move, start + 0.001f), "node_track")
                        : !stageShot && item.Carried is { } carried
                            ? new ActorPose(item.Entry, carried, "previous_node")
                            : new ActorPose(item.Entry, item.Entry.Base, "stage_slot"))
                    .ToList();
                ShotRecord shot = BuildShot(node, timeline, start, end, source, cameraName, poses, out CameraOrigin? subject);
                shots.Add(shot);
                subjects.Add(subject);
            }

            BuildCameraMoves(node, timeline, shots, subjects, moves, conversationId);
            return shots;
        }

        /// <summary>
        /// Director cuts decide who owns the view: a cut to a group with a TrackMove is a matinee camera; a cut to the
        /// group holding the SwitchCamera track ("Conversation") hands control to the BioStage cameras, which then
        /// follow the switch keys. Without a director, the switch keys alone drive the cuts.
        /// </summary>
        private List<(float T, string Source, string Camera)> BuildCameraEvents(TimelineRecord timeline)
        {
            var switches = timeline.StageCameraSwitches.Where(cut => !string.IsNullOrEmpty(cut.Target)).ToList();
            var events = new List<(float T, string Source, string Camera)>();
            if (timeline.Cuts.Count == 0)
            {
                events.AddRange(switches.Select(cut => (cut.T, "stage", cut.Target)));
            }
            else
            {
                var stageGroups = timeline.Tracks.Where(track => track.Kind == "stage_camera").Select(track => track.Group)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                stageGroups.Add("Conversation");
                var cuts = timeline.Cuts.ToList();
                if (cuts[0].T > 0.001f && switches.Count > 0)
                {
                    cuts.Insert(0, new CutRecord { T = 0, Target = "Conversation" });
                }
                for (int i = 0; i < cuts.Count; i++)
                {
                    float start = cuts[i].T;
                    float end = i + 1 < cuts.Count ? cuts[i + 1].T : float.MaxValue;
                    string group = cuts[i].Target;
                    if (FindTrack(timeline, group, "camera_move") is not null)
                    {
                        events.Add((start, "matinee", group));
                    }
                    else if (group is not null && stageGroups.Contains(group))
                    {
                        CutRecord active = switches.LastOrDefault(cut => cut.T <= start + 0.001f);
                        events.Add((start, "stage", active?.Target));
                        events.AddRange(switches.Where(cut => cut.T > start + 0.001f && cut.T < end).Select(cut => (cut.T, "stage", cut.Target)));
                    }
                    else if (LevelCameraFor(timeline, group) is not null)
                    {
                        events.Add((start, "level", group));
                    }
                    else
                    {
                        events.Add((start, "unresolved", group));
                    }
                }
            }
            events.Sort((a, b) => a.T.CompareTo(b.T));

            // Merge consecutive cuts to the same camera.
            var merged = new List<(float T, string Source, string Camera)>();
            foreach (var cameraEvent in events)
            {
                if (merged.Count > 0 && merged[^1].Source == cameraEvent.Source && cameraEvent.Camera is not null
                    && string.Equals(merged[^1].Camera, cameraEvent.Camera, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                merged.Add(cameraEvent);
            }
            return merged;
        }

        private LevelCameraRecord LevelCameraFor(TimelineRecord timeline, string group)
        {
            if (group is null || levelCameras.Count == 0)
            {
                return null;
            }
            string findActor = timeline.Tracks.FirstOrDefault(track => string.Equals(track.Group, group, StringComparison.OrdinalIgnoreCase))?.GroupFindActor;
            return findActor is not null && levelCameras.TryGetValue(findActor, out LevelCameraRecord byFindActor) ? byFindActor
                : levelCameras.GetValueOrDefault(group);
        }

        private ShotRecord BuildShot(NodeRecord node, TimelineRecord timeline, float start, float end, string source,
            string cameraName, List<ActorPose> poses, out CameraOrigin? subjectOrigin)
        {
            subjectOrigin = null;
            var shot = new ShotRecord
            {
                Start = CorpusPropertyJson.Round(start),
                End = CorpusPropertyJson.Round(end),
                Camera = cameraName
            };

            CameraOrigin startCamera;
            CameraOrigin endCamera;
            float? fovStart = null;
            float? fovEnd = null;
            TrackRecord moveTrack = source == "matinee" ? FindTrack(timeline, cameraName, "camera_move") : null;
            if (source == "stage" && cameraName is null)
            {
                shot.Source = "stage";
                (shot.Notes ??= []).Add("stage camera carries over from the previous node");
                return shot;
            }
            if (source == "level" && LevelCameraFor(timeline, cameraName) is { } levelCamera)
            {
                shot.Source = "level";
                startCamera = endCamera = ToCameraOrigin(levelCamera.Origin);
                TrackRecord levelFov = FindTrack(timeline, cameraName, "fov");
                if (levelFov is not null && levelFov.Keys.Count > 0)
                {
                    fovStart = EvaluateFloat(levelFov, start + 0.001f);
                    fovEnd = EvaluateFloat(levelFov, Math.Max(start, end - 0.001f));
                    shot.FovSource = "fov_track";
                }
                else if (levelCamera.Fov is { } actorFov)
                {
                    fovStart = fovEnd = actorFov;
                    shot.FovSource = "camera_actor";
                }
                (shot.Notes ??= []).Add($"placed CameraActor {levelCamera.Tag} ({levelCamera.Path})");
            }
            else if (moveTrack is not null && moveTrack.Keys.Count > 0)
            {
                shot.Source = "matinee";
                startCamera = EvaluateMove(moveTrack, start + 0.001f);
                endCamera = EvaluateMove(moveTrack, Math.Max(start, end - 0.001f));
                TrackRecord fovTrack = FindTrack(timeline, cameraName, "fov");
                if (fovTrack is not null && fovTrack.Keys.Count > 0)
                {
                    fovStart = EvaluateFloat(fovTrack, start + 0.001f);
                    fovEnd = EvaluateFloat(fovTrack, Math.Max(start, end - 0.001f));
                    shot.FovSource = "fov_track";
                }
                if (moveTrack.Settings?["MoveFrame"] is JsonValue moveFrame && moveFrame.GetValue<string>() != "IMF_World")
                {
                    (shot.Notes ??= []).Add($"TrackMove uses {moveFrame.GetValue<string>()}; world position may be relative");
                }
            }
            else if (cameraName is not null && stageCameras.TryGetValue(cameraName, out StageCameraRecord stageCamera))
            {
                shot.Source = "stage";
                startCamera = endCamera = ToCameraOrigin(stageCamera.World);
                if (stageCamera.Fov is { } stageFov)
                {
                    fovStart = fovEnd = stageFov;
                    shot.FovSource = "stage_camera";
                }
            }
            else
            {
                shot.Source = source == "stage" ? "stage" : "unresolved";
                bool groupExists = timeline.Tracks.Any(track => string.Equals(track.Group, cameraName, StringComparison.OrdinalIgnoreCase));
                (shot.Notes ??= []).Add(source == "stage"
                    ? "stage camera tag not found on the chosen BioStage"
                    : groupExists
                        ? "director target group has no TrackMove and no matching placed CameraActor"
                        : "director target group does not exist in this node (the view stays on the previous camera)");
                return shot;
            }

            if (fovStart is null)
            {
                shot.FovSource = "default";
            }
            float fov = fovStart ?? DefaultFov;
            shot.Fov = CorpusPropertyJson.Round(fov, 2);
            shot.FovEnd = fovEnd is { } fe && Math.Abs(fe - fov) > 0.01f ? CorpusPropertyJson.Round(fe, 2) : null;
            shot.WorldPosition = CorpusPropertyJson.Vec(startCamera.Location);
            shot.WorldRotation = CorpusPropertyJson.Vec(startCamera.Rotation);

            if (poses.Count == 0)
            {
                (shot.Notes ??= []).Add("no stage context or actor moves: actor-relative measurements unavailable");
                shot.Move = DescribeMove(startCamera, endCamera, fov, fovEnd ?? fov, null, out Dictionary<string, float> noSubjectDelta);
                shot.MoveDelta = noSubjectDelta;
                return shot;
            }

            ActorPose? subject = Measure(shot, node, startCamera, fov, poses);
            if (subject is { } pose)
            {
                subjectOrigin = pose.Origin;
                shot.SubjectPositionSource = pose.PositionSource;
                shot.Move = DescribeMove(startCamera, endCamera, fov, fovEnd ?? fov, pose.Origin, out Dictionary<string, float> delta);
                shot.MoveDelta = delta;
            }
            else
            {
                shot.Move = DescribeMove(startCamera, endCamera, fov, fovEnd ?? fov, null, out Dictionary<string, float> delta);
                shot.MoveDelta = delta;
            }
            return shot;
        }

        private static ActorPose? Measure(ShotRecord shot, NodeRecord node, CameraOrigin camera, float fov, List<ActorPose> poses)
        {
            CameraPresetGenerator.BuildBasis(camera.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
            float halfH = fov * 0.5f;
            float halfV = RadToDeg(MathF.Atan(MathF.Tan(DegToRad(halfH)) / AspectRatio));

            var visible = new List<(ActorPose Pose, float Angle, float Distance)>();
            (ActorPose Pose, float Angle, float Distance)? closestToCenter = null;
            foreach (ActorPose pose in poses)
            {
                Vector3 toEye = Eye(pose.Origin) - camera.Location;
                float x = Vector3.Dot(toEye, forward);
                float y = Vector3.Dot(toEye, right);
                float z = Vector3.Dot(toEye, up);
                float horizontal = RadToDeg(MathF.Atan2(y, x));
                float vertical = RadToDeg(MathF.Atan2(z, MathF.Sqrt(x * x + y * y)));
                float angle = MathF.Sqrt(horizontal * horizontal + vertical * vertical);
                float distance = toEye.Length();
                if (x > 0 && Math.Abs(horizontal) <= halfH && Math.Abs(vertical) <= halfV + 15f)
                {
                    visible.Add((pose, angle, distance));
                }
                if (closestToCenter is null || angle < closestToCenter.Value.Angle)
                {
                    closestToCenter = (pose, angle, distance);
                }
            }
            if (closestToCenter is null)
            {
                return null;
            }

            if (visible.Count == 0)
            {
                // Insert / environment shot, or an actor whose position we could not resolve.
                shot.NearestActor = closestToCenter.Value.Pose.Tag;
                shot.NearestActorAngle = CorpusPropertyJson.Round(closestToCenter.Value.Angle, 1);
                (shot.Notes ??= []).Add("no actor inside the frame (insert/environment shot, or actor position unknown)");
                return null;
            }
            // Prefer actors whose face the camera can see; someone seen from behind is usually the shoulder in an OTS.
            var faceVisible = visible.Where(candidate => Math.Abs(AzimuthOf(candidate.Pose.Origin, camera.Location)) <= 110f).ToList();
            var pool = faceVisible.Count > 0 ? faceVisible : visible;
            var subject = pool.OrderBy(candidate => candidate.Angle).First();
            // A much nearer actor near frame centre is an over-the-shoulder foreground, not the subject.
            if (pool.Count >= 2 && subject.Distance < pool.Max(candidate => candidate.Distance) * 0.7f)
            {
                subject = pool.Where(candidate => candidate.Distance >= subject.Distance / 0.7f).OrderBy(candidate => candidate.Angle).First();
            }
            var foreground = visible.Where(candidate => candidate.Pose.Tag != subject.Pose.Tag
                                                        && (candidate.Distance < subject.Distance * 0.7f
                                                            || (candidate.Distance < subject.Distance
                                                                && Math.Abs(AzimuthOf(candidate.Pose.Origin, camera.Location)) > 110f)))
                .ToList();

            shot.Subject = subject.Pose.Tag;
            shot.SubjectRole = RoleOf(subject.Pose.Entry, node);
            shot.InFrame = visible.Select(candidate => candidate.Pose.Tag).ToList();
            shot.Distance = CorpusPropertyJson.Round(subject.Distance, 1);
            float frameHeight = 2f * subject.Distance * MathF.Tan(DegToRad(halfV));
            shot.FrameHeight = CorpusPropertyJson.Round(frameHeight, 1);
            shot.Size = frameHeight switch
            {
                < 40 => "ECU",
                < 75 => "CU",
                < 115 => "MCU",
                < 170 => "MS",
                < 250 => "MWS",
                < 400 => "FS",
                _ => "WS"
            };

            CameraOrigin subjectOrigin = subject.Pose.Origin;
            Vector3 fromSubject = camera.Location - Eye(subjectOrigin);
            shot.Azimuth = CorpusPropertyJson.Round(AzimuthOf(subjectOrigin, camera.Location), 1);
            shot.HeightAboveEyes = CorpusPropertyJson.Round(fromSubject.Z, 1);
            shot.Pitch = CorpusPropertyJson.Round(camera.Rotation.Y, 1);
            float elevation = RadToDeg(MathF.Atan2(fromSubject.Z, MathF.Sqrt(fromSubject.X * fromSubject.X + fromSubject.Y * fromSubject.Y)));
            shot.Angle = elevation > 15 ? "high" : elevation < -15 ? "low" : "eye_level";

            if (foreground.Count > 0)
            {
                shot.OverShoulderOf = foreground.OrderBy(candidate => candidate.Distance).First().Pose.Tag;
            }
            shot.TwoShot = shot.OverShoulderOf is null && visible.Count >= 2;

            OriginRecord local = CorpusFileExtractor.ToLocal(camera, subjectOrigin);
            shot.LocalPosition = local.Position;
            shot.LocalRotation = local.Rotation;

            ActorPose? speaker = Find(poses, node.Speaker);
            ActorPose? listener = Find(poses, node.Listener);
            if (speaker is { } s && listener is { } l && s.Entry != l.Entry
                && Vector3.DistanceSquared(s.Origin.Location, l.Origin.Location) > 1f)
            {
                shot.LineSide = SideOfLine(s.Origin.Location, l.Origin.Location, camera.Location);
                string a = s.Tag;
                string b = l.Tag;
                bool swap = string.Compare(a, b, StringComparison.OrdinalIgnoreCase) > 0;
                shot.LinePair = swap ? $"{b}|{a}" : $"{a}|{b}";
                shot.LineSideCanonical = swap
                    ? SideOfLine(l.Origin.Location, s.Origin.Location, camera.Location)
                    : shot.LineSide;
            }
            return subject.Pose;
        }

        private static List<string> DescribeMove(CameraOrigin start, CameraOrigin end, float fovStart, float fovEnd,
            CameraOrigin? subject, out Dictionary<string, float> delta)
        {
            delta = new Dictionary<string, float>();
            var moves = new List<string>();
            float translation = Vector3.Distance(start.Location, end.Location);
            float yaw = WrapDegrees(end.Rotation.Z - start.Rotation.Z);
            float pitch = WrapDegrees(end.Rotation.Y - start.Rotation.Y);
            float fov = fovEnd - fovStart;
            float height = end.Location.Z - start.Location.Z;
            bool explained = false;

            if (subject is { } s)
            {
                Vector3 eye = Eye(s);
                float distance = Vector3.Distance(end.Location, eye) - Vector3.Distance(start.Location, eye);
                float bearingStart = RadToDeg(MathF.Atan2(start.Location.Y - eye.Y, start.Location.X - eye.X));
                float bearingEnd = RadToDeg(MathF.Atan2(end.Location.Y - eye.Y, end.Location.X - eye.X));
                float azimuth = -WrapDegrees(bearingEnd - bearingStart);
                delta["distance"] = CorpusPropertyJson.Round(distance, 1);
                delta["azimuth"] = CorpusPropertyJson.Round(azimuth, 1);
                if (Math.Abs(distance) > 8) { moves.Add(distance < 0 ? "push_in" : "pull_out"); explained = true; }
                if (Math.Abs(azimuth) > 4) { moves.Add(azimuth > 0 ? "arc_left" : "arc_right"); explained = true; }
            }
            if (Math.Abs(height) > 8) { moves.Add(height > 0 ? "crane_up" : "crane_down"); explained = true; }
            if (translation > 8 && !explained) moves.Add("truck");
            if (Math.Abs(yaw) > 3) moves.Add(yaw > 0 ? "pan_right" : "pan_left");
            if (Math.Abs(pitch) > 3) moves.Add(pitch > 0 ? "tilt_up" : "tilt_down");
            if (Math.Abs(fov) > 1.5f) moves.Add(fov < 0 ? "zoom_in" : "zoom_out");
            if (moves.Count == 0) moves.Add("static");

            delta["translation"] = CorpusPropertyJson.Round(translation, 1);
            delta["height"] = CorpusPropertyJson.Round(height, 1);
            delta["yaw"] = CorpusPropertyJson.Round(yaw, 1);
            delta["pitch"] = CorpusPropertyJson.Round(pitch, 1);
            delta["fov"] = CorpusPropertyJson.Round(fov, 2);
            return moves;
        }

        private static void BuildCameraMoves(NodeRecord node, TimelineRecord timeline, List<ShotRecord> shots,
            List<CameraOrigin?> subjects, List<CameraMoveRecord> moves, string conversationId)
        {
            if (moves is null)
            {
                return;
            }

            var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < shots.Count; i++)
            {
                ShotRecord shot = shots[i];
                if (shot.Source != "matinee" || subjects[i] is not { } subject || shot.Move is null
                    || shot.Move.SequenceEqual(["static"]) || !seenGroups.Add(shot.Camera))
                {
                    continue;
                }
                TrackRecord moveTrack = FindTrack(timeline, shot.Camera, "camera_move");
                if (moveTrack is null)
                {
                    continue;
                }

                CameraPresetGenerator.BuildBasis(subject.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
                var record = new CameraMoveRecord
                {
                    Id = $"{conversationId}:{node.Id}:{shot.Camera}",
                    ConversationId = conversationId,
                    NodeId = node.Id,
                    Group = shot.Camera,
                    Subject = shot.Subject,
                    SubjectRole = shot.SubjectRole,
                    Size = shot.Size,
                    Move = shot.Move,
                    MoveDelta = shot.MoveDelta,
                    LineText = node.Text,
                    StageDirection = node.StageDirection
                };
                float firstTime = GetFloat(moveTrack.Keys[0], "t");
                foreach (JsonObject key in moveTrack.Keys)
                {
                    Vector3 deltaPosition = GetVector(key, "pos") - subject.Location;
                    record.LocalKeys.Add(new LocalCameraKeyRecord
                    {
                        T = CorpusPropertyJson.Round(GetFloat(key, "t") - firstTime),
                        Position = CorpusPropertyJson.Vec(new Vector3(Vector3.Dot(deltaPosition, forward), Vector3.Dot(deltaPosition, right), Vector3.Dot(deltaPosition, up))),
                        Rotation = CorpusPropertyJson.Vec(CameraPresetGenerator.WorldRotationToLocal(GetVector(key, "rot"), subject.Rotation)),
                        Interp = key["interp"]?.GetValue<string>(),
                        ArriveTangent = CorpusPropertyJson.Vec(CameraPresetTrackCapture.ToLocalVector(GetVector(key, "posArrive"), forward, right, up)),
                        LeaveTangent = CorpusPropertyJson.Vec(CameraPresetTrackCapture.ToLocalVector(GetVector(key, "posLeave"), forward, right, up))
                    });
                }
                record.Duration = record.LocalKeys.Count > 0 ? record.LocalKeys[^1].T : 0;
                TrackRecord fovTrack = FindTrack(timeline, shot.Camera, "fov");
                if (fovTrack is not null)
                {
                    record.FovKeys = fovTrack.Keys
                        .Select(key => new[] { CorpusPropertyJson.Round(GetFloat(key, "t") - firstTime), CorpusPropertyJson.Round(GetFloat(key, "OutVal"), 2) })
                        .ToList();
                }
                moves.Add(record);
            }
        }

        /// <summary>Builds a Camera Preset (Interp Editor "saved track move") from a camera move record.</summary>
        public static CameraPreset ToCameraPreset(CameraMoveRecord move, string name)
        {
            List<CameraPresetLocalKey> keys = move.LocalKeys.Select(key => new CameraPresetLocalKey(
                key.T,
                ToVector(key.Position),
                ToVector(key.Rotation),
                Enum.TryParse(key.Interp, out EInterpCurveMode mode)
                    ? MulticamCameraPresetCapture.MapInterpolation(mode)
                    : CameraKeyInterpolation.Linear,
                ToVector(key.ArriveTangent),
                ToVector(key.LeaveTangent))).ToList();
            CameraPresetLocalKey first = keys[0];
            return new CameraPreset(name, CameraPresetCategory.SavedTrackMoves,
                first.LocalPosition.X, first.LocalPosition.Y, first.LocalPosition.Z, 0,
                Duration: move.Duration, KeyCount: keys.Count, Interpolation: first.Interpolation, LocalKeys: keys);
        }

        #region Helpers

        private static Vector3 Eye(CameraOrigin actor) => actor.Location + new Vector3(0, 0, EyeHeightAboveActorOrigin);

        /// <summary>Camera bearing around an actor: 0 = in front of their face, +90 = their left, -90 = their right.</summary>
        private static float AzimuthOf(CameraOrigin actor, Vector3 camera)
        {
            Vector3 fromActor = camera - Eye(actor);
            float bearing = RadToDeg(MathF.Atan2(fromActor.Y, fromActor.X));
            return WrapDegrees(actor.Rotation.Z - bearing);
        }

        private static bool IsRelativeMove(TrackRecord track) =>
            track.Settings?["MoveFrame"] is JsonValue moveFrame && moveFrame.GetValue<string>() != "IMF_World";

        private static TrackRecord FindTrack(TimelineRecord timeline, string group, string kind) =>
            group is null
                ? null
                : timeline.Tracks.FirstOrDefault(track => track.Kind == kind && string.Equals(track.Group, group, StringComparison.OrdinalIgnoreCase));

        private static ActorPose? Find(List<ActorPose> poses, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            foreach (ActorPose pose in poses)
            {
                if (pose.Entry.Aliases.Contains(tag)) return pose;
            }
            return null;
        }

        private static string RoleOf(ActorEntry entry, NodeRecord node)
        {
            if (node.Speaker is not null && entry.Aliases.Contains(node.Speaker)) return "speaker";
            if (node.Listener is not null && entry.Aliases.Contains(node.Listener)) return "listener";
            return "other";
        }

        private static string SideOfLine(Vector3 from, Vector3 to, Vector3 camera)
        {
            // Unreal is left-handed (X forward, Y right); a positive cross product means the camera is right of from->to.
            float cross = (to.X - from.X) * (camera.Y - from.Y) - (to.Y - from.Y) * (camera.X - from.X);
            return cross >= 0 ? "right" : "left";
        }

        private static CameraOrigin EvaluateMove(TrackRecord track, float time)
        {
            (JsonObject a, JsonObject b, float alpha) = Bracket(track.Keys, time);
            Vector3 position = Vector3.Lerp(GetVector(a, "pos"), GetVector(b, "pos"), alpha);
            Vector3 ra = GetVector(a, "rot");
            Vector3 rb = GetVector(b, "rot");
            Vector3 rotation = new(
                ra.X + WrapDegrees(rb.X - ra.X) * alpha,
                ra.Y + WrapDegrees(rb.Y - ra.Y) * alpha,
                ra.Z + WrapDegrees(rb.Z - ra.Z) * alpha);
            return new CameraOrigin(position, rotation);
        }

        private static float EvaluateFloat(TrackRecord track, float time)
        {
            (JsonObject a, JsonObject b, float alpha) = Bracket(track.Keys, time);
            return GetFloat(a, "OutVal") + (GetFloat(b, "OutVal") - GetFloat(a, "OutVal")) * alpha;
        }

        /// <summary>Linear evaluation between keys (curve tangents are ignored; constant keys step).</summary>
        private static (JsonObject A, JsonObject B, float Alpha) Bracket(List<JsonObject> keys, float time)
        {
            if (time <= GetFloat(keys[0], "t")) return (keys[0], keys[0], 0);
            for (int i = 0; i < keys.Count - 1; i++)
            {
                float t0 = GetFloat(keys[i], "t");
                float t1 = GetFloat(keys[i + 1], "t");
                if (time >= t0 && time <= t1)
                {
                    string interp = keys[i]["interp"]?.GetValue<string>() ?? keys[i]["InterpMode"]?.GetValue<string>();
                    if (interp == "CIM_Constant" || t1 - t0 < 1e-5f) return (keys[i], keys[i], 0);
                    return (keys[i], keys[i + 1], (time - t0) / (t1 - t0));
                }
            }
            return (keys[^1], keys[^1], 0);
        }

        internal static float GetFloat(JsonObject obj, string name) =>
            obj?[name] is JsonValue value && value.TryGetValue(out float result) ? result : 0f;

        internal static Vector3 GetVector(JsonObject obj, string name) =>
            obj?[name] is JsonArray { Count: 3 } array
                ? new Vector3(array[0].GetValue<float>(), array[1].GetValue<float>(), array[2].GetValue<float>())
                : Vector3.Zero;

        private static CameraOrigin ToCameraOrigin(OriginRecord origin) =>
            new(ToVector(origin?.Position), ToVector(origin?.Rotation));

        private static Vector3 ToVector(float[] values) =>
            values is { Length: 3 } ? new Vector3(values[0], values[1], values[2]) : Vector3.Zero;

        private static string Vector3Key(Vector3 value) => $"{MathF.Round(value.X)}:{MathF.Round(value.Y)}:{MathF.Round(value.Z)}";

        internal static float WrapDegrees(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees < -180f) degrees += 360f;
            return degrees;
        }

        private static float DegToRad(float degrees) => degrees * MathF.PI / 180f;
        private static float RadToDeg(float radians) => radians * 180f / MathF.PI;

        #endregion
    }
}
