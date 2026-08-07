using System.Collections.Generic;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.SharedToolControls;

/// <summary>
/// Common surface used by the live material editor for both mesh-preview renderers.
/// </summary>
public interface ILiveMaterialRenderProxy
{
    ExportEntry MaterialExport { get; }
    IReadOnlyDictionary<string, float> ScalarParameters { get; }
    IReadOnlyDictionary<string, LinearColor> VectorParameters { get; }

    void SetScalarParameter(string parameterName, float value);
    void SetVectorParameter(string parameterName, LinearColor value);
    void RemoveScalarParameter(string parameterName);
    void RemoveVectorParameter(string parameterName);
    void CommitPreviewParameterOverrides();
    void ResetPreviewParameterOverrides();
}
