using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork: skeletal actors are posed with the animation they're configured with (a SkeletalMeshActor's AnimNodeSequence, a stunt actor's gesture
/// default pose), sampled at <see cref="ActorPreviewAnimation.RepresentativePoseFraction"/>; the selected actor's animation is labelled in the viewport.
/// Also selects actors the Create menu wrote to the package once the preview refresh has added them
/// </summary>
public partial class LevelEditor
{
    //actors whose configured animation was looked for (each view once; a rebuilt view is a new one)
    private readonly ConditionalWeakTable<ActorProxy, object> animationConfiguredActors = new();
    private bool previewAnimationPassQueued;

    private void ForkActors_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Remove) return;
        if (_pendingForkSelect is { } pending
            && Actors.FirstOrDefault(actor => actor.Export is { } export && export.FileRef == pending.Package && export.UIndex == pending.UIndex) is { } created)
        {
            _pendingForkSelect = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (Actors.Contains(created)) SelectAndRevealActor(created, false);
            });
        }
        if (previewAnimationPassQueued) return;
        previewAnimationPassQueued = true;
        // After the load or refresh that added them, so posing (which reads and decompresses animations) doesn't hold it up
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyPendingPreviewAnimations);
    }

    private void ApplyPendingPreviewAnimations()
    {
        previewAnimationPassQueued = false;
        if (IsBakePreview) return;
        foreach (ActorProxy actor in Actors.ToList())
        {
            //a duplicate (a draft until its commit) is posed as the actor it's a copy of
            if ((actor.Export is null && actor is not ActorCloneProxy { SavedSource: not null }) || actor.IsDisposed
                || animationConfiguredActors.TryGetValue(actor, out _)) continue;
            animationConfiguredActors.AddOrUpdate(actor, null);
            try
            {
                ConfigurePreviewAnimation(actor);
            }
            catch (Exception)
            {
                //(a pose is only a preview: the actor keeps its reference pose)
            }
        }
    }

    private void ConfigurePreviewAnimation(ActorProxy actor)
    {
        ActorPreviewAnimation animation;
        if (actor is ActorCloneProxy { SavedSource: { } saved })
        {
            animation = saved.IsA("SFXStuntActor") ? AnimationFromStuntGestureModule(saved)
                : saved.IsA("SkeletalMeshActor") ? AnimationFromSkeletalMeshComponent(saved, actor)
                : null;
        }
        else
        {
            animation = actor switch
            {
                SFXStuntActorProxy => AnimationFromStuntGestureModule(actor.Export),
                SkeletalMeshActorProxy => AnimationFromSkeletalMeshComponent(actor.Export, actor),
                _ => null
            };
        }
        if (animation?.Resolve(RenderContext) is not { } sequence) return;
        actor.SetAnimation(sequence, sequence.SequenceLength * ActorPreviewAnimation.RepresentativePoseFraction);
        actor.PreviewAnimationName = animation.DisplayName;
    }

    private ActorPreviewAnimation AnimationFromSkeletalMeshComponent(ExportEntry actorExport, ActorProxy actor)
    {
        //(a copy's stand-in stands for its source's component, which it's drawn from)
        ExportEntry componentExport = actorExport.GetProperty<ObjectProperty>("SkeletalMeshComponent")?.ResolveToEntry(actorExport.FileRef) as ExportEntry
                                      ?? (actor?.Components.OfType<SkeletalMeshComponentProxy>().FirstOrDefault() is { } first ? first.CopiedFrom ?? first.Export : null);
        if (componentExport is null) return null;
        IMEPackage package = componentExport.FileRef;
        PropertyCollection componentProperties = componentExport.GetProperties();

        ExportEntry animationNode = componentProperties.GetProp<ObjectProperty>("Animations") is { Value: not 0 } nodeReference
            ? nodeReference.ResolveToEntry(package) as ExportEntry
            : null;
        animationNode ??= package.Exports.FirstOrDefault(export =>
            export.idxLink == componentExport.UIndex && export.ClassName is "AnimNodeSequence" or "BioAnimNodeSequence");
        NameReference animationName = animationNode?.GetCondensedProperties().GetProp<NameProperty>("AnimSeqName")?.Value ?? NameReference.None;

        List<int> animSetReferences = componentProperties.GetProp<ArrayProperty<ObjectProperty>>("AnimSets")?
            .Where(reference => reference.Value != 0)
            .Select(reference => reference.Value)
            .ToList() ?? [];
        if (animSetReferences.Count == 0)
        {
            animSetReferences.AddRange(package.Exports
                .Where(export => export.idxLink == componentExport.UIndex && export.ClassName is "AnimSet" or "BioDynamicAnimSet")
                .Select(export => export.UIndex));
        }
        return CreatePreviewAnimation(package, animationName, animSetReferences);
    }

    private static ActorPreviewAnimation AnimationFromStuntGestureModule(ExportEntry actorExport)
    {
        IMEPackage package = actorExport.FileRef;
        ExportEntry gestureModule = null;
        if (actorExport.GetProperty<ArrayProperty<ObjectProperty>>("Modules") is { } modules)
        {
            foreach (ObjectProperty moduleReference in modules)
            {
                if (moduleReference.ResolveToEntry(package) is ExportEntry { ClassName: "SFXModule_Gestures" } candidate)
                {
                    gestureModule = candidate;
                    break;
                }
            }
        }
        gestureModule ??= package.Exports.FirstOrDefault(export => export.idxLink == actorExport.UIndex && export.ClassName == "SFXModule_Gestures");

        PropertyCollection gestureProperties = gestureModule?.GetCondensedProperties();
        NameReference animationName = gestureProperties?.GetProp<NameProperty>("m_nmDefaultPoseAnim")?.Value ?? NameReference.None;
        List<int> animSetReferences = [];
        if (gestureProperties?.GetProp<ObjectProperty>("m_pDefaultPoseSet") is { Value: not 0 } defaultPoseSet)
        {
            animSetReferences.Add(defaultPoseSet.Value);
        }
        if (animSetReferences.Count == 0 && gestureModule is not null)
        {
            animSetReferences.AddRange(package.Exports
                .Where(export => export.idxLink == gestureModule.UIndex && export.ClassName is "AnimSet" or "BioDynamicAnimSet")
                .Select(export => export.UIndex));
        }
        return CreatePreviewAnimation(package, animationName, animSetReferences);
    }

    private static ActorPreviewAnimation CreatePreviewAnimation(IMEPackage sourcePackage, NameReference animationName, IReadOnlyList<int> animSetReferences)
    {
        if (sourcePackage is null || animSetReferences.Count == 0
            || string.IsNullOrWhiteSpace(animationName.Name)
            || animationName.Name.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new ActorPreviewAnimation(sourcePackage, animationName, animSetReferences);
    }
}
