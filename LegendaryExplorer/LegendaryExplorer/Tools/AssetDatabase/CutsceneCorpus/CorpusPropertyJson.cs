using System;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.CutsceneCorpus
{
    /// <summary>
    /// Converts Unreal properties into compact JSON for agents. Vectors become [x,y,z], rotators become
    /// {pitch,yaw,roll} in degrees, colors become "#RRGGBBAA", object references become instanced paths,
    /// and None names / null objects are dropped. String refs keep the number and add "{name}Text" when resolvable.
    /// </summary>
    internal static class CorpusPropertyJson
    {
        public static float Round(float value, int digits = 3) =>
            float.IsFinite(value) ? MathF.Round(value, digits) : 0f;

        public static float[] Vec(Vector3 value, int digits = 2) =>
            [Round(value.X, digits), Round(value.Y, digits), Round(value.Z, digits)];

        public static JsonObject ToObject(PropertyCollection properties, IMEPackage pcc, Func<int, string> tlk,
            Func<Property, bool> skip = null)
        {
            var obj = new JsonObject();
            foreach (Property property in properties)
            {
                if (property is NoneProperty || skip?.Invoke(property) == true)
                {
                    continue;
                }
                AddProperty(obj, property, pcc, tlk);
            }
            return obj;
        }

        public static void AddProperty(JsonObject obj, Property property, IMEPackage pcc, Func<int, string> tlk)
        {
            string name = property.Name.Instanced;
            if (property.StaticArrayIndex > 0)
            {
                name = $"{property.Name.Name}[{property.StaticArrayIndex}]";
            }
            JsonNode value = ToNode(property, pcc, tlk);
            if (value is null)
            {
                return;
            }
            obj[name] = value;
            if (property is StringRefProperty stringRef && stringRef.Value > 0 && tlk is not null)
            {
                string text = tlk(stringRef.Value);
                if (!string.IsNullOrEmpty(text))
                {
                    obj[name + "Text"] = text;
                }
            }
        }

        public static JsonNode ToNode(Property property, IMEPackage pcc, Func<int, string> tlk)
        {
            switch (property)
            {
                case StructProperty structProperty:
                    return StructToNode(structProperty, pcc, tlk);
                case ArrayPropertyBase array:
                {
                    var jsonArray = new JsonArray();
                    foreach (Property element in array.Properties)
                    {
                        JsonNode node = ToNode(element, pcc, tlk);
                        jsonArray.Add(node);
                    }
                    return jsonArray;
                }
                case ObjectProperty objectProperty:
                    return objectProperty.Value == 0 ? null : JsonValue.Create(objectProperty.ResolveToEntry(pcc)?.InstancedFullPath ?? $"#{objectProperty.Value}");
                case NameProperty nameProperty:
                    return nameProperty.Value.Name is null || nameProperty.Value.Name.Equals("None", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : JsonValue.Create(nameProperty.Value.Instanced);
                case EnumProperty enumProperty:
                    return JsonValue.Create(enumProperty.Value.Instanced);
                case FloatProperty floatProperty:
                    return JsonValue.Create(Round(floatProperty.Value, 4));
                case IntProperty intProperty:
                    return JsonValue.Create(intProperty.Value);
                case BoolProperty boolProperty:
                    return JsonValue.Create(boolProperty.Value);
                case ByteProperty byteProperty:
                    return JsonValue.Create((int)byteProperty.Value);
                case StrProperty strProperty:
                    return string.IsNullOrEmpty(strProperty.Value) ? null : JsonValue.Create(strProperty.Value);
                case StringRefProperty stringRefProperty:
                    return JsonValue.Create(stringRefProperty.Value);
                default:
                    return null;
            }
        }

        private static JsonNode StructToNode(StructProperty structProperty, IMEPackage pcc, Func<int, string> tlk)
        {
            PropertyCollection properties = structProperty.Properties;
            switch (structProperty.StructType)
            {
                case "Vector":
                    return new JsonArray(
                        Round(GetFloat(properties, "X"), 2),
                        Round(GetFloat(properties, "Y"), 2),
                        Round(GetFloat(properties, "Z"), 2));
                case "Rotator":
                    return new JsonObject
                    {
                        ["pitch"] = Round(UnrealToDegrees(GetInt(properties, "Pitch")), 2),
                        ["yaw"] = Round(UnrealToDegrees(GetInt(properties, "Yaw")), 2),
                        ["roll"] = Round(UnrealToDegrees(GetInt(properties, "Roll")), 2)
                    };
                case "Color":
                    return JsonValue.Create(
                        $"#{GetByte(properties, "R"):X2}{GetByte(properties, "G"):X2}{GetByte(properties, "B"):X2}{GetByte(properties, "A"):X2}");
                case "LinearColor":
                    return new JsonArray(
                        Round(GetFloat(properties, "R")), Round(GetFloat(properties, "G")),
                        Round(GetFloat(properties, "B")), Round(GetFloat(properties, "A")));
                default:
                    return ToObject(properties, pcc, tlk);
            }
        }

        public static float UnrealToDegrees(int unrealRotation) => unrealRotation * 360f / 65536f;

        private static float GetFloat(PropertyCollection properties, string name) =>
            properties.GetProp<FloatProperty>(name)?.Value ?? 0f;

        private static int GetInt(PropertyCollection properties, string name) =>
            properties.GetProp<IntProperty>(name)?.Value ?? 0;

        private static int GetByte(PropertyCollection properties, string name) =>
            properties.GetProp<ByteProperty>(name)?.Value ?? 0;

        public static bool HasAny(PropertyCollection properties, params string[] names) =>
            names.Any(name => properties.Any(property => property.Name.Name == name));
    }
}
