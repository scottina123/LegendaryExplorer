using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using Gammtek.Conduit.MassEffect3.SFXGame.StateEventMap;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;
using LegendaryExplorer.Tools.PlotEditor.Dialogs;
using LegendaryExplorerCore.PlotDatabase;

namespace LegendaryExplorer.Tools.PlotEditor
{
	/// <summary>
	///   Interaction logic for StateEventMapView.xaml
	/// </summary>
	public partial class StateEventMapView : NotifyPropertyChangedControlBase
    {
		public StateEventMapView()
		{
			InitializeComponent();
            SetFromStateEventMap(new BioStateEventMap());
        }
        private KeyValuePair<int, BioStateEvent> _selectedStateEvent;
        private BioStateEventElement _selectedStateEventElement;
        private BioStateEventElement _subscribedElement;
        private bool _propagatingChanges;
        private ObservableCollection<KeyValuePair<int, BioStateEvent>> _stateEvents;
        private ObservableCollection<KeyValuePair<int, BioStateEvent>> _visibleStateEvents;
        private string _stateEventSearchText;
        private bool _searchStateEventIds = true;
        private bool _searchPlotBools = true;
        private bool _searchPlotInts = true;
        private bool _searchPlotFloats = true;
        private bool _searchPlotNames = true;
        
        public bool CanAddStateEventElement => StateEvents != null && SelectedStateEvent.Value != null;

        public bool CanRemoveStateEvent => StateEvents != null && SelectedStateEvent.Value != null;

        public bool CanRemoveStateEventElement
        {
            get
            {
                if (StateEvents == null || SelectedStateEvent.Value == null)
                {
                    return false;
                }

                return SelectedStateEventElement != null;
            }
        }

        public KeyValuePair<int, BioStateEvent> SelectedStateEvent
        {
            get => _selectedStateEvent;
            set
            {
                SetProperty(ref _selectedStateEvent, value);
                OnPropertyChanged(nameof(CanAddStateEventElement));
                OnPropertyChanged(nameof(CanRemoveStateEvent));
                OnPropertyChanged(nameof(CanRemoveStateEventElement));
            }
        }

        public BioStateEventElement SelectedStateEventElement
        {
            get => _selectedStateEventElement;
            set
            {
                if (_subscribedElement != null)
                {
                    _subscribedElement.PropertyChanged -= SelectedElement_PropertyChanged;
                }
                SetProperty(ref _selectedStateEventElement, value);
                _subscribedElement = value;
                if (_subscribedElement != null)
                {
                    _subscribedElement.PropertyChanged += SelectedElement_PropertyChanged;
                }
                OnPropertyChanged(nameof(CanRemoveStateEventElement));
            }
        }

        public ObservableCollection<KeyValuePair<int, BioStateEvent>> StateEvents
        {
            get => _stateEvents;
            set
            {
                SetProperty(ref _stateEvents, value);
                ApplyStateEventSearchFilter();
            }
        }

        public bool SearchStateEventIds
        {
            get => _searchStateEventIds;
            set
            {
                SetProperty(ref _searchStateEventIds, value);
                ApplyStateEventSearchFilter();
            }
        }

        public bool SearchPlotBools
        {
            get => _searchPlotBools;
            set
            {
                SetProperty(ref _searchPlotBools, value);
                ApplyStateEventSearchFilter();
            }
        }

        public bool SearchPlotInts
        {
            get => _searchPlotInts;
            set
            {
                SetProperty(ref _searchPlotInts, value);
                ApplyStateEventSearchFilter();
            }
        }

        public bool SearchPlotFloats
        {
            get => _searchPlotFloats;
            set
            {
                SetProperty(ref _searchPlotFloats, value);
                ApplyStateEventSearchFilter();
            }
        }

        public bool SearchPlotNames
        {
            get => _searchPlotNames;
            set
            {
                SetProperty(ref _searchPlotNames, value);
                ApplyStateEventSearchFilter();
            }
        }

        public ObservableCollection<KeyValuePair<int, BioStateEvent>> VisibleStateEvents
        {
            get => _visibleStateEvents;
            set => SetProperty(ref _visibleStateEvents, value);
        }

        public string StateEventSearchText
        {
            get => _stateEventSearchText;
            set
            {
                SetProperty(ref _stateEventSearchText, value);
                ApplyStateEventSearchFilter();
            }
        }

        public void AddStateEvent()
        {
            if (StateEvents == null)
            {
                StateEvents = InitCollection<KeyValuePair<int, BioStateEvent>>();
            }

            var dlg = new NewObjectDialog
            {
                ContentText = "New state event",
                ObjectId = GetMaxStateEventId() + 1
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0)
            {
                return;
            }

            AddStateEvent(dlg.ObjectId);
        }

        public void AddStateEvent(int id, BioStateEvent stateEvent = null)
        {
            if (StateEvents == null)
            {
                StateEvents = InitCollection<KeyValuePair<int, BioStateEvent>>();
            }

            if (StateEvents.Any(pair => pair.Key == id))
            {
                return;
            }

            if (stateEvent == null)
            {
                stateEvent = new BioStateEvent(InitCollection<BioStateEventElement>());
            }

            if (!(stateEvent.Elements is ObservableCollection<BioStateEventElement>))
            {
                stateEvent.Elements = InitCollection(stateEvent.Elements);
            }

            stateEvent.PlotPath = PlotDatabases.FindPlotTransitionByID(id, package.Game)?.Path;
            var stateEventPair = new KeyValuePair<int, BioStateEvent>(id, stateEvent);

            StateEvents.Add(stateEventPair);

            ApplyStateEventSearchFilter();

            SelectedStateEvent = stateEventPair;
        }

        public void AddStateEventElement(BioStateEventElementType elementType)
        {
            if (StateEvents == null || SelectedStateEvent.Value == null)
            {
                return;
            }

            switch (elementType)
            {
                case BioStateEventElementType.Bool:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementBool());

                        break;
                    }
                case BioStateEventElementType.Consequence:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementConsequence());

                        break;
                    }
                case BioStateEventElementType.Float:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementFloat());

                        break;
                    }
                case BioStateEventElementType.Function:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementFunction());

                        break;
                    }
                case BioStateEventElementType.Int:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementInt());

                        break;
                    }
                case BioStateEventElementType.LocalBool:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalBool());

                        break;
                    }
                case BioStateEventElementType.LocalFloat:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalFloat());

                        break;
                    }
                case BioStateEventElementType.LocalInt:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalInt());

                        break;
                    }
                case BioStateEventElementType.Substate:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementSubstate(siblingIndices: InitCollection<int>()));

                        break;
                    }
            }
        }

        public void AddMultipleStateEventElements(BioStateEventElementType elementType)
        {
            if (StateEvents == null || SelectedStateEvent.Value == null)
            {
                return;
            }

            var dlg = new AddMultipleStateEventElementsDialog
            {
                ContentText = $"Add multiple {elementType} elements",
                StartingValue = 0,
                Count = 1
            };

            if (dlg.ShowDialog() == false || dlg.Count <= 0)
            {
                return;
            }

            int startValue = dlg.StartingValue;
            int count = dlg.Count;

            for (int i = 0; i < count; i++)
            {
                int value = startValue + i;
                switch (elementType)
                {
                    case BioStateEventElementType.Bool:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementBool(globalBool: value));
                        break;
                    case BioStateEventElementType.Consequence:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementConsequence(consequence: value));
                        break;
                    case BioStateEventElementType.Float:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementFloat(globalFloat: value));
                        break;
                    case BioStateEventElementType.Function:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementFunction(parameter: value));
                        break;
                    case BioStateEventElementType.Int:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementInt(globalInt: value));
                        break;
                    case BioStateEventElementType.LocalBool:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalBool());
                        break;
                    case BioStateEventElementType.LocalFloat:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalFloat());
                        break;
                    case BioStateEventElementType.LocalInt:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalInt(newValue: value));
                        break;
                    case BioStateEventElementType.Substate:
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementSubstate(globalBool: value, siblingIndices: InitCollection<int>()));
                        break;
                }
            }
        }

        public void AddSubstateSiblingIndex()
        {
            if (StateEvents == null || SelectedStateEvent.Value == null || SelectedStateEventElement == null)
            {
                return;
            }

            var selectedSubstate = SelectedStateEventElement as BioStateEventElementSubstate;

            if (selectedSubstate == null)
            {
                return;
            }

            if (!(selectedSubstate.SiblingIndices is ObservableCollection<int>))
            {
                selectedSubstate.SiblingIndices = InitCollection(selectedSubstate.SiblingIndices);
            }

            var dlg = new NewObjectDialog
            {
                ContentText = "New substate sibling index",
                ObjectId = 0
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0)
            {
                return;
            }

            selectedSubstate.SiblingIndices.Add(dlg.ObjectId);
        }

        public void CopyStateEvent()
        {
            if (StateEvents == null || SelectedStateEvent.Value == null)
            {
                return;
            }

            var dlg = new CopyObjectDialog
            {
                ContentText = $"Copy state event #{SelectedStateEvent.Key}",
                ObjectId = SelectedStateEvent.Key
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0)
            {
                return;
            }

            AddStateEvent(dlg.ObjectId, new BioStateEvent(SelectedStateEvent.Value));
        }

        public void CloneStateEvent()
        {
            if (StateEvents == null || SelectedStateEvent.Value == null)
            {
                return;
            }

            var newId = GetMaxStateEventId() + 1;
            AddStateEvent(newId, new BioStateEvent(SelectedStateEvent.Value));
        }

        public void CopyStateEventElement()
        {
            if (StateEvents == null || SelectedStateEvent.Value == null || SelectedStateEventElement == null)
            {
                return;
            }

            var elementType = SelectedStateEventElement.ElementType;

            switch (elementType)
            {
                case BioStateEventElementType.Bool:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementBool(SelectedStateEventElement as BioStateEventElementBool));

                        break;
                    }
                case BioStateEventElementType.Consequence:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementConsequence(SelectedStateEventElement as BioStateEventElementConsequence));

                        break;
                    }
                case BioStateEventElementType.Float:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementFloat(SelectedStateEventElement as BioStateEventElementFloat));

                        break;
                    }
                case BioStateEventElementType.Function:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementFunction(SelectedStateEventElement as BioStateEventElementFunction));

                        break;
                    }
                case BioStateEventElementType.Int:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementInt(SelectedStateEventElement as BioStateEventElementInt));

                        break;
                    }
                case BioStateEventElementType.LocalBool:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalBool(SelectedStateEventElement as BioStateEventElementLocalBool));

                        break;
                    }
                case BioStateEventElementType.LocalFloat:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalFloat(SelectedStateEventElement as BioStateEventElementLocalFloat));

                        break;
                    }
                case BioStateEventElementType.LocalInt:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementLocalInt(SelectedStateEventElement as BioStateEventElementLocalInt));

                        break;
                    }
                case BioStateEventElementType.Substate:
                    {
                        SelectedStateEvent.Value.Elements.Add(new BioStateEventElementSubstate(SelectedStateEventElement as BioStateEventElementSubstate));

                        break;
                    }
            }
        }

        public static bool TryFindStateEventMap(IMEPackage pcc, out ExportEntry export, string objectName="StateTransitionMap")
        {
            // ME1 has 2 BioStateEventMaps. We generally want the one called StateTransitionMap, but there is an optional
            // parameter for other ones, such as ConsequenceMap.

            // BioConsequenceMaps have the same format as BioStateEventMaps
            string[] stateEventClasses = { "BioStateEventMap", "BioConsequenceMap" };
            export = pcc.Exports.FirstOrDefault(exp => stateEventClasses.Contains(exp.ClassName) && exp.ObjectName == objectName);

            return export != null;
        }

        public IMEPackage package { get; private set; }

        public void Open(IMEPackage pcc, string objectName = "StateTransitionMap")
        {
            if (!TryFindStateEventMap(pcc, out ExportEntry export, objectName))
            {
                return;
            }

            var stateEventMap = BinaryBioStateEventMap.Load(export);
            StateEvents = InitCollection(stateEventMap.StateEvents.OrderBy(stateEvent => stateEvent.Key));
            foreach (var stateEvent in StateEvents)
            {
                stateEvent.Value.PlotPath = PlotDatabases.FindPlotTransitionByID(stateEvent.Key, pcc.Game)?.Path;
            }
            SetListsAsBindable();
            package = pcc;
        }

        
        public BioStateEventMap ToStateEventMap()
        {
            var stateEventMap = new BioStateEventMap
            {
                StateEvents = StateEvents.ToDictionary(pair => pair.Key, pair => pair.Value)
            };

            return stateEventMap;
        }

        public void ChangeStateEventId()
        {
            if (SelectedStateEvent.Value == null)
            {
                return;
            }

            var dlg = new ChangeObjectIdDialog
            {
                ContentText = $"Change id of codex page #{SelectedStateEvent.Key}",
                ObjectId = SelectedStateEvent.Key
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0 || dlg.ObjectId == SelectedStateEvent.Key)
            {
                return;
            }

            var codexSection = SelectedStateEvent.Value;

            StateEvents.Remove(SelectedStateEvent);

            AddStateEvent(dlg.ObjectId, codexSection);
            ApplyStateEventSearchFilter();
        }

        public void RemoveStateEvent()
        {
            if (StateEvents == null || SelectedStateEvent.Value == null)
            {
                return;
            }

            var index = StateEvents.IndexOf(SelectedStateEvent);

            if (!StateEvents.Remove(SelectedStateEvent))
            {
                return;
            }

            ApplyStateEventSearchFilter();

            if (VisibleStateEvents.Any())
            {
                SelectedStateEvent = VisibleStateEvents.FirstOrDefault(pair => pair.Key == SelectedStateEvent.Key)
                    is var matchingPair && matchingPair.Value != null
                        ? matchingPair
                        : VisibleStateEvents.First();
            }
        }

        public void RemoveStateEventElement()
        {
            if (StateEvents == null || SelectedStateEvent.Value == null || SelectedStateEventElement == null)
            {
                return;
            }

            var index = SelectedStateEvent.Value.Elements.IndexOf(SelectedStateEventElement);

            if (!SelectedStateEvent.Value.Elements.Remove(SelectedStateEventElement))
            {
                return;
            }

            if (SelectedStateEvent.Value.Elements.Any())
            {
                SelectedStateEventElement = ((index - 1) >= 0)
                    ? SelectedStateEvent.Value.Elements[index - 1]
                    : SelectedStateEvent.Value.Elements.First();
            }
        }

        public void RemoveSubstateSiblingIndex(int siblingIndex)
        {
            if (StateEvents != null && SelectedStateEvent.Value != null && SelectedStateEventElement != null 
                && SelectedStateEventElement is BioStateEventElementSubstate selectedSubstate 
                && siblingIndex >= 0 
                && siblingIndex < selectedSubstate.SiblingIndices.Count)
            {
                selectedSubstate.SiblingIndices.RemoveAt(siblingIndex);
            }
        }

        public void SelectStateEvent(KeyValuePair<int, BioStateEvent> stateEvent)
        {
            SelectedStateEvent = stateEvent;
            StateEventMapListBox.ScrollIntoView(SelectedStateEvent);
            StateEventMapListBox.Focus();
        }

        protected void SetListsAsBindable()
        {
            //StateEvents = new ObservableCollection<KeyValuePair<int, BioStateEvent>>(StateEvents);

            foreach (var stateEvent in StateEvents)
            {
                stateEvent.Value.Elements = InitCollection(stateEvent.Value.Elements);

                foreach (var substateStateEventElement in stateEvent.Value.Elements
                    .OfType<BioStateEventElementSubstate>().Select(stateEventElement => stateEventElement))
                {
                    substateStateEventElement.SiblingIndices = InitCollection(substateStateEventElement.SiblingIndices);
                }
            }

            ApplyStateEventSearchFilter();
        }

        
        private static ObservableCollection<T> InitCollection<T>()
        {
            return new ObservableCollection<T>();
        }

        
        private static ObservableCollection<T> InitCollection<T>(IEnumerable<T> collection)
        {
            if (collection == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(collection));
            }

            return new ObservableCollection<T>(collection);
        }

        private void SelectedElement_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_propagatingChanges)
                return;

            _propagatingChanges = true;
            try
            {
                var sourceElement = (BioStateEventElement)sender;
                var sourceType = sourceElement.GetType();
                System.Reflection.PropertyInfo propertyInfo = sourceType.GetProperty(e.PropertyName);

                if (propertyInfo == null || !propertyInfo.CanRead || !propertyInfo.CanWrite)
                    return;

                Type propType = propertyInfo.PropertyType;
                if (propType != typeof(int) && propType != typeof(float) && propType != typeof(bool) && propType != typeof(string))
                    return;

                object newValue = propertyInfo.GetValue(sourceElement);

                foreach (BioStateEventElement element in StateEventElementsListBox.SelectedItems)
                {
                    if (element != sourceElement && element.GetType() == sourceType)
                    {
                        propertyInfo.SetValue(element, newValue);
                    }
                }
            }
            finally
            {
                _propagatingChanges = false;
            }
        }

        private int GetMaxStateEventId()
        {
            return StateEvents.Any() ? StateEvents.Max(pair => pair.Key) : -1;
        }

        private void ApplyStateEventSearchFilter()
        {
            if (StateEvents == null)
            {
                VisibleStateEvents = InitCollection<KeyValuePair<int, BioStateEvent>>();
                return;
            }

            IEnumerable<KeyValuePair<int, BioStateEvent>> filteredStateEvents = StateEvents;
            if (!string.IsNullOrWhiteSpace(StateEventSearchText))
            {
                string searchText = StateEventSearchText.Trim();
                filteredStateEvents = filteredStateEvents.Where(stateEvent => StateEventMatchesSearch(stateEvent, searchText));
            }

            VisibleStateEvents = InitCollection(filteredStateEvents);

            if (VisibleStateEvents.Any(stateEvent => stateEvent.Key == SelectedStateEvent.Key))
            {
                return;
            }

            SelectedStateEvent = VisibleStateEvents.FirstOrDefault();
        }

        private bool StateEventMatchesSearch(KeyValuePair<int, BioStateEvent> stateEventPair, string searchText)
        {
            BioStateEvent stateEvent = stateEventPair.Value;
            return (SearchStateEventIds && SearchTextMatches(stateEventPair.Key, searchText))
                   || (SearchPlotNames && SearchTextMatches(stateEvent?.PlotPath, searchText))
                   || stateEvent?.Elements.Any(element => StateEventElementMatchesSearch(element, searchText)) == true;
        }

        private bool StateEventElementMatchesSearch(BioStateEventElement element, string searchText)
        {
            return element switch
            {
                BioStateEventElementBool boolElement => PlotElementMatchesSearch(boolElement.GlobalBool, searchText, "bool"),
                BioStateEventElementSubstate substateElement => PlotElementMatchesSearch(substateElement.GlobalBool, searchText, "bool"),
                BioStateEventElementInt intElement => PlotElementMatchesSearch(intElement.GlobalInt, searchText, "int"),
                BioStateEventElementFloat floatElement => PlotElementMatchesSearch(floatElement.GlobalFloat, searchText, "float"),
                _ => false
            };
        }

        private bool PlotElementMatchesSearch(int id, string searchText, string plotType)
        {
            return ((SearchPlotNames && SearchTextMatches(GetPlotElementPath(id, plotType), searchText))
                    || (SearchPlotNames && SearchTextMatches(GetPlotElementLabel(id, plotType), searchText))
                    || (IsPlotTypeFilterEnabled(plotType) && SearchTextMatches(id, searchText)));
        }

        private bool IsPlotTypeFilterEnabled(string plotType)
        {
            return plotType switch
            {
                "bool" => SearchPlotBools,
                "int" => SearchPlotInts,
                "float" => SearchPlotFloats,
                _ => false
            };
        }

        private string GetPlotElementPath(int id, string plotType)
        {
            if (package == null)
            {
                return null;
            }

            return plotType switch
            {
                "bool" => PlotDatabases.FindPlotBoolByID(id, package.Game)?.Path,
                "int" => PlotDatabases.FindPlotIntByID(id, package.Game)?.Path,
                "float" => PlotDatabases.FindPlotFloatByID(id, package.Game)?.Path,
                _ => null
            };
        }

        private string GetPlotElementLabel(int id, string plotType)
        {
            if (package == null)
            {
                return null;
            }

            return plotType switch
            {
                "bool" => PlotDatabases.FindPlotBoolByID(id, package.Game)?.Label,
                "int" => PlotDatabases.FindPlotIntByID(id, package.Game)?.Label,
                "float" => PlotDatabases.FindPlotFloatByID(id, package.Game)?.Label,
                _ => null
            };
        }

        private static bool SearchTextMatches(string value, string searchText)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && value.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                   && !value.Equals("No Data", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SearchTextMatches(int value, string searchText)
        {
            return value.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase);
        }

        private void SetFromStateEventMap(BioStateEventMap bioStateEventMap)
        {
            if (bioStateEventMap == null)
            {
                return;
            }

            StateEvents = InitCollection(bioStateEventMap.StateEvents);

            SetListsAsBindable();
        }

        private void ChangeStateEventId_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ChangeStateEventId();
        }

        private void CopyStateEvent_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            CopyStateEvent();
        }

        private void CloneStateEvent_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            CloneStateEvent();
        }

        private void RemoveStateEvent_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RemoveStateEvent();
        }

        private void AddStateEvent_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddStateEvent();
        }

        private void CopyStateEventElement_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            CopyStateEventElement();
        }

        private void RemoveStateEventElement_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RemoveStateEventElement();
        }

        private void AddStateEventElement_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddStateEventElement((BioStateEventElementType)NewStateEventElementComboBox.Tag);
        }

        private void AddMultipleStateEventElements_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddMultipleStateEventElements((BioStateEventElementType)NewStateEventElementComboBox.Tag);
        }

        private void AddSubstateSiblingIndex_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AddSubstateSiblingIndex();
        }

        private void RemoveSubstateSiblingIndex_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var button = (Button)sender;
            RemoveSubstateSiblingIndex((int)button.Tag);
        }
    }

    /// <summary>
	///   Resolves a name entry for a given Pcc
	/// </summary>
    public class NameConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] is IMEPackage pcc && values[1] is int index && values[2] is int instanceNum)
            {
                return new LegendaryExplorerCore.Unreal.NameReference(pcc.GetNameEntry(index), instanceNum).Instanced;

            }
            return "";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Gets a plot path
    /// </summary>
    public class PlotPathConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] is MEGame game && values[1] is int index)
            {
                switch((string)parameter)
                {
                    case "bool":
                        return PlotDatabases.FindPlotBoolByID(index, game)?.Path;
                    case "int":
                        return PlotDatabases.FindPlotIntByID(index, game)?.Path;
                    case "float":
                        return PlotDatabases.FindPlotFloatByID(index, game)?.Path;
                    case "conditional":
                        return PlotDatabases.FindPlotConditionalByID(index, game)?.Path;
                    case "transition":
                        return PlotDatabases.FindPlotTransitionByID(index, game)?.Path;
                }
            }
            return "";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
