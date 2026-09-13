using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using static LegendaryExplorer.UserControls.ExportLoaderControls.FaceFXAnimSetEditorControl;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator;

/// <summary>A detached line and name table for previewing generation before applying it.</summary>
internal sealed class FaceFXGenerationDraft : IFaceFXBinary
{
    public FaceFXAnimSet AnimSet { get; }
    public FaceFXLine Line => AnimSet.Lines[0];
    public List<string> Names => AnimSet.Names;
    public List<FaceFXLine> Lines => AnimSet.Lines;
    public ObjectBinary Binary => AnimSet;

    public FaceFXGenerationDraft(IFaceFXBinary source, FaceFXLine line, MEGame game)
    {
        AnimSet = FaceFXAnimSet.Create(game);
        AnimSet.Names = new List<string>(source.Names);
        AnimSet.Lines = [line.Clone()];
    }

    public void ApplyTo(IFaceFXBinary target, FaceFXLine line)
    {
        var remappedNames = new List<int>();
        foreach (int nameIndex in Line.AnimationNames)
        {
            string name = Names[nameIndex];
            int targetIndex = target.Names.IndexOf(name);
            if (targetIndex < 0)
            {
                targetIndex = target.Names.Count;
                target.Names.Add(name);
            }
            remappedNames.Add(targetIndex);
        }
        line.AnimationNames = remappedNames;
        line.NumKeys = Line.NumKeys.ToList();
        line.Points = Line.Points.ToList();
    }
}
