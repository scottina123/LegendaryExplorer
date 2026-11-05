using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorerCore.Unreal.PhysX;

namespace LegendaryExplorerCore.Unreal.BinaryConverters
{
    public class BrushComponent : ObjectBinary
    {
        public KCachedConvexData CachedPhysBrushData;
        protected override void Serialize(SerializingContainer sc)
        {
            sc.Serialize(ref CachedPhysBrushData);
        }

        public static BrushComponent Create()
        {
            return new()
            {
                CachedPhysBrushData = new KCachedConvexData
                {
                    CachedConvexElements = []
                }
            };
        }

        /// <summary>Cooks detached, component-local convex pieces without modifying a package.</summary>
        public static KCachedConvexData CookConvexData(IEnumerable<Vector3[]> pieces, Vector3 scale)
        {
            var elements = new List<KCachedConvexDataElement>();
            using var cooker = new PhysXCooker();
            foreach (var piece in pieces)
            {
                var vertices = new Vector3[piece.Length];
                for (int i = 0; i < piece.Length; i++) vertices[i] = piece[i] * scale * PhysXSDK.UnrealtoPhysXScale;
                elements.Add(new KCachedConvexDataElement { ConvexElementData = cooker.GenerateCachedPhysicsData(vertices) });
            }
            return new KCachedConvexData { CachedConvexElements = elements.ToArray() };
        }

        //appears to work, but the resulting data is untested
        public void RegenCachedPhysBrushData()
        {
            if (Export is null)
            {
                throw new Exception($"No export associated with this {nameof(BrushComponent)}");
            }
            if (!Export.Game.IsLEGame())
            {
                throw new Exception("Can only regenerate CachedPhysBrushData for LE games");
            }
            var props = Export.GetProperties();
            var convexElemsProp = props.GetProp<StructProperty>("BrushAggGeom")?.GetProp<ArrayProperty<StructProperty>>("ConvexElems");
            if (convexElemsProp is null || convexElemsProp.Count is 0)
            {
                //should this be an error?
                return;
            }
            var scale3DProp = props.GetProp<StructProperty>("Scale3D");
            Vector3 scale = (props.GetProp<FloatProperty>("Scale") ?? 1f) * (scale3DProp is null ? Vector3.One : CommonStructs.GetVector3(scale3DProp));
            if (Export.Parent is ExportEntry parent && parent.IsA("Actor"))
            {
                scale *= ActorUtils.GetActorTotalScale(parent.GetProperties());
            }

            CachedPhysBrushData = CookConvexData(convexElemsProp.Select(piece =>
                (piece.GetProp<ArrayProperty<StructProperty>>("VertexData") ?? throw new Exception("Malformed BrushAggGeom"))
                .Select(CommonStructs.GetVector3).ToArray()), scale);

            props.AddOrReplaceProp(new IntProperty(Export.Game is MEGame.LE1 ? 33882375 : 34079762, "CachedPhysBrushDataVersion"));
            Export.WriteProperties(props);
        }
    }
}
