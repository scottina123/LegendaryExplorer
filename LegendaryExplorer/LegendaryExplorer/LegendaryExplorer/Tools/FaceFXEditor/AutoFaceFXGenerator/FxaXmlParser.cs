using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// Parses FaceFX files from UDK FaceFX Studio
    /// Supports: FXA (binary and XML), FXT (text phoneme data)
    /// </summary>
    public static class FxaXmlParser
    {
        // FaceFX binary file magic bytes
        private static readonly byte[] FXA_MAGIC = { 0x46, 0x41, 0x43, 0x45 }; // "FACE"
        
        /// <summary>
        /// Parse an FXA file (binary or XML format from FaceFX Studio)
        /// </summary>
        public static FxaAnimationData ParseFxaFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("FXA file not found", filePath);

            // First, check if it's a binary file by reading the first few bytes
            byte[] header = new byte[4];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                fs.Read(header, 0, 4);
            }

            // Check for binary FXA format
            if (IsBinaryFxa(header))
            {
                return ParseBinaryFxa(filePath);
            }

            // Otherwise try text-based parsing
            var content = File.ReadAllText(filePath);
            
            // Check if it's XML
            if (content.TrimStart().StartsWith("<") || content.TrimStart().StartsWith("<?xml"))
            {
                return ParseFxaXml(content);
            }
            
            // Try to detect if it's a text-based format (like exported animation data)
            if (content.Contains("anim") || content.Contains("curve") || content.Contains("key") ||
                content.Contains("m_") || content.Contains("phoneme"))
            {
                return ParseFxaText(content);
            }
            
            throw new InvalidDataException(
                "Unable to parse the FXA file format.\n" +
                "Supported formats: Binary FXA, XML export, text-based exports.");
        }

        /// <summary>
        /// Check if the file header indicates a binary FXA file
        /// </summary>
        private static bool IsBinaryFxa(byte[] header)
        {
            // Check for "FACE" magic or other common FaceFX binary markers
            if (header.Length >= 4)
            {
                // "FACE" header
                if (header[0] == 0x46 && header[1] == 0x41 && header[2] == 0x43 && header[3] == 0x45)
                    return true;
                
                // Check for other binary indicators (non-printable characters in what should be text)
                int nonPrintable = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (header[i] < 32 && header[i] != 9 && header[i] != 10 && header[i] != 13)
                        nonPrintable++;
                }
                if (nonPrintable > 1)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Parse a binary FXA file from FaceFX Studio
        /// </summary>
        private static FxaAnimationData ParseBinaryFxa(string filePath)
        {
            var result = new FxaAnimationData();
            
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            try
            {
                // Read header
                byte[] magic = reader.ReadBytes(4);
                
                // Try to identify the format version
                // FaceFX binary format varies by version, but generally has:
                // - Magic header
                // - Version info
                // - String table
                // - Bone data
                // - Animation data

                // Read version (usually 4 bytes after magic)
                int version = 0;
                if (fs.Length > 8)
                {
                    version = reader.ReadInt32();
                }

                // Skip to find animation data
                // This is a simplified parser - full FXA binary parsing would require
                // reverse engineering the complete format
                
                // Try to find string markers that indicate animation names
                fs.Position = 0;
                byte[] fileData = reader.ReadBytes((int)fs.Length);
                
                // Search for animation name patterns (m_*) in the binary
                var animationNames = FindAnimationNamesInBinary(fileData);
                
                // Search for float data that could be animation curves
                var curveData = FindCurveDataInBinary(fileData, animationNames);

                foreach (var kvp in curveData)
                {
                    if (kvp.Value.Count > 0)
                    {
                        result.Animations[kvp.Key] = new FxaAnimation
                        {
                            Name = kvp.Key,
                        };
                        result.Animations[kvp.Key].Keys.AddRange(kvp.Value);
                    }
                }

                if (result.Animations.Count == 0)
                {
                    throw new InvalidDataException(
                        "Could not extract animation data from binary FXA file.\n" +
                        "The binary format may be unsupported. Try exporting as XML from FaceFX Studio.");
                }
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("Unexpected end of file while parsing binary FXA.");
            }

            return result;
        }

        /// <summary>
        /// Search for animation names (m_*) in binary data
        /// </summary>
        private static List<string> FindAnimationNamesInBinary(byte[] data)
        {
            var names = new List<string>();
            var knownNames = new[] 
            { 
                "m_Jaw+", "m_Jaw-", "m_Open", "m_M", "m_EE", "m_N", "m_G", 
                "m_OW", "m_OH", "m_Flap", "m_FV", "m_TH", "m_L", "m_ZZ", "m_EH"
            };

            string dataStr = Encoding.ASCII.GetString(data);
            
            foreach (var name in knownNames)
            {
                if (dataStr.Contains(name))
                {
                    names.Add(name);
                }
            }

            // Also search for generic m_ patterns
            var regex = new Regex(@"m_[A-Za-z0-9+\-]+");
            var matches = regex.Matches(dataStr);
            foreach (Match match in matches)
            {
                if (!names.Contains(match.Value))
                {
                    names.Add(match.Value);
                }
            }

            return names;
        }

        /// <summary>
        /// Attempt to find curve data associated with animation names in binary
        /// </summary>
        private static Dictionary<string, List<FxaKey>> FindCurveDataInBinary(byte[] data, List<string> animNames)
        {
            var result = new Dictionary<string, List<FxaKey>>();
            
            foreach (var name in animNames)
            {
                result[name] = new List<FxaKey>();
            }

            // Search for float arrays that could be curve data
            // Floats in binary are typically 4 bytes each
            // We look for sequences that could be time/value pairs
            
            string dataStr = Encoding.ASCII.GetString(data);
            
            foreach (var animName in animNames)
            {
                int nameIndex = dataStr.IndexOf(animName);
                if (nameIndex < 0) continue;

                // Look for float data after the name (within next ~1KB)
                int searchStart = nameIndex + animName.Length;
                int searchEnd = Math.Min(searchStart + 1024, data.Length - 8);

                var floats = new List<float>();
                for (int i = searchStart; i < searchEnd - 4; i += 4)
                {
                    try
                    {
                        float f = BitConverter.ToSingle(data, i);
                        // Check if it's a reasonable float value (not NaN, not huge)
                        if (!float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) < 1000)
                        {
                            floats.Add(f);
                        }
                    }
                    catch { }
                }

                // Try to interpret floats as time/value pairs
                if (floats.Count >= 4)
                {
                    for (int i = 0; i < floats.Count - 1; i += 2)
                    {
                        float time = floats[i];
                        float value = floats[i + 1];
                        
                        // Sanity check: time should be positive and value should be in reasonable range
                        if (time >= 0 && time < 100 && value >= -10 && value <= 10)
                        {
                            result[animName].Add(new FxaKey
                            {
                                Time = time,
                                Value = value,
                                InTangent = 0f,
                                OutTangent = 0f
                            });
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Parse an FXT file (FaceFX text phoneme timing data)
        /// </summary>
        public static FxaAnimationData ParseFxtFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("FXT file not found", filePath);

            var content = File.ReadAllText(filePath);
            return ParseFxtContent(content);
        }

        /// <summary>
        /// Parse FXT content (phoneme timing data)
        /// FXT format typically contains lines like:
        /// phoneme startTime endTime
        /// or word-based timing
        /// </summary>
        public static FxaAnimationData ParseFxtContent(string content)
        {
            var result = new FxaAnimationData();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Collect all phoneme events with timing
            var phonemeEvents = new List<(string phoneme, float startTime, float endTime)>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                    continue;

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                
                // Format: phoneme startTime endTime
                // or: startTime endTime phoneme
                if (parts.Length >= 3)
                {
                    string phoneme;
                    float startTime, endTime;

                    // Try both formats
                    if (TryParseFloat(parts[0], out startTime) && TryParseFloat(parts[1], out endTime))
                    {
                        // Format: startTime endTime phoneme
                        phoneme = parts[2];
                    }
                    else if (TryParseFloat(parts[1], out startTime) && TryParseFloat(parts[2], out endTime))
                    {
                        // Format: phoneme startTime endTime
                        phoneme = parts[0];
                    }
                    else
                    {
                        continue;
                    }

                    phonemeEvents.Add((phoneme.ToUpper(), startTime, endTime));
                }
            }

            // Convert phoneme events to animation curves using the phoneme mapping
            if (phonemeEvents.Count > 0)
            {
                result.PhonemeEvents = phonemeEvents;
                ConvertPhonemesToAnimations(phonemeEvents, result);
            }

            return result;
        }

        /// <summary>
        /// Convert phoneme timing events into animation curves
        /// </summary>
        private static void ConvertPhonemesToAnimations(
            List<(string phoneme, float startTime, float endTime)> phonemeEvents,
            FxaAnimationData result)
        {
            // Create animation curves for each viseme
            var visemeCurves = new Dictionary<string, List<FxaKey>>();

            foreach (var (phoneme, startTime, endTime) in phonemeEvents)
            {
                // Look up the phoneme mapping
                if (PhonemeToVisemeMap.PhonemeMap.TryGetValue(phoneme, out var mappings))
                {
                    float centerTime = (startTime + endTime) / 2f;
                    float duration = endTime - startTime;

                    foreach (var mapping in mappings)
                    {
                        if (!visemeCurves.ContainsKey(mapping.VisemeName))
                        {
                            visemeCurves[mapping.VisemeName] = new List<FxaKey>();
                        }

                        // Add attack key (ramp up)
                        visemeCurves[mapping.VisemeName].Add(new FxaKey
                        {
                            Time = startTime,
                            Value = 0f,
                            InTangent = 0f,
                            OutTangent = 0f
                        });

                        // Add peak key
                        visemeCurves[mapping.VisemeName].Add(new FxaKey
                        {
                            Time = centerTime,
                            Value = mapping.Weight,
                            InTangent = 0f,
                            OutTangent = 0f
                        });

                        // Add release key (ramp down)
                        visemeCurves[mapping.VisemeName].Add(new FxaKey
                        {
                            Time = endTime,
                            Value = 0f,
                            InTangent = 0f,
                            OutTangent = 0f
                        });
                    }
                }
            }

            // Convert to FxaAnimation objects
            foreach (var kvp in visemeCurves)
            {
                var anim = new FxaAnimation { Name = kvp.Key };
                
                // Sort keys by time and merge nearby ones
                var sortedKeys = kvp.Value.OrderBy(k => k.Time).ToList();
                var mergedKeys = MergeNearbyKeys(sortedKeys, 0.02f);
                
                anim.Keys.AddRange(mergedKeys);
                result.Animations[kvp.Key] = anim;
            }
        }

        /// <summary>
        /// Merge keys that are too close together
        /// </summary>
        private static List<FxaKey> MergeNearbyKeys(List<FxaKey> keys, float threshold)
        {
            if (keys.Count < 2)
                return keys;

            var result = new List<FxaKey> { keys[0] };

            for (int i = 1; i < keys.Count; i++)
            {
                var last = result[^1];
                var current = keys[i];

                if (current.Time - last.Time < threshold)
                {
                    // Merge by taking the higher value
                    if (current.Value > last.Value)
                    {
                        result[^1] = current;
                    }
                }
                else
                {
                    result.Add(current);
                }
            }

            return result;
        }

        /// <summary>
        /// Parse FXA XML content
        /// </summary>
        public static FxaAnimationData ParseFxaXml(string xmlContent)
        {
            var result = new FxaAnimationData();

            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;

                if (root == null)
                    throw new InvalidDataException("Invalid FXA XML: no root element");

                // Find animation elements - try multiple possible structures
                ParseAnimationsFromXml(root, result);

                // Parse the phoneme mapping if present
                var mapping = root.Descendants("mapping").FirstOrDefault();
                if (mapping != null)
                {
                    ParsePhonemeMapping(mapping, result);
                }

                // Parse face graph for bone information
                var faceGraph = root.Descendants("face_graph").FirstOrDefault();
                if (faceGraph != null)
                {
                    ParseFaceGraph(faceGraph, result);
                }
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidDataException($"Invalid XML format: {ex.Message}", ex);
            }

            return result;
        }

        /// <summary>
        /// Try to parse text-based FXA format (non-XML)
        /// </summary>
        private static FxaAnimationData ParseFxaText(string content)
        {
            var result = new FxaAnimationData();
            
            // Try to find animation data patterns in text format
            // This handles various text export formats from FaceFX
            
            // Pattern: "anim animName" followed by curve data
            var animPattern = new Regex(@"anim\s+[""']?(\w+)[""']?\s*\{([^}]+)\}", RegexOptions.IgnoreCase);
            var matches = animPattern.Matches(content);

            foreach (Match match in matches)
            {
                var animName = match.Groups[1].Value;
                var animData = match.Groups[2].Value;
                
                var anim = new FxaAnimation { Name = animName };
                ParseTextCurveData(animData, anim);
                
                if (anim.Keys.Count > 0)
                {
                    result.Animations[animName] = anim;
                }
            }

            // If no structured format found, try line-by-line parsing
            if (result.Animations.Count == 0)
            {
                ParseUnstructuredText(content, result);
            }

            return result;
        }

        private static void ParseTextCurveData(string data, FxaAnimation anim)
        {
            // Look for key data: time value [tangentIn tangentOut]
            var keyPattern = new Regex(@"([\d.-]+)\s+([\d.-]+)(?:\s+([\d.-]+)\s+([\d.-]+))?");
            var matches = keyPattern.Matches(data);

            foreach (Match match in matches)
            {
                if (TryParseFloat(match.Groups[1].Value, out float time) &&
                    TryParseFloat(match.Groups[2].Value, out float value))
                {
                    float inTangent = 0f, outTangent = 0f;
                    if (match.Groups[3].Success && match.Groups[4].Success)
                    {
                        TryParseFloat(match.Groups[3].Value, out inTangent);
                        TryParseFloat(match.Groups[4].Value, out outTangent);
                    }

                    anim.Keys.Add(new FxaKey
                    {
                        Time = time,
                        Value = value,
                        InTangent = inTangent,
                        OutTangent = outTangent
                    });
                }
            }
        }

        private static void ParseUnstructuredText(string content, FxaAnimationData result)
        {
            // Try to identify animation names and their data from unstructured text
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            string currentAnimName = null;
            FxaAnimation currentAnim = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                // Check if this line starts a new animation
                if (trimmed.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
                {
                    // Could be animation name
                    var parts = trimmed.Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        currentAnimName = parts[0];
                        currentAnim = new FxaAnimation { Name = currentAnimName };
                        result.Animations[currentAnimName] = currentAnim;
                    }
                }
                else if (currentAnim != null)
                {
                    // Try to parse as key data
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && 
                        TryParseFloat(parts[0], out float time) &&
                        TryParseFloat(parts[1], out float value))
                    {
                        currentAnim.Keys.Add(new FxaKey
                        {
                            Time = time,
                            Value = value,
                            InTangent = 0f,
                            OutTangent = 0f
                        });
                    }
                }
            }
        }

        private static void ParseAnimationsFromXml(XElement root, FxaAnimationData result)
        {
            // Try various XML structures that FaceFX might export

            // Structure 1: anim_group > anim
            var animGroups = root.Descendants("anim_group");
            foreach (var group in animGroups)
            {
                foreach (var anim in group.Elements("anim"))
                {
                    ParseAnimation(anim, result);
                }
            }

            // Structure 2: Direct anim elements
            var animations = root.Descendants("anim").ToList();
            foreach (var anim in animations)
            {
                ParseAnimation(anim, result);
            }

            // Structure 3: animation elements
            var animationElements = root.Descendants("animation").ToList();
            foreach (var anim in animationElements)
            {
                ParseAnimationElement(anim, result);
            }

            // Structure 4: curve elements
            var curves = root.Descendants("curve").ToList();
            foreach (var curve in curves)
            {
                ParseCurveElement(curve, result);
            }
        }

        private static void ParseAnimation(XElement anim, FxaAnimationData result)
        {
            var name = anim.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
                return;

            // Skip if already parsed
            if (result.Animations.ContainsKey(name))
                return;

            var animData = new FxaAnimation { Name = name };

            // Try various key formats
            ParseKeysFromElement(anim, animData);

            if (animData.Keys.Count > 0)
            {
                result.Animations[name] = animData;
            }
        }

        private static void ParseAnimationElement(XElement anim, FxaAnimationData result)
        {
            var name = anim.Attribute("name")?.Value ?? anim.Element("name")?.Value;
            if (string.IsNullOrEmpty(name))
                return;

            if (result.Animations.ContainsKey(name))
                return;

            var animData = new FxaAnimation { Name = name };
            ParseKeysFromElement(anim, animData);

            if (animData.Keys.Count > 0)
            {
                result.Animations[name] = animData;
            }
        }

        private static void ParseCurveElement(XElement curve, FxaAnimationData result)
        {
            var name = curve.Attribute("name")?.Value ?? curve.Attribute("target")?.Value;
            if (string.IsNullOrEmpty(name))
                return;

            if (result.Animations.ContainsKey(name))
                return;

            var animData = new FxaAnimation { Name = name };
            ParseKeysFromElement(curve, animData);

            if (animData.Keys.Count > 0)
            {
                result.Animations[name] = animData;
            }
        }

        private static void ParseKeysFromElement(XElement element, FxaAnimation animData)
        {
            // Try curve_keys element
            var curveKeys = element.Element("curve_keys");
            if (curveKeys != null)
            {
                ParseCurveKeysText(curveKeys.Value, animData);
            }

            // Try keys element
            var keys = element.Element("keys");
            if (keys != null)
            {
                ParseCurveKeysText(keys.Value, animData);
            }

            // Try keyframes element
            var keyframes = element.Element("keyframes");
            if (keyframes != null)
            {
                ParseCurveKeysText(keyframes.Value, animData);
            }

            // Try individual key elements
            var keyElements = element.Elements("key").ToList();
            foreach (var key in keyElements)
            {
                var timeAttr = key.Attribute("time")?.Value ?? key.Attribute("t")?.Value;
                var valueAttr = key.Attribute("value")?.Value ?? key.Attribute("v")?.Value;

                if (TryParseFloat(timeAttr, out float time) && TryParseFloat(valueAttr, out float value))
                {
                    TryParseFloat(key.Attribute("in")?.Value, out float inTan);
                    TryParseFloat(key.Attribute("out")?.Value, out float outTan);

                    animData.Keys.Add(new FxaKey
                    {
                        Time = time,
                        Value = value,
                        InTangent = inTan,
                        OutTangent = outTan
                    });
                }
            }

            // Try data attribute or element content
            var dataAttr = element.Attribute("data")?.Value;
            if (!string.IsNullOrEmpty(dataAttr))
            {
                ParseCurveKeysText(dataAttr, animData);
            }

            // Try element text content directly
            if (animData.Keys.Count == 0 && !element.HasElements)
            {
                ParseCurveKeysText(element.Value, animData);
            }
        }

        private static void ParseCurveKeysText(string keysText, FxaAnimation animData)
        {
            if (string.IsNullOrWhiteSpace(keysText))
                return;

            var values = keysText.Split(new[] { ' ', '\t', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries);

            if (values.Length < 2)
                return;

            // Determine format: 2 values (time, value) or 4 values (time, value, inTan, outTan)
            if (values.Length >= 4 && values.Length % 4 == 0)
            {
                for (int i = 0; i < values.Length; i += 4)
                {
                    if (TryParseFloat(values[i], out float time) &&
                        TryParseFloat(values[i + 1], out float value))
                    {
                        TryParseFloat(values[i + 2], out float inTangent);
                        TryParseFloat(values[i + 3], out float outTangent);

                        animData.Keys.Add(new FxaKey
                        {
                            Time = time,
                            Value = value,
                            InTangent = inTangent,
                            OutTangent = outTangent
                        });
                    }
                }
            }
            else if (values.Length >= 2 && values.Length % 2 == 0)
            {
                for (int i = 0; i < values.Length; i += 2)
                {
                    if (TryParseFloat(values[i], out float time) &&
                        TryParseFloat(values[i + 1], out float value))
                    {
                        animData.Keys.Add(new FxaKey
                        {
                            Time = time,
                            Value = value,
                            InTangent = 0f,
                            OutTangent = 0f
                        });
                    }
                }
            }
        }

        private static void ParsePhonemeMapping(XElement mapping, FxaAnimationData result)
        {
            var entries = mapping.Elements("entry");
            foreach (var entry in entries)
            {
                var phoneme = entry.Attribute("phoneme")?.Value;
                var target = entry.Attribute("target")?.Value;
                var amountStr = entry.Attribute("amount")?.Value;

                if (!string.IsNullOrEmpty(phoneme) && !string.IsNullOrEmpty(target) &&
                    TryParseFloat(amountStr, out float amount))
                {
                    if (!result.PhonemeMapping.ContainsKey(phoneme))
                    {
                        result.PhonemeMapping[phoneme] = new List<FxaPhonemeTarget>();
                    }
                    result.PhonemeMapping[phoneme].Add(new FxaPhonemeTarget
                    {
                        Target = target,
                        Amount = amount
                    });
                }
            }
        }

        private static void ParseFaceGraph(XElement faceGraph, FxaAnimationData result)
        {
            var bones = faceGraph.Descendants("bone");
            foreach (var bone in bones)
            {
                var name = bone.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    result.BoneNames.Add(name);
                }
            }
        }

        private static bool TryParseFloat(string value, out float result)
        {
            result = 0f;
            if (string.IsNullOrEmpty(value))
                return false;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }
    }

    /// <summary>
    /// Container for all animation data from FXA/FXT files
    /// </summary>
    public class FxaAnimationData
    {
        public Dictionary<string, FxaAnimation> Animations { get; } = new();
        public Dictionary<string, List<FxaPhonemeTarget>> PhonemeMapping { get; } = new();
        public List<string> BoneNames { get; } = new();
        public List<(string phoneme, float startTime, float endTime)> PhonemeEvents { get; set; } = new();
    }

    /// <summary>
    /// A single animation curve from FXA
    /// </summary>
    public class FxaAnimation
    {
        public string Name { get; set; }
        public List<FxaKey> Keys { get; } = new();
    }

    /// <summary>
    /// A keyframe in an animation curve
    /// </summary>
    public class FxaKey
    {
        public float Time { get; set; }
        public float Value { get; set; }
        public float InTangent { get; set; }
        public float OutTangent { get; set; }
    }

    /// <summary>
    /// A phoneme to viseme target mapping
    /// </summary>
    public class FxaPhonemeTarget
    {
        public string Target { get; set; }
        public float Amount { get; set; }
    }
}
