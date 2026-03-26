using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Gammtek.Conduit.MassEffect3.SFXGame.QuestMap;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorer.Tools.PlotEditor.Dialogs;

namespace LegendaryExplorer.Tools.PlotEditor
{
	/// <summary>
	/// Interaction logic for StateTaskListsView.xaml
	/// </summary>
	public partial class StateTaskListsView : NotifyPropertyChangedControlBase
    {
		public StateTaskListsView()
		{
			InitializeComponent();
            SetStateTaskLists(null);
        }
        private KeyValuePair<int, BioStateTaskList> _selectedStateTaskList;
        private BioTaskEval _selectedTaskEval;
        private ObservableCollection<KeyValuePair<int, BioStateTaskList>> _allStateTaskLists;
        private ObservableCollection<KeyValuePair<int, BioStateTaskList>> _stateTaskLists;
        private ObservableCollection<BioTaskEval> _visibleTaskEvals;
        private Dictionary<int, string> _questNamesById;
        private int? _filteredStateTaskListId;

        public bool CanAddTaskEval
        {
            get
            {
                if (StateTaskLists == null
                    || !StateTaskLists.Any())
                {
                    return false;
                }

                return SelectedStateTaskList.Value != null;
            }
        }

        public bool CanRemoveStateTaskList
        {
            get
            {
                if (StateTaskLists == null
                    || !StateTaskLists.Any())
                {
                    return false;
                }

                return SelectedStateTaskList.Value != null;
            }
        }

        public bool CanRemoveTaskEval
        {
            get
            {
                if (StateTaskLists == null
                    || !StateTaskLists.Any())
                {
                    return false;
                }

                if (VisibleTaskEvals == null || !VisibleTaskEvals.Any())
                {
                    return false;
                }

                return SelectedTaskEval != null;
            }
        }

        public KeyValuePair<int, BioStateTaskList> SelectedStateTaskList
        {
            get => _selectedStateTaskList;
            set
            {
                SetProperty(ref _selectedStateTaskList, value);
                RefreshVisibleTaskEvals();
                SelectedTaskEval = VisibleTaskEvals.FirstOrDefault();
                OnPropertyChanged(nameof(CanAddTaskEval));
                OnPropertyChanged(nameof(CanRemoveStateTaskList));
                OnPropertyChanged(nameof(CanRemoveTaskEval));
            }
        }

        public BioTaskEval SelectedTaskEval
        {
            get => _selectedTaskEval;
            set
            {
                SetProperty(ref _selectedTaskEval, value);
                OnPropertyChanged(nameof(CanRemoveTaskEval));
            }
        }

        public int? FilteredStateTaskListId
        {
            get => _filteredStateTaskListId;
            set
            {
                SetProperty(ref _filteredStateTaskListId, value);
                OnPropertyChanged(nameof(IsFilteredToSingleQuest));
                ApplyFilter();
            }
        }

        public void SetQuestNames(IEnumerable<KeyValuePair<int, BioQuest>> quests)
        {
            _questNamesById = quests?.ToDictionary(pair => pair.Key, pair => pair.Value?.QuestName) ?? new Dictionary<int, string>();
            RefreshTaskEvalQuestNames();
            RefreshVisibleTaskEvals();
        }

        public bool IsFilteredToSingleQuest => FilteredStateTaskListId.HasValue;

        public IEnumerable<KeyValuePair<int, BioStateTaskList>> AllStateTaskLists => _allStateTaskLists ?? Enumerable.Empty<KeyValuePair<int, BioStateTaskList>>();

        public ObservableCollection<BioTaskEval> VisibleTaskEvals
        {
            get => _visibleTaskEvals;
            set
            {
                SetProperty(ref _visibleTaskEvals, value);
                OnPropertyChanged(nameof(CanRemoveTaskEval));
            }
        }

        public ObservableCollection<KeyValuePair<int, BioStateTaskList>> StateTaskLists
        {
            get => _stateTaskLists;
            set
            {
                SetProperty(ref _stateTaskLists, value);
                OnPropertyChanged(nameof(CanAddTaskEval));
                OnPropertyChanged(nameof(CanRemoveStateTaskList));
                OnPropertyChanged(nameof(CanRemoveTaskEval));
            }
        }

        public void AddStateTaskList()
        {
            if (FilteredStateTaskListId is int filteredId)
            {
                if (AllStateTaskLists.Any(pair => pair.Value.TaskEvals.Any(taskEval => taskEval.Quest == filteredId)))
                {
                    ApplyFilter();
                    return;
                }

                var stateTaskList = new BioStateTaskList();
                stateTaskList.TaskEvals = InitCollection<BioTaskEval>();
                stateTaskList.TaskEvals.Add(new BioTaskEval { Quest = filteredId });
                AddStateTaskList(GetMaxStateTaskListId() + 1, stateTaskList);
                return;
            }

            var dlg = new NewObjectDialog
            {
                ContentText = "New StateTaskList",
                ObjectId = (GetMaxStateTaskListId() + 1)
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0)
            {
                return;
            }

            AddStateTaskList(dlg.ObjectId);
        }

        public void AddStateTaskList(int id, BioStateTaskList taskList = null)
        {
            EnsureAllStateTaskLists();

            if (id < 0)
            {
                return;
            }

            if (_allStateTaskLists.Any(pair => pair.Key == id))
            {
                ApplyFilter();
                return;
            }

            if (taskList == null)
            {
                taskList = new BioStateTaskList();
            }

            taskList.TaskEvals = taskList.TaskEvals != null
                ? InitCollection(taskList.TaskEvals)
                : InitCollection<BioTaskEval>();

            var stateTaskList = new KeyValuePair<int, BioStateTaskList>(id, taskList);

            _allStateTaskLists.Add(stateTaskList);

            ApplyFilter();

            SelectedStateTaskList = StateTaskLists.FirstOrDefault(pair => pair.Key == id);
        }

        public void AddTaskEval()
        {
            AddTaskEval(null);
        }

        public void AddTaskEval(BioTaskEval taskEval)
        {
            if (StateTaskLists == null || SelectedStateTaskList.Value == null)
            {
                return;
            }

            if (taskEval == null)
            {
                taskEval = new BioTaskEval();
            }

            if (FilteredStateTaskListId is int filteredId)
            {
                taskEval.Quest = filteredId;
            }

            SelectedStateTaskList.Value.TaskEvals.Add(taskEval);

            RefreshVisibleTaskEvals();
            SelectedTaskEval = taskEval;
        }

        public void ChangeStateTaskListId()
        {
            if (SelectedStateTaskList.Value == null)
            {
                return;
            }

            var dlg = new ChangeObjectIdDialog
            {
                ContentText = $"Change id of StateTaskList #{SelectedStateTaskList.Key}",
                ObjectId = SelectedStateTaskList.Key
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0)
            {
                return;
            }

            var stateTaskList = SelectedStateTaskList.Value;

            _allStateTaskLists.Remove(SelectedStateTaskList);

            AddStateTaskList(dlg.ObjectId, stateTaskList);
        }

        public void CopyStateTaskList()
        {
            if (FilteredStateTaskListId.HasValue || SelectedStateTaskList.Value == null)
            {
                return;
            }

            var dlg = new CopyObjectDialog
            {
                ContentText = $"Copy StateTaskList {SelectedStateTaskList.Key}",
                ObjectId = SelectedStateTaskList.Key
            };

            if (dlg.ShowDialog() == false || dlg.ObjectId < 0 || SelectedStateTaskList.Key == dlg.ObjectId)
            {
                return;
            }

            AddStateTaskList(dlg.ObjectId, new BioStateTaskList(SelectedStateTaskList.Value));
        }

        public void CopyTaskEval()
        {
            if (StateTaskLists == null || SelectedStateTaskList.Value == null || SelectedTaskEval == null)
            {
                return;
            }

            AddTaskEval(new BioTaskEval(SelectedTaskEval));
        }

        public void RemoveStateTaskList()
        {
            if (_allStateTaskLists == null || SelectedStateTaskList.Value == null)
            {
                return;
            }

            var index = _allStateTaskLists.IndexOf(SelectedStateTaskList);

            if (!_allStateTaskLists.Remove(SelectedStateTaskList))
            {
                return;
            }

            ApplyFilter();

            if (StateTaskLists.Any())
            {
                if (FilteredStateTaskListId.HasValue)
                {
                    SelectedStateTaskList = StateTaskLists.First();
                }
                else
                {
                    SelectedStateTaskList = ((index - 1) >= 0 && index - 1 < StateTaskLists.Count)
                        ? StateTaskLists[index - 1]
                        : StateTaskLists.First();
                }
            }
        }

        public void RemoveTaskEval()
        {
            if (StateTaskLists == null || SelectedStateTaskList.Value == null || SelectedTaskEval == null)
            {
                return;
            }

            var index = VisibleTaskEvals.IndexOf(SelectedTaskEval);

            if (!SelectedStateTaskList.Value.TaskEvals.Remove(SelectedTaskEval))
            {
                return;
            }

            RefreshVisibleTaskEvals();

            if (VisibleTaskEvals.Any())
            {
                SelectedTaskEval = ((index - 1) >= 0)
                    ? VisibleTaskEvals[index - 1]
                    : VisibleTaskEvals.First();
            }
            else
            {
                SelectedTaskEval = null;
            }
        }

        public void SetStateTaskLists(IEnumerable<KeyValuePair<int, BioStateTaskList>> collection)
        {
            if (collection == null)
            {
                _allStateTaskLists = new ObservableCollection<KeyValuePair<int, BioStateTaskList>>();
            }
            else
            {
                _allStateTaskLists = InitCollection(collection);

                foreach (var taskEval in _allStateTaskLists)
                {
                    taskEval.Value.TaskEvals = InitCollection(taskEval.Value.TaskEvals);
                }
            }

            ApplyFilter();
        }

        public void UpdateStateTaskListId(int oldId, int newId)
        {
            if (_allStateTaskLists == null || oldId < 0 || newId < 0 || oldId == newId)
            {
                return;
            }

            bool updatedAny = false;
            foreach (KeyValuePair<int, BioStateTaskList> stateTaskList in _allStateTaskLists)
            {
                foreach (BioTaskEval taskEval in stateTaskList.Value.TaskEvals.Where(taskEval => taskEval.Quest == oldId))
                {
                    taskEval.Quest = newId;
                    updatedAny = true;
                }
            }

            if (!updatedAny)
            {
                return;
            }

            ApplyFilter();
        }

        private void EnsureAllStateTaskLists()
        {
            _allStateTaskLists ??= InitCollection<KeyValuePair<int, BioStateTaskList>>();
        }

        private void ApplyFilter()
        {
            EnsureAllStateTaskLists();
            RefreshTaskEvalQuestNames();

            IEnumerable<KeyValuePair<int, BioStateTaskList>> filteredStateTaskLists = _allStateTaskLists;
            if (FilteredStateTaskListId is int filteredId)
            {
                filteredStateTaskLists = filteredStateTaskLists.Where(pair => pair.Value.TaskEvals.Any(taskEval => taskEval.Quest == filteredId));
            }

            var previousSelection = _selectedStateTaskList;
            StateTaskLists = InitCollection(filteredStateTaskLists);

            if (!StateTaskLists.Any())
            {
                VisibleTaskEvals = InitCollection<BioTaskEval>();
                SelectedTaskEval = null;
                SelectedStateTaskList = default;
                return;
            }

            SelectedStateTaskList = StateTaskLists.FirstOrDefault(pair => pair.Key == previousSelection.Key)
                is var matchingPair && matchingPair.Value != null
                    ? matchingPair
                    : StateTaskLists.First();
        }

        private void RefreshVisibleTaskEvals()
        {
            if (SelectedStateTaskList.Value?.TaskEvals == null)
            {
                VisibleTaskEvals = InitCollection<BioTaskEval>();
                return;
            }

            IEnumerable<BioTaskEval> visibleTaskEvals = SelectedStateTaskList.Value.TaskEvals;
            if (FilteredStateTaskListId is int filteredId)
            {
                visibleTaskEvals = visibleTaskEvals.Where(taskEval => taskEval.Quest == filteredId);
            }

            VisibleTaskEvals = InitCollection(visibleTaskEvals);
        }

        private void RefreshTaskEvalQuestNames()
        {
            if (_allStateTaskLists == null)
            {
                return;
            }

            foreach (BioTaskEval taskEval in _allStateTaskLists.SelectMany(pair => pair.Value.TaskEvals))
            {
                taskEval.QuestName = _questNamesById != null && _questNamesById.TryGetValue(taskEval.Quest, out string questName)
                    ? questName
                    : null;
            }
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

        private int GetMaxStateTaskListId()
        {
            return AllStateTaskLists.Any() ? AllStateTaskLists.Max(b => b.Key) : -1;
        }

        private void ChangeStateTaskListId_Click(object sender, RoutedEventArgs e)
        {
            ChangeStateTaskListId();
        }

        private void CopyStateTaskList_Click(object sender, RoutedEventArgs e)
        {
            CopyStateTaskList();
        }

        private void RemoveStateTaskList_Click(object sender, RoutedEventArgs e)
        {
            RemoveStateTaskList();
        }

        private void AddStateTaskList_Click(object sender, RoutedEventArgs e)
        {
            AddStateTaskList();
        }

        private void CopyTaskEval_Click(object sender, RoutedEventArgs e)
        {
            CopyTaskEval();
        }

        private void RemoveTaskEval_Click(object sender, RoutedEventArgs e)
        {
            RemoveTaskEval();
        }

        private void AddTaskEval_Click(object sender, RoutedEventArgs e)
        {
            AddTaskEval();
        }

        private void TaskEvalQuest_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (SelectedTaskEval == null)
            {
                return;
            }

            SelectedTaskEval.QuestName = _questNamesById != null && _questNamesById.TryGetValue(SelectedTaskEval.Quest, out string questName)
                ? questName
                : null;
            RefreshVisibleTaskEvals();
            SelectedTaskEval = VisibleTaskEvals.FirstOrDefault(taskEval => ReferenceEquals(taskEval, SelectedTaskEval)) ?? SelectedTaskEval;
        }
    }
}
