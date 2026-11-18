using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MediaColor = System.Windows.Media.Color;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork window API on upstream's <see cref="ActorProxy"/>, for the fork's outliner rows and Light inspector group: the outliner's second line, the
/// preview animation's name, and the light settings upstream's <see cref="EditableLight"/> doesn't edit (bounced modulation color, classification,
/// other levels to affect)
/// </summary>
public partial class ActorProxy
{
    #region Outliner subtitle

    private bool displaySubtitleResolved;
    private string displaySubtitle;

    /// <summary>
    /// Fork: a second outliner line for actors whose numbered object name says little: an emitter's particle system, a lens flare source's flare.
    /// Read once, when first shown (a refresh that changes it rebuilds the view)
    /// </summary>
    public string DisplaySubtitle
    {
        get
        {
            if (!displaySubtitleResolved)
            {
                displaySubtitleResolved = true;
                displaySubtitle = ResolveDisplaySubtitle();
            }
            return displaySubtitle;
        }
    }

    protected virtual string ResolveDisplaySubtitle()
    {
        if (Export is null) return null;
        try
        {
            if (GlobalUnrealObjectInfo.IsA(Export.ClassName, "Emitter", Export.Game))
            {
                return ParticleSystemExportLoader.ResolveParticleSystemEntry(Export)?.ObjectName.Instanced;
            }
            if (GlobalUnrealObjectInfo.IsA(Export.ClassName, "LensFlareSource", Export.Game))
            {
                ExportEntry flareComponent = Export.GetProperty<ObjectProperty>("LensFlareComp")?.ResolveToEntry(Pcc) as ExportEntry
                    ?? Components.Select(component => component.Export).FirstOrDefault(export => export?.ClassName == "LensFlareComponent");
                return flareComponent?.GetProperty<ObjectProperty>("Template")?.ResolveToEntry(Pcc)?.ObjectName.Instanced;
            }
        }
        catch (Exception)
        {
            //(a subtitle is only a hint)
        }
        return null;
    }

    #endregion

    #region Preview animation

    private string previewAnimationName;

    /// <summary>Fork: the animation the actor is posed with in the viewport (its configured AnimNodeSequence or gesture default pose), or null</summary>
    public string PreviewAnimationName
    {
        get => previewAnimationName;
        internal set => SetProperty(ref previewAnimationName, value);
    }

    #endregion

    #region Fork light settings

    private EditableLight WindowLight => (this as ILightProxy)?.Light;
    private ExportEntry WindowLightComponent => WindowLight?.Component;

    /// <summary>Fork: whether the light has a bounced modulation color (LightEnv_BouncedModulationColor) to edit</summary>
    public virtual bool HasBouncedModulationColor => WindowLightComponent is not null;

    /// <summary>
    /// Fork: the light component's LightEnv_BouncedModulationColor. Written to the component directly (the package update rereads the light in
    /// place, keeping its uncommitted edits)
    /// </summary>
    public virtual MediaColor LightEnv_BouncedModulationColor
    {
        get
        {
            if (WindowLightComponent?.GetProperty<StructProperty>("LightEnv_BouncedModulationColor") is { } colorProp)
            {
                System.Drawing.Color color = CommonStructs.GetColor(colorProp);
                return MediaColor.FromArgb(color.A, color.R, color.G, color.B);
            }
            return WindowLight?.Color ?? default;
        }
        set
        {
            if (IsReadOnly || WindowLightComponent is not { } component) return;
            if (LightEnv_BouncedModulationColor == value) return;
            PropertyCollection props = component.GetProperties();
            props.AddOrReplaceProp(CommonStructs.ColorProp(System.Drawing.Color.FromArgb(value.A, value.R, value.G, value.B), "LightEnv_BouncedModulationColor"));
            component.WriteProperties(props);
            OnPropertyChanged();
            if (ApplyBouncedModulationColor) RefreshForkLightPreview();
        }
    }

    private static readonly ConditionalWeakTable<ExportEntry, object> BouncedColorPreviews = new();

    /// <summary>Fork: whether the viewport previews the light with its bounced modulation color instead of its LightColor (nothing is written)</summary>
    public virtual bool CanApplyBouncedModulationColor => WindowLightComponent is not null;

    public virtual bool ApplyBouncedModulationColor
    {
        get => WindowLightComponent is { } component && BouncedColorPreviews.TryGetValue(component, out _);
        set
        {
            if (WindowLightComponent is not { } component || ApplyBouncedModulationColor == value) return;
            if (value) BouncedColorPreviews.AddOrUpdate(component, null);
            else BouncedColorPreviews.Remove(component);
            OnPropertyChanged();
            RefreshForkLightPreview();
        }
    }

    /// <summary>Fork: the bounced modulation color the viewport previews this light with, if <see cref="ApplyBouncedModulationColor"/> is on</summary>
    internal static bool TryGetPreviewedBouncedColor(ExportEntry component, out System.Drawing.Color color)
    {
        color = default;
        if (component is null || !BouncedColorPreviews.TryGetValue(component, out _)) return false;
        if (component.GetProperty<StructProperty>("LightEnv_BouncedModulationColor") is not { } colorProp) return false;
        color = CommonStructs.GetColor(colorProp);
        return true;
    }

    private void RefreshForkLightPreview()
    {
        if (this is ILightProxy light) Editor?.OnLightEdited(light);
    }

    /// <summary>Fork: whether the actor has a LightAffectsClassification to choose (standalone light actors)</summary>
    public virtual bool HasLightAffectsClassification => WindowLight is not null && this is not CollectionActorComponentProxy && Export is not null;

    public virtual string LightAffectsClassification
    {
        get => Export?.GetProperty<EnumProperty>("LightAffectsClassification")?.Value.Name ?? "LIGHTAFFECTS_All";
        set
        {
            if (IsReadOnly || Export is null || string.IsNullOrEmpty(value) || value == LightAffectsClassification) return;
            PropertyCollection props = Export.GetProperties();
            props.AddOrReplaceProp(new EnumProperty(value, "ELightAffectsClassification", Export.Game, "LightAffectsClassification"));
            Export.WriteProperties(props);
            OnPropertyChanged();
        }
    }

    public virtual IReadOnlyList<string> LightAffectsClassificationValues
    {
        get
        {
            if (Export is not null && GlobalUnrealObjectInfo.GetEnumValues(Export.Game, "ELightAffectsClassification") is { } values)
            {
                return values.Select(value => value.Name).ToList();
            }
            return ["LIGHTAFFECTS_All", "LIGHTAFFECTS_DynamicObjectsOnly", "LIGHTAFFECTS_StaticObjectsOnly", "LIGHTAFFECTS_DynamicThenStaticObjects", "LIGHTAFFECTS_None"];
        }
    }

    private ObservableCollection<string> otherLevelsToAffect;

    /// <summary>Fork: the light component's OtherLevelsToAffect</summary>
    public virtual bool HasOtherLevelsToAffect => WindowLightComponent is not null;

    public virtual ObservableCollection<string> OtherLevelsToAffect
    {
        get
        {
            if (otherLevelsToAffect is null && WindowLightComponent is { } component)
            {
                otherLevelsToAffect = [];
                if (component.GetProperty<ArrayProperty<NameProperty>>("OtherLevelsToAffect") is { } levels)
                {
                    foreach (NameProperty level in levels) otherLevelsToAffect.Add(level.Value.Instanced);
                }
            }
            return otherLevelsToAffect;
        }
    }

    public virtual void AddOtherLevelToAffect(string levelName)
    {
        if (IsReadOnly || WindowLightComponent is null || string.IsNullOrWhiteSpace(levelName)) return;
        OtherLevelsToAffect.Add(levelName.Trim());
        WriteOtherLevelsToAffect();
    }

    public virtual void RemoveOtherLevelFromAffect(string levelName)
    {
        if (IsReadOnly || WindowLightComponent is null) return;
        OtherLevelsToAffect.Remove(levelName);
        WriteOtherLevelsToAffect();
    }

    private void WriteOtherLevelsToAffect()
    {
        if (WindowLightComponent is not { } component) return;
        var levels = new ArrayProperty<NameProperty>("OtherLevelsToAffect");
        foreach (string name in OtherLevelsToAffect) levels.Add(new NameProperty(NameReference.FromInstancedString(name)));
        PropertyCollection props = component.GetProperties();
        props.AddOrReplaceProp(levels);
        component.WriteProperties(props);
    }

    #endregion
}

/// <summary>
/// Fork: the viewport preview of a light whose bounced modulation color is applied to it (<see cref="ActorProxy.ApplyBouncedModulationColor"/>):
/// its LightColor is overridden in the preview only
/// </summary>
internal static class ForkLightPreview
{
    public static Scene3D.LightPreviewOverride Apply(ILightProxy light, Scene3D.LightPreviewOverride preview)
    {
        if (light?.Light?.Component is not { } component || !ActorProxy.TryGetPreviewedBouncedColor(component, out System.Drawing.Color color)) return preview;
        List<Property> properties = preview?.Properties?.Where(p => p.Name.Name != "LightColor").ToList() ?? [];
        properties.Add(CommonStructs.ColorProp(color, "LightColor"));
        return new Scene3D.LightPreviewOverride(preview?.OwnerTransform, properties);
    }
}
