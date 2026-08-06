using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MediaColor = System.Windows.Media.Color;

namespace LegendaryExplorer.UserControls.ExportLoaderControls
{
    public sealed class LiveMaterialEditorMaterial : NotifyPropertyChangedBase
    {
        internal ILiveMaterialRenderProxy RenderProxy { get; }
        public ExportEntry MaterialExport => RenderProxy.MaterialExport;
        public IEntry SourceEntry { get; }
        public string DisplayName { get; }
        public string SourcePath { get; }
        public bool CanSaveToCurrent => SourceEntry is ExportEntry export
                                               && export.FileRef == MaterialExport.FileRef
                                               && export.IsA("MaterialInstanceConstant");
        public bool CanCreateNew => SourceEntry is not null;

        public ObservableCollectionExtended<LiveScalarMaterialParameter> ScalarParameters { get; } = [];
        public ObservableCollectionExtended<LiveVectorMaterialParameter> VectorParameters { get; } = [];
        private readonly Dictionary<string, float> _removedScalarValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LinearColor> _removedVectorValues = new(StringComparer.OrdinalIgnoreCase);

        private bool _hasUnsavedChanges;
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set => SetProperty(ref _hasUnsavedChanges, value);
        }

        internal LiveMaterialEditorMaterial(ILiveMaterialRenderProxy renderProxy, IEntry sourceEntry,
            string displayName = null, string sourcePath = null)
        {
            RenderProxy = renderProxy;
            SourceEntry = sourceEntry;
            DisplayName = displayName
                          ?? $"{SourceEntry?.ObjectName.Instanced ?? MaterialExport.ObjectName.Instanced} ({SourceEntry?.ClassName ?? MaterialExport.ClassName})";
            SourcePath = sourcePath ?? SourceEntry?.InstancedFullPath ?? MaterialExport.InstancedFullPath;

            foreach ((string name, float value) in renderProxy.ScalarParameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase))
            {
                ScalarParameters.Add(new LiveScalarMaterialParameter(this, name, value));
            }

            foreach ((string name, LinearColor value) in renderProxy.VectorParameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase))
            {
                VectorParameters.Add(new LiveVectorMaterialParameter(this, name, value));
            }
        }

        internal void MarkChanged() => HasUnsavedChanges = true;
        internal void MarkSaved() => HasUnsavedChanges = false;

        internal LiveScalarMaterialParameter AddScalarParameter(string parameterName)
        {
            LiveScalarMaterialParameter existing = ScalarParameters.FirstOrDefault(parameter =>
                parameter.ParameterName.Equals(parameterName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            float value = _removedScalarValues.Remove(parameterName, out float removedValue)
                ? removedValue
                : RenderProxy.ScalarParameters.GetValueOrDefault(parameterName);
            RenderProxy.SetScalarParameter(parameterName, value);
            var parameter = new LiveScalarMaterialParameter(this, parameterName, value);
            ScalarParameters.Insert(GetInsertionIndex(ScalarParameters.Select(item => item.ParameterName), parameterName), parameter);
            MarkChanged();
            return parameter;
        }

        internal LiveVectorMaterialParameter AddVectorParameter(string parameterName)
        {
            LiveVectorMaterialParameter existing = VectorParameters.FirstOrDefault(parameter =>
                parameter.ParameterName.Equals(parameterName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            LinearColor value = _removedVectorValues.Remove(parameterName, out LinearColor removedValue)
                ? removedValue
                : RenderProxy.VectorParameters.GetValueOrDefault(parameterName, LinearColor.Black);
            RenderProxy.SetVectorParameter(parameterName, value);
            var parameter = new LiveVectorMaterialParameter(this, parameterName, value);
            VectorParameters.Insert(GetInsertionIndex(VectorParameters.Select(item => item.ParameterName), parameterName), parameter);
            MarkChanged();
            return parameter;
        }

        internal bool RemoveScalarParameter(LiveScalarMaterialParameter parameter)
        {
            if (parameter is null || !ScalarParameters.Remove(parameter))
            {
                return false;
            }

            _removedScalarValues[parameter.ParameterName] = parameter.Value;
            RenderProxy.RemoveScalarParameter(parameter.ParameterName);
            MarkChanged();
            return true;
        }

        internal bool RemoveVectorParameter(LiveVectorMaterialParameter parameter)
        {
            if (parameter is null || !VectorParameters.Remove(parameter))
            {
                return false;
            }

            _removedVectorValues[parameter.ParameterName] = new LinearColor(parameter.R, parameter.G, parameter.B, parameter.A);
            RenderProxy.RemoveVectorParameter(parameter.ParameterName);
            MarkChanged();
            return true;
        }

        private static int GetInsertionIndex(IEnumerable<string> parameterNames, string parameterName)
        {
            int index = 0;
            foreach (string existingName in parameterNames)
            {
                if (StringComparer.OrdinalIgnoreCase.Compare(existingName, parameterName) > 0)
                {
                    break;
                }
                index++;
            }
            return index;
        }
    }

    public sealed class LiveScalarMaterialParameter : NotifyPropertyChangedBase
    {
        private readonly LiveMaterialEditorMaterial _owner;
        private float _value;

        public string ParameterName { get; }
        public float Value
        {
            get => _value;
            set
            {
                if (float.IsFinite(value) && SetProperty(ref _value, value))
                {
                    _owner.RenderProxy.SetScalarParameter(ParameterName, value);
                    _owner.MarkChanged();
                }
            }
        }

        internal LiveScalarMaterialParameter(LiveMaterialEditorMaterial owner, string parameterName, float value)
        {
            _owner = owner;
            ParameterName = parameterName;
            _value = value;
        }
    }

    public sealed class LiveVectorMaterialParameter : NotifyPropertyChangedBase
    {
        private readonly LiveMaterialEditorMaterial _owner;
        private float _r;
        private float _g;
        private float _b;
        private float _a;

        public string ParameterName { get; }
        public float R { get => _r; set => SetComponent(ref _r, value); }
        public float G { get => _g; set => SetComponent(ref _g, value); }
        public float B { get => _b; set => SetComponent(ref _b, value); }
        public float A { get => _a; set => SetComponent(ref _a, value); }

        public MediaColor? PreviewColor
        {
            get => MediaColor.FromArgb(ToByte(A), ToByte(PreviewColorSpace.LinearToSrgb(R)),
                ToByte(PreviewColorSpace.LinearToSrgb(G)), ToByte(PreviewColorSpace.LinearToSrgb(B)));
            set
            {
                if (value is { } color)
                {
                    SetFromColor(color);
                }
            }
        }

        internal LiveVectorMaterialParameter(LiveMaterialEditorMaterial owner, string parameterName, LinearColor value)
        {
            _owner = owner;
            ParameterName = parameterName;
            _r = value.R;
            _g = value.G;
            _b = value.B;
            _a = value.A;
        }

        public void SetFromColor(MediaColor color)
        {
            bool changed = false;
            changed |= SetProperty(ref _r, PreviewColorSpace.SrgbToLinear(color.R / 255f), nameof(R));
            changed |= SetProperty(ref _g, PreviewColorSpace.SrgbToLinear(color.G / 255f), nameof(G));
            changed |= SetProperty(ref _b, PreviewColorSpace.SrgbToLinear(color.B / 255f), nameof(B));
            changed |= SetProperty(ref _a, color.A / 255f, nameof(A));
            if (changed)
            {
                ApplyValue();
            }
        }

        public void SetValue(float r, float g, float b, float a)
        {
            if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b) || !float.IsFinite(a))
            {
                return;
            }

            bool changed = false;
            changed |= SetProperty(ref _r, r, nameof(R));
            changed |= SetProperty(ref _g, g, nameof(G));
            changed |= SetProperty(ref _b, b, nameof(B));
            changed |= SetProperty(ref _a, a, nameof(A));
            if (changed)
            {
                ApplyValue();
            }
        }

        private void SetComponent(ref float field, float value)
        {
            if (float.IsFinite(value) && SetProperty(ref field, value))
            {
                ApplyValue();
            }
        }

        private void ApplyValue()
        {
            _owner.RenderProxy.SetVectorParameter(ParameterName, new LinearColor(R, G, B, A));
            _owner.MarkChanged();
            OnPropertyChanged(nameof(PreviewColor));
        }

        private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }
}
