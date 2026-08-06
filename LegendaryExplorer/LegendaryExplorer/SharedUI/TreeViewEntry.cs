using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.ME1.Unreal.UnhoodBytecode;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.SharedUI
{
    [DebuggerDisplay("TreeViewEntry {" + nameof(DisplayName) + "}")]
    public sealed class TreeViewEntry : NotifyPropertyChangedBase, IDisposable
    {
        /// <summary>
        /// Dumps packages in the global cache for looking up defaults
        /// </summary>
        public static void ClearCache() => DefaultsLookupCache.ReleasePackages();

        // Consider a global tiered package cache 10/16/2024 Mgamerz
        private static readonly PackageCache DefaultsLookupCache = new() { CacheMaxSize = 3 }; // Don't let cache get big.

        public bool IsProgramaticallySelecting;

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set => SetProperty(ref isSelected, value);
        }

        private bool _isMultiSelected;
        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set => SetProperty(ref _isMultiSelected, value);
        }

        /// <summary>
        /// Returns the game that this entry node is tied to
        /// </summary>
        public MEGame Game => PackageRef?.Game ?? Entry.Game;

        /*   {
              /* if (!IsProgramaticallySelecting && isSelected != value)
               {
                   //user is selecting
                   isSelected = value;
                   OnPropertyChanged();
                   return;
               }
               // build a priority queue of dispatcher operations

               // All operations relating to tree item expansion are added with priority = DispatcherPriority.ContextIdle, so that they are
               // sorted before any operations relating to selection (which have priority = DispatcherPriority.ApplicationIdle).
               // This ensures that the visual container for all items are created before any selection operation is carried out.
               // First expand all ancestors of the selected item - those closest to the root first
               // Expanding a node will scroll as many of its children as possible into view - see perTreeViewItemHelper, but these scrolling
               // operations will be added to the queue after all of the parent expansions.
               if (value)
               {
                   var ancestorsToExpand = new Stack<TreeViewEntry>();

                   var parent = Parent;
                   while (parent != null)
                   {
                       if (!parent.IsExpanded)
                           ancestorsToExpand.Push(parent);

                       parent = parent.Parent;
                   }

                   while (ancestorsToExpand.Any())
                   {
                       var parentToExpand = ancestorsToExpand.Pop();
                       DispatcherHelper.AddToQueue(() => parentToExpand.IsExpanded = true, DispatcherPriority.ContextIdle);
                   }
               }

               //cancel if we're currently selected.
               if (isSelected == value)
                   return;

               // Set the item's selected state - use DispatcherPriority.ApplicationIdle so this operation is executed after all
               // expansion operations, no matter when they were added to the queue.
               // Selecting a node will also scroll it into view - see perTreeViewItemHelper
               DispatcherHelper.AddToQueue(() =>
               {
                   if (value != isSelected)
                   {
                       this.isSelected = value;
                       OnPropertyChanged(nameof(IsSelected));
                       IsProgramaticallySelecting = false;
                   }
               }, DispatcherPriority.ApplicationIdle);

               // note that by rule, a TreeView can only have one selected item, but this is handled automatically by 
               // the control - we aren't required to manually unselect the previously selected item.

               // execute all of the queued operations in descending DipatecherPriority order (expansion before selection)
               var unused = DispatcherHelper.ProcessQueueAsync();
           }
       }*/

        private bool isExpanded;
        public bool IsExpanded
        {
            get => this.isExpanded;
            set => SetProperty(ref isExpanded, value);
        }

        private bool _isVisibleInTree = true;
        public bool IsVisibleInTree
        {
            get => _isVisibleInTree;
            set => SetProperty(ref _isVisibleInTree, value);
        }

        public void ExpandParents()
        {
            if (Parent != null)
            {
                Parent.ExpandParents();
                Parent.IsExpanded = true;
            }
        }

        /// <summary>
        /// Flattens the tree into depth first order. Use this method for searching the list.
        /// </summary>
        /// <returns></returns>
        public List<TreeViewEntry> FlattenTree()
        {
            var nodes = new List<TreeViewEntry>();
            var nodesToVisit = new Stack<TreeViewEntry>();
            nodesToVisit.Push(this);

            while (nodesToVisit.Count > 0)
            {
                TreeViewEntry node = nodesToVisit.Pop();
                nodes.Add(node);

                // Push in reverse so the resulting depth-first order remains the
                // same as the order exposed by Sublinks.
                for (int i = node.Sublinks.Count - 1; i >= 0; i--)
                {
                    nodesToVisit.Push(node.Sublinks[i]);
                }
            }

            return nodes;
        }

        public TreeViewEntry Parent { get; set; }

        /// <summary>
        /// The entry object from the file that this node represents
        /// </summary>
        public IEntry Entry { get; set; }

        /// <summary>
        /// Only used on the root node - used to tell what package this entry represent the root for
        /// </summary>
        public IMEPackage PackageRef { get; set; }

        /// <summary>
        /// List of entries that link to this node
        /// </summary>
        public ObservableCollectionExtended<TreeViewEntry> Sublinks { get; set; }
        public TreeViewEntry(IEntry entry, string displayName = null)
        {
            Entry = entry;
            DisplayName = displayName;
            Sublinks = new ObservableCollectionExtended<TreeViewEntry>();

            // Events don't work in interface without method to raise changes
            // so we just attach to each
            if (Entry is ImportEntry imp)
            {
                imp.PropertyChanged += TVEntryPropertyChanged;
            }
            else if (Entry is ExportEntry exp)
            {
                exp.PropertyChanged += TVEntryPropertyChanged;
            }
        }

        private void TVEntryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (Settings.PackageEditor_ShowTreeEntrySubText)
            {
                RefreshSubText();
            }
        }

        public void RefreshDisplayName()
        {
            OnPropertyChanged(nameof(DisplayName));
        }

        public void RefreshSubText()
        {
            loadedSubtext = false;
            SubText = null;
        }

        private readonly string _displayName;
        public string DisplayName
        {
            get
            {
                try
                {
                    if (_displayName != null) return _displayName;
                    string returnvalue = $"{UIndex} {Entry.ObjectName.Instanced}";
                    if (Settings.PackageEditor_ShowImpExpPrefix)
                    {
                        string type = UIndex < 0 ? "(Imp) " : "(Exp) ";
                        returnvalue = type + returnvalue;
                    }

                    string className = Entry.ClassName;
                    if (Entry is ExportEntry exp && BinaryInterpreterWPF.IsNativePropertyType(Entry.ClassName))
                    {
                        // We can't do this in the subtext setter since:
                        // 1. Users might have that off
                        // 2. The display property is Init-Only
                        var bin = ObjectBinary.From<UBoolProperty>(exp);
                        if (bin.ArraySize > 1)
                            className += $"[{bin.ArraySize}]";
                    }
                    returnvalue += $"({className})";
                    return returnvalue;
                }
                catch (Exception)
                {
                    return "ERROR GETTING DISPLAY NAME!";
                }
            }
            init { _displayName = value; OnPropertyChanged(); }
        }

        private bool loadedSubtext = false;
        private string _subtext;
        public string SubText
        {
            get
            {
                if (!Settings.PackageEditor_ShowTreeEntrySubText) return null;
                try
                {
                    if (loadedSubtext) return _subtext;
                    if (Entry == null) return null;
                    if (Entry is ExportEntry ee)
                    {
                        if (ee.IsDefaultObject) return null; // We don't have subtext on defaults.
                        //Parse as export
                        switch (ee.ClassName)
                        {
                            case "Function":
                                {
                                    //check if exec
                                    var data = ee.DataReadOnly;
                                    if (Entry.FileRef.Game == MEGame.ME3 || Entry.FileRef.Platform == MEPackage.GamePlatform.PS3 || Entry.Game.IsLEGame())
                                    {
                                        var flagOffset = Entry.Game.IsGame3() || Entry.FileRef.Platform == MEPackage.GamePlatform.PS3 ? 4 : 12;
                                        var flags = EndianReader.ToInt32(data, data.Length - flagOffset, ee.FileRef.Endian);
                                        var fs = new FlagValues(flags, UE3FunctionReader._flagSet);
                                        _subtext = "";
                                        if (fs.HasFlag("Static"))
                                        {
                                            if (_subtext != "") _subtext += " ";
                                            _subtext = "Static";
                                        }
                                        if (fs.HasFlag("Native"))
                                        {
                                            if (_subtext != "") _subtext += " ";
                                            _subtext += "Native";
                                            var nativeBackOffset = !Entry.FileRef.Game.IsGame3() ? 3 : 2; // can be ps3 me1/me2
                                            var nativeIndex = EndianReader.ToInt16(data, data.Length - nativeBackOffset - flagOffset, ee.FileRef.Endian);
                                            if (nativeIndex > 0)
                                            {
                                                _subtext += ", index " + nativeIndex;
                                            }
                                        }

                                        if (fs.HasFlag("Exec"))
                                        {
                                            if (_subtext != "") _subtext += " ";
                                            _subtext += "Exec - console command";
                                        }

                                        if (_subtext == "") _subtext = null;
                                    }
                                    else if (Entry.Game.IsOTGame() || Entry.Game is MEGame.UDK) // ME1 / ME2
                                    {
                                        //This could be -14 if it's defined as Net... we would have to decompile the whole function to know though...
                                        var flags = EndianReader.ToInt32(data, data.Length - 12, ee.FileRef.Endian);
                                        var fs = new FlagValues(flags, UE3FunctionReader._flagSet);
                                        if (fs.HasFlag("Exec"))
                                        {
                                            _subtext = "Exec - console command";
                                        }
                                        else if (fs.HasFlag("Native"))
                                        {
                                            var nativeBackOffset = ee.FileRef.Game == MEGame.ME3 ? 6 : 7;
                                            if (ee.Game is MEGame.UDK || ee.Game < MEGame.ME3 && ee.FileRef.Platform != MEPackage.GamePlatform.PS3)
                                            {
                                                nativeBackOffset = 0xF;
                                            }
                                            var nativeIndex = EndianReader.ToInt16(data, data.Length - nativeBackOffset,
                                                ee.FileRef.Endian);
                                            if (nativeIndex > 0)
                                            {
                                                _subtext = "Native, index " + nativeIndex;
                                            }
                                            else
                                            {
                                                _subtext = "Native";
                                            }
                                        }
                                    }

                                    if (Entry.ObjectName.Name.StartsWith("F") &&
                                        Entry.ParentName.Equals("BioAutoConditionals", StringComparison.OrdinalIgnoreCase) &&
                                        int.TryParse(Entry.ObjectName.Name.Substring(1), out int id))
                                    {
                                        _subtext = PlotDatabases.FindPlotConditionalByID(id, Entry.Game)?.Path;
                                    }

                                    break;
                                }
                            case "Const":
                                {
                                    var data = ee.DataReadOnly;
                                    //This is kind of a hack. 
                                    var value = EndianReader.ReadUnrealString(data, Entry.Game is MEGame.UDK ? 0x10 : 0x14, ee.FileRef.Endian);
                                    _subtext = "Value: " + value;
                                    break;
                                }
                            case "ByteProperty":
                            case "StructProperty":
                            case "ObjectProperty":
                            case "ComponentProperty":
                                {
                                    // Objects of this type
                                    var typeRef = EndianReader.ToInt32(ee.DataReadOnly, Entry.FileRef.Platform == MEPackage.GamePlatform.PC ? Entry.Game is MEGame.UDK ? 0x28 : 0x2C : 0x20, ee.FileRef.Endian);
                                    if (ee.FileRef.TryGetEntry(typeRef, out var type))
                                    {
                                        _subtext = type.ObjectName;
                                    }
                                    break;
                                }
                            case "ClassProperty":
                                {
                                    var data = ee.DataReadOnly;
                                    var typeRef = EndianReader.ToInt32(data, data.Length - 4, ee.FileRef.Endian);
                                    if (ee.FileRef.TryGetEntry(typeRef, out var type))
                                    {
                                        _subtext = $"Class: {type.ObjectName}";
                                    }
                                    break;
                                }
                            case "Texture2D":
                                {
                                    var properties = ee.GetProperties();
                                    var sizeX = properties.GetProp<IntProperty>("SizeX");
                                    var sizeY = properties.GetProp<IntProperty>("SizeY");
                                    _subtext = $"{sizeX?.Value}x{sizeY?.Value}";
                                    break;
                                }
                            case "ParticleSpriteEmitter":
                                {
                                    var emitterName = ee.GetProperty<NameProperty>("EmitterName");
                                    if (emitterName != null)
                                    {
                                        _subtext = emitterName.Value.Instanced;
                                    }
                                }
                                break;
                            case "SFXGalaxy":
                            case "SFXSystem":
                            case "SFXCluster":
                                {
                                    var objName = ee.GetProperty<StringRefProperty>("DisplayName");
                                    if (objName != null)
                                    {
                                        var dispName = TLKManagerWPF.GlobalFindStrRefbyID(objName.Value, ee.Game);
                                        if (dispName != @"No Data")
                                        {
                                            _subtext = dispName;
                                        }
                                    }
                                }
                                break;
                            case "InterpData":
                                {
                                    _subtext = ResolveInterpDataSubtitle(ee);
                                }
                                break;
                            case "BioEvtSysTrackSubtitles":
                                {
                                    var subtitleData = ee.GetProperty<ArrayProperty<StructProperty>>("m_aSubtitleData");
                                    if (subtitleData != null)
                                    {
                                        var lines = new List<string>();
                                        foreach (var subtitle in subtitleData)
                                        {
                                            int strRef = subtitle.GetProp<IntProperty>("nStrRefID");
                                            if (strRef != 0)
                                            {
                                                var tlkStr = TLKManagerWPF.GlobalFindStrRefbyID(strRef, ee.FileRef);
                                                if (tlkStr != "No Data")
                                                {
                                                    lines.Add(tlkStr);
                                                }
                                            }
                                        }
                                        if (lines.Count > 0)
                                        {
                                            _subtext = string.Join("\n", lines);
                                        }
                                    }
                                }
                                break;
                            case "InterpTrackWwiseEvent":
                                {
                                    var wwiseEvents = ee.GetProperty<ArrayProperty<StructProperty>>("WwiseEvents");
                                    if (wwiseEvents != null)
                                    {
                                        var lines = new List<string>();
                                        foreach (var wwiseEvent in wwiseEvents)
                                        {
                                            var eventRef = wwiseEvent.GetProp<ObjectProperty>("Event");
                                            if (eventRef != null && ee.FileRef.TryGetEntry(eventRef.Value, out var eventEntry))
                                            {
                                                string name = eventEntry.ObjectName.Name;
                                                if (name.StartsWith("VO_"))
                                                {
                                                    var parsing = name.Substring(3);
                                                    var nextUnderScore = parsing.IndexOf("_");
                                                    if (nextUnderScore > 0)
                                                    {
                                                        parsing = parsing.Substring(0, nextUnderScore);
                                                    }
                                                    if (int.TryParse(parsing, out var parsedInt))
                                                    {
                                                        var tlkStr = TLKManagerWPF.GlobalFindStrRefbyID(parsedInt, ee.FileRef);
                                                        if (tlkStr != "No Data")
                                                        {
                                                            lines.Add(tlkStr);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        if (lines.Count > 0)
                                        {
                                            _subtext = string.Join("\n", lines.Distinct());
                                        }
                                    }
                                }
                                break;
                            case "SFXInterpTrackPlayFaceOnlyVO":
                                {
                                    var fovoKeys = ee.GetProperty<ArrayProperty<StructProperty>>("m_aFOVOKeys");
                                    if (fovoKeys != null)
                                    {
                                        var lines = new List<string>();
                                        foreach (var key in fovoKeys)
                                        {
                                            int strRef = key.GetProp<IntProperty>("nLineStrRef");
                                            if (strRef != 0)
                                            {
                                                var tlkStr = TLKManagerWPF.GlobalFindStrRefbyID(strRef, ee.FileRef);
                                                if (tlkStr != "No Data")
                                                {
                                                    lines.Add(tlkStr);
                                                }
                                            }
                                        }
                                        if (lines.Count > 0)
                                        {
                                            _subtext = string.Join("\n", lines);
                                        }
                                    }
                                }
                                break;
                            case "BioSeqAct_FaceOnlyVO":
                                {
                                    var strRefProp = ee.GetProperty<IntProperty>("m_nStrRefID");
                                    if (strRefProp != null && strRefProp.Value != 0)
                                    {
                                        var tlkStr = TLKManagerWPF.GlobalFindStrRefbyID(strRefProp.Value, ee.FileRef);
                                        if (tlkStr != "No Data")
                                        {
                                            _subtext = tlkStr;
                                        }
                                    }
                                }
                                break;
                            case "BioEvtSysTrackVOElements":
                                {
                                    var strRefProp = ee.GetProperty<IntProperty>("m_nStrRefID");
                                    if (strRefProp != null && strRefProp.Value != 0)
                                    {
                                        var tlkStr = TLKManagerWPF.GlobalFindStrRefbyID(strRefProp.Value, ee.FileRef);
                                        if (tlkStr != "No Data")
                                        {
                                            _subtext = tlkStr;
                                        }
                                    }
                                }
                                break;
                        }

                        {
                            var findActor = ee.GetProperty<NameProperty>("m_nmSFXFindActor")
                                         ?? ee.GetProperty<NameProperty>("m_nmFindActor");
                            if (findActor != null && findActor.Value.Name != "None")
                            {
                                string actorName = findActor.Value.Instanced;
                                if (actorName == "Owner")
                                {
                                    if (ConversationExtended.TryGetCachedOwnerTag(ee, out var cachedOwnerTag))
                                    {
                                        if (!string.IsNullOrEmpty(cachedOwnerTag))
                                        {
                                            actorName = $"Owner ({cachedOwnerTag})";
                                        }
                                    }
                                    else
                                    {
                                        // Resolve in background and surgically update the subtext
                                        // without re-computing the entire subtext (avoids layout thrashing)
                                        var capturedExport = ee;
                                        Task.Run(() =>
                                        {
                                            var resolved = ConversationExtended.ResolveOwnerTagFromExport(capturedExport);
                                            if (!string.IsNullOrEmpty(resolved))
                                            {
                                                var resolvedName = $"Owner ({resolved})";
                                                Application.Current?.Dispatcher?.BeginInvoke(() =>
                                                {
                                                    // Only update if subtext still starts with unresolved "Owner"
                                                    if (_subtext != null &&
                                                        _subtext.StartsWith("Owner", StringComparison.Ordinal) &&
                                                        !_subtext.StartsWith("Owner (", StringComparison.Ordinal))
                                                    {
                                                        _subtext = resolvedName + _subtext.Substring("Owner".Length);
                                                        OnPropertyChanged(nameof(SubText));
                                                    }
                                                }, DispatcherPriority.Background);
                                            }
                                        });
                                    }
                                }

                                _subtext = _subtext != null
                                    ? actorName + "\n" + _subtext
                                    : actorName;
                            }

                            var groupName = ee.GetProperty<NameProperty>("GroupName");
                            if (groupName != null && groupName.Value.Name != "None")
                            {
                                _subtext = _subtext != null
                                    ? _subtext + "\n" + groupName.Value.Instanced
                                    : groupName.Value.Instanced;
                            }
                        }

                        if (_subtext == null && ee.IsA("SequenceObject"))
                        {
                            _subtext = ee.GetProperty<StrProperty>("ObjName")?.Value;
                            if (_subtext == ee.ObjectName.Instanced)
                                _subtext = null; // Don't display it if it's the same.
                        }

                        if (_subtext == null && IsUnderSfxSystem(ee))
                        {
                            _subtext = ResolveGalaxyMapDisplayName(ee);
                        }

                        // Short circuit
                        

                        if (!AddPropertyFlags(ee))
                        {
                            var tag = ee.GetProperty<NameProperty>("Tag", DefaultsLookupCache); // Todo: Pass a package cache through here so hits to Engine.pcc aren't as costly. We will need a global shared package cache (maybe just for this treeview), but one that is not
                                                                                                // using the LEX cache as we don't want the package actually open.
                            if (tag != null && tag.Value.Name != Entry.ObjectName)
                            {
                                _subtext = tag.Value.Instanced;
                            }
                        }

                        if (ee.ClassName == "SFXPointOfInterest" && ResolvePointOfInterestGameName(ee) is { } gameName)
                        {
                            _subtext = _subtext != null
                                ? $"{_subtext}\n{gameName}"
                                : gameName;
                        }

                        if (ee.ParentName == "PersistentLevel"
                            && ee.ClassName is "SFXStuntActor" or "BioStage"
                            && ee.Archetype is ExportEntry archetype)
                        {
                            string archetypeSubtext = archetype.ObjectName.Instanced;
                            var inheritedTag = archetype.GetProperty<NameProperty>("Tag", DefaultsLookupCache);
                            if (inheritedTag != null && inheritedTag.Value.Name != "None")
                            {
                                archetypeSubtext += $"\nInherited: {inheritedTag.Value.Instanced}";
                            }

                            _subtext = _subtext != null
                                ? $"{_subtext}\n{archetypeSubtext}"
                                : archetypeSubtext;
                        }
                    }

                    if (_subtext == null)
                    {
                        // Parse if export or import
                        switch (Entry.ClassName)
                        {
                            case "WwiseEvent":
                                {
                                    //parse out tlk id?
                                    if (Entry.ObjectName.Name.StartsWith("VO_"))
                                    {
                                        var parsing = Entry.ObjectName.Name.Substring(3);
                                        var nextUnderScore = parsing.IndexOf("_");
                                        if (nextUnderScore > 0)
                                        {
                                            parsing = parsing.Substring(0, nextUnderScore);
                                            if (int.TryParse(parsing, out var parsedInt))
                                            {
                                                //Lookup TLK
                                                var data = TLKManagerWPF.GlobalFindStrRefbyID(parsedInt, Entry.FileRef);
                                                if (data != "No Data")
                                                {
                                                    _subtext = data;
                                                }
                                            }
                                        }
                                    }

                                    break;
                                }
                            case "WwiseStream":
                                {
                                    //parse out tlk id?
                                    var splits = Entry.ObjectName.Name.Split('_', ',');
                                    for (int i = splits.Length - 1; i > 0; i--)
                                    {
                                        //backwards is faster
                                        if (int.TryParse(splits[i], out var parsed))
                                        {
                                            //Lookup TLK
                                            var data = TLKManagerWPF.GlobalFindStrRefbyID(parsed, Entry.FileRef);
                                            if (data != "No Data")
                                            {
                                                _subtext = data;
                                            }
                                        }
                                    }

                                    break;
                                }
                            case "SoundNodeWave":
                            case "SoundCue":
                                {
                                    //parse out tlk id?
                                    // 11/02/2024 - Have to do instances as in Game1 only male strings (suffixed with _M) get treated as unique base names
                                    // So audio:VO_123456 and audio:VO_123456_M have different base names!
                                    var splits = Entry.ObjectName.Instanced.Split('_', ',');
                                    for (int i = splits.Length - 1; i > 0; i--)
                                    {
                                        //backwards is faster
                                        if (int.TryParse(splits[i], out var parsed))
                                        {
                                            //Lookup TLK
                                            var data = TLKManagerWPF.GlobalFindStrRefbyID(parsed, Entry.FileRef);
                                            if (data != "No Data")
                                            {
                                                _subtext = data;
                                            }
                                        }
                                    }

                                    break;
                                }
                        }
                    }

                    loadedSubtext = true;
                    return _subtext;
                }
                catch (Exception)
                {
                    loadedSubtext = true;
                    _subtext = "ERROR GETTING SUBTEXT!";
                    return "ERROR GETTING SUBTEXT!";
                }
            }
            set { _subtext = value; OnPropertyChanged(); }
        }

        private static string ResolvePointOfInterestGameName(ExportEntry pointOfInterest)
        {
            int strRef = pointOfInterest.GetProperty<StringRefProperty>("m_srGameName")?.Value
                         ?? pointOfInterest.GetProperty<IntProperty>("m_srGameName")?.Value
                         ?? 0;

            if (strRef == 0 && pointOfInterest.GetProperty<ArrayProperty<ObjectProperty>>("Modules") is { } modules)
            {
                foreach (var moduleRef in modules)
                {
                    if (pointOfInterest.FileRef.TryGetUExport(moduleRef.Value, out var module)
                        && module.ClassName == "SFXSimpleUseModule")
                    {
                        strRef = module.GetProperty<StringRefProperty>("m_srGameName")?.Value
                                 ?? module.GetProperty<IntProperty>("m_srGameName")?.Value
                                 ?? 0;
                        if (strRef != 0)
                        {
                            break;
                        }
                    }
                }
            }

            if (strRef == 0)
            {
                return null;
            }

            string gameName = TLKManagerWPF.GlobalFindStrRefbyID(strRef, pointOfInterest.FileRef);
            return gameName == "No Data" ? null : gameName;
        }

        private bool AddPropertyFlags(ExportEntry ee)
        {
            if (BinaryInterpreterWPF.IsNativePropertyType(Entry.ClassName))
            {
                var objectFlags = ee.GetPropertyFlags();
                if (objectFlags != null)
                {
                    if (objectFlags.Value.HasFlag(UnrealFlags.EPropertyFlags.Config))
                    {
                        if (_subtext != null)
                        {
                            _subtext = " Config, " + _subtext;
                        }
                        else
                        {
                            _subtext = "Config";
                        }
                    }
                }
                else
                {
                    // Bool is the most common subset so we parse the export as this to access the actual data.
                    // Lots of common properties won't have a stack
                    var bin = ObjectBinary.From<UBoolProperty>(ee);
                    if (bin.PropertyFlags.HasFlag(UnrealFlags.EPropertyFlags.Config))
                    {
                        if (_subtext != null)
                        {
                            _subtext = " Config, " + _subtext;
                        }
                        else
                        {
                            _subtext = "Config";
                        }
                    }
                }

                return true;
            }

            return false;
        }

        private bool IsUnderSfxSystem(IEntry entry)
        {
            for (IEntry current = entry; current != null; current = current.Parent)
            {
                if (current.ClassName.StartsWith("SFXSystem", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private string ResolveGalaxyMapDisplayName(ExportEntry ee)
        {
            PropertyCollection props = ee.GetProperties();
            string[] candidateNames = ["DisplayName", "SystemName", "ClusterName", "PlanetName", "RelayName", "Name", "NameStrRef"];

            string subtitle = ResolveDisplayNameFromProperties(ee, props, candidateNames);
            if (!string.IsNullOrWhiteSpace(subtitle))
                return subtitle;

            subtitle = ResolveDisplayNameFromPropertyTree(ee, props);
            if (!string.IsNullOrWhiteSpace(subtitle))
                return subtitle;

            if (props.GetProp<ObjectProperty>("Appearance")?.ResolveToExport(ee.FileRef, DefaultsLookupCache) is ExportEntry appearanceExport)
            {
                PropertyCollection appearanceProps = appearanceExport.GetProperties();

                subtitle = ResolveDisplayNameFromProperties(ee, appearanceProps, candidateNames);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    return subtitle;

                subtitle = ResolveDisplayNameFromPropertyTree(ee, appearanceProps);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    return subtitle;
            }

            return null;
        }

        private string ResolveDisplayNameFromProperties(ExportEntry ee, PropertyCollection props, IEnumerable<string> propertyNames)
        {
            if (props is null)
                return null;

            foreach (string propName in propertyNames)
            {
                string strValue = props.GetProp<StrProperty>(propName)?.Value;
                if (IsUsefulDisplayName(ee, strValue))
                    return strValue.Trim();

                string nameValue = props.GetProp<NameProperty>(propName)?.Value.Instanced;
                if (IsUsefulDisplayName(ee, nameValue))
                    return nameValue.Trim();

                int strRef = props.GetProp<StringRefProperty>(propName)?.Value
                             ?? props.GetProp<IntProperty>(propName)?.Value
                             ?? 0;
                if (strRef <= 0)
                    continue;

                string resolved = ResolveDisplayNameStringRef(ee, strRef);
                if (IsUsefulDisplayName(ee, resolved))
                    return resolved.Trim();
            }

            return null;
        }

        private string ResolveDisplayNameFromPropertyTree(ExportEntry ee, PropertyCollection props)
        {
            if (props is null)
                return null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in EnumerateDisplayNameCandidates(ee, props, 0, seen))
            {
                if (IsUsefulDisplayName(ee, candidate))
                    return candidate.Trim();
            }

            return null;
        }

        private IEnumerable<string> EnumerateDisplayNameCandidates(ExportEntry ee, PropertyCollection props, int depth, HashSet<string> seen)
        {
            if (props is null || depth > 4)
                yield break;

            foreach (Property prop in props)
            {
                string propName = prop.Name.Instanced;
                bool looksLikeDisplayName = propName.Contains("name", StringComparison.OrdinalIgnoreCase)
                                            || propName.Contains("display", StringComparison.OrdinalIgnoreCase)
                                            || propName.Contains("title", StringComparison.OrdinalIgnoreCase)
                                            || propName.Contains("label", StringComparison.OrdinalIgnoreCase);

                switch (prop)
                {
                    case StrProperty strProp when looksLikeDisplayName:
                        if (seen.Add(strProp.Value ?? string.Empty))
                            yield return strProp.Value;
                        break;

                    case NameProperty nameProp when looksLikeDisplayName:
                        if (seen.Add(nameProp.Value.Instanced ?? string.Empty))
                            yield return nameProp.Value.Instanced;
                        break;

                    case StringRefProperty stringRefProp when looksLikeDisplayName || propName.Contains("strref", StringComparison.OrdinalIgnoreCase):
                    {
                        string resolved = ResolveDisplayNameStringRef(ee, stringRefProp.Value);
                        if (seen.Add(resolved ?? string.Empty))
                            yield return resolved;
                        break;
                    }

                    case IntProperty intProp when propName.Contains("strref", StringComparison.OrdinalIgnoreCase):
                    {
                        string resolved = ResolveDisplayNameStringRef(ee, intProp.Value);
                        if (seen.Add(resolved ?? string.Empty))
                            yield return resolved;
                        break;
                    }

                    case StructProperty structProp:
                        foreach (string nested in EnumerateDisplayNameCandidates(ee, structProp.Properties, depth + 1, seen))
                            yield return nested;
                        break;

                    case ArrayPropertyBase arrayProp:
                        foreach (Property item in arrayProp.Properties)
                        {
                            if (item is StructProperty itemStruct)
                            {
                                foreach (string nested in EnumerateDisplayNameCandidates(ee, itemStruct.Properties, depth + 1, seen))
                                    yield return nested;
                            }
                            else if (item is StrProperty itemStr && looksLikeDisplayName)
                            {
                                if (seen.Add(itemStr.Value ?? string.Empty))
                                    yield return itemStr.Value;
                            }
                            else if (item is NameProperty itemName && looksLikeDisplayName)
                            {
                                if (seen.Add(itemName.Value.Instanced ?? string.Empty))
                                    yield return itemName.Value.Instanced;
                            }
                            else if (item is StringRefProperty itemStringRef)
                            {
                                string resolved = ResolveDisplayNameStringRef(ee, itemStringRef.Value);
                                if (seen.Add(resolved ?? string.Empty))
                                    yield return resolved;
                            }
                        }
                        break;
                }
            }
        }

        private string ResolveDisplayNameStringRef(ExportEntry ee, int strRef)
        {
            if (strRef <= 0)
                return null;

            string resolved = TLKManagerWPF.GlobalFindStrRefbyID(strRef, ee.FileRef);
            return resolved == "No Data" ? null : resolved;
        }

        private string ResolveInterpDataSubtitle(ExportEntry interpData)
        {
            var lines = new List<string>();
            var interpGroups = interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
            if (interpGroups == null)
            {
                return null;
            }

            foreach (var groupRef in interpGroups)
            {
                if (!interpData.FileRef.TryGetUExport(groupRef.Value, out ExportEntry group))
                {
                    continue;
                }

                var interpTracks = group.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks");
                if (interpTracks == null)
                {
                    continue;
                }

                foreach (var trackRef in interpTracks)
                {
                    if (!interpData.FileRef.TryGetUExport(trackRef.Value, out ExportEntry track))
                    {
                        continue;
                    }

                    AddInterpTrackSubtitleLines(track, lines);
                }
            }

            return lines.Count > 0 ? string.Join("\n", lines.Distinct()) : null;
        }

        private void AddInterpTrackSubtitleLines(ExportEntry track, List<string> lines)
        {
            if (!string.Equals(track.ClassName, "BioEvtSysTrackVOElements", StringComparison.Ordinal))
            {
                return;
            }

            string resolved = ResolveDisplayNameStringRef(track, track.GetProperty<IntProperty>("m_nStrRefID")?.Value ?? 0);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                lines.Add(resolved);
            }
        }

        private bool IsUsefulDisplayName(ExportEntry ee, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string trimmed = value.Trim();
            if (string.Equals(trimmed, ee.ObjectName.Instanced, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(trimmed, ee.ClassName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (trimmed.StartsWith("SFX", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Bio", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Default__", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        public int UIndex => Entry?.UIndex ?? 0;

        /// <summary>
        /// Returns true if this entry is an ImportEntry, used for XAML DataTrigger binding
        /// </summary>
        public bool IsImport => Entry is ImportEntry;

        public override string ToString()
        {
            return "TreeViewEntry " + DisplayName;
        }

        /// <summary>
        /// Sorts this node's children in ascending positives first, then descending negatives
        /// </summary>
        internal void SortChildren()
        {
            var exportNodes = Sublinks.Where(x => x.Entry.UIndex > 0).OrderBy(GetExportSortPriority).ThenBy(x => x.UIndex).ToList();
            var importNodes = Sublinks.Where(x => x.Entry.UIndex < 0).OrderByDescending(x => x.UIndex).ToList();

            exportNodes.AddRange(importNodes);
            Sublinks.ClearEx();
            Sublinks.AddRange(exportNodes);
        }

        private static int GetExportSortPriority(TreeViewEntry entry)
        {
            return entry.Entry is ExportEntry { ClassName: "World", ObjectName.Name: "TheWorld" } ? -1 : 0;
        }

        public void Dispose()
        {
            if (Entry is not null)
            {
                Entry.PropertyChanged -= TVEntryPropertyChanged;
                Entry = null;
            }
        }
    }
}
