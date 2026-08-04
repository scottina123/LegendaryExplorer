using System;
using System.Linq;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MediaColor = System.Windows.Media.Color;

namespace LegendaryExplorer.UserControls.ExportLoaderControls
{
    public sealed class LiveMaterialEditorMaterial : NotifyPropertyChangedBase
    {
        internal MaterialRenderProxy RenderProxy { get; }
        public ExportEntry MaterialExport => RenderProxy.Export;
        public IEntry SourceEntry { get; }
        public string DisplayName => $"{SourceEntry?.ObjectName.Instanced ?? MaterialExport.ObjectName.Instanced} ({SourceEntry?.ClassName ?? MaterialExport.ClassName})";
        public string SourcePath => SourceEntry?.InstancedFullPath ?? MaterialExport.InstancedFullPath;
        public bool CanSaveToCurrent => SourceEntry is ExportEntry export
                                               && export.FileRef == MaterialExport.FileRef
                                               && export.IsA("MaterialInstanceConstant");
        public bool CanCreateNew => SourceEntry is not null;

        public ObservableCollectionExtended<LiveScalarMaterialParameter> ScalarParameters { get; } = [];
        public ObservableCollectionExtended<LiveVectorMaterialParameter> VectorParameters { get; } = [];

        private bool _hasUnsavedChanges;
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set => SetProperty(ref _hasUnsavedChanges, value);
        }

        internal LiveMaterialEditorMaterial(MaterialRenderProxy renderProxy, IEntry sourceEntry)
        {
            RenderProxy = renderProxy;
            SourceEntry = sourceEntry;

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
            get => MediaColor.FromArgb(ToByte(A), ToByte(R), ToByte(G), ToByte(B));
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
            changed |= SetProperty(ref _r, color.R / 255f, nameof(R));
            changed |= SetProperty(ref _g, color.G / 255f, nameof(G));
            changed |= SetProperty(ref _b, color.B / 255f, nameof(B));
            changed |= SetProperty(ref _a, color.A / 255f, nameof(A));
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
