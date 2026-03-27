using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Globalization;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using static LegendaryExplorer.Tools.TlkManagerNS.TLKManagerWPF;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.DialogueEditor
{
    /// <summary>
    /// Interaction logic for Window1.xaml
    /// </summary>
    public partial class LinkEditor : Window
    {
        private static readonly IValueConverter ReplyCategoryBrushConverter = new ReplyCategoryToBrushConverter();
        private readonly DialogueEditorWindow ParentWindow;
        private readonly DiagNode Dnode;
        private readonly bool IsReply;
        public ObservableCollectionExtended<ReplyChoiceNode> linkTable { get; } = new();
        private bool NeedsSave;
        public bool NeedsPush;
        public ICommand FinishedCommand { get; set; }
        public ICommand AddCommand { get; set; }
        public ICommand DeleteCommand { get; set; }
        public ICommand UpCommand { get; set; }
        public ICommand DownCommand { get; set; }
        public ICommand EditCommand { get; set; }
        public ICommand GoToTargetCommand { get; set; }

        public LinkEditor(DialogueEditorWindow owner, DiagNode node)
        {
            ParentWindow = owner;
            if (ParentWindow.SelectedDialogueNode == null)
            {
                Close();
                throw new Exception("ListEd couldn't find node.");
            }
            LoadCommands();
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);

            Dnode = node;
            IsReply = Dnode.Node.IsReply;

            string s = "E";
            int id = Dnode.NodeID;
            if (IsReply)
            {
                //outType_TB.Text = "E";
                s = "R";
                id -= 1000;
            }

            Title = $"Link Editor - {s}{id} : {Dnode.Node.LineStrRef}";
            LineString_TextBlock.Text = Dnode.Node.Line;

            RebuildDisplayRows(Dnode.Links.OrderBy(link => link.Order));
            GenerateTable();
        }

        private void LoadCommands()
        {
            FinishedCommand = new GenericCommand(Close_LinkEditor);
            AddCommand = new GenericCommand(CloneLink, HasActiveLink);
            EditCommand = new GenericCommand(EditItem, HasActiveLink);
            DeleteCommand = new GenericCommand(DeleteLink, HasActiveLink);
            UpCommand = new RelayCommand(MoveLink);
            DownCommand = new RelayCommand(MoveLink);
            GoToTargetCommand = new RelayCommand(GoToTargetNode);
        }

        private void ParseLink(ReplyChoiceNode link)
        {
            string a = "E";
            int tgtUID = link.Index;
            if (!IsReply)
            {
                a = "R";
                tgtUID += 1000;
            }
            link.IsIncomingConnection = false;
            link.IsDividerRow = false;
            link.NavigationNodeUid = tgtUID;
            link.NodeIDLink = $"{a}{link.Index}";

            link.ReplyLine = GlobalFindStrRefbyID(link.ReplyStrRef, ParentWindow.Pcc);

            var tgtObj = ParentWindow.CurrentObjects.First(t => t.NodeUID == tgtUID);
            var tgtNode = (DiagNode)tgtObj;

            link.TgtFireCnd = "Conditional";
            if (!tgtNode.Node.FiresConditional)
                link.TgtFireCnd = "Bool";

            link.TgtCondition = tgtNode.Node.ConditionalOrBool;
            link.TgtLine = tgtNode.Node.Line;
            link.Ordinal = DialogueEditorWindow.AddOrdinal(link.Order + 1);
            link.TgtSpeaker = tgtNode.Node.SpeakerTag.SpeakerName;
        }

        private static ReplyChoiceNode CreateLinkSectionDivider(string label)
        {
            return new ReplyChoiceNode
            {
                IsDividerRow = true,
                NodeIDLink = label,
                Ordinal = string.Empty,
                TgtFireCnd = string.Empty,
                TgtLine = string.Empty,
                TgtSpeaker = string.Empty,
                ReplyLine = string.Empty
            };
        }

        private static string GetDialogueNodeLinkLabel(DiagNode node)
        {
            return $"{(node.Node.IsReply ? "R" : "E")}{node.Node.NodeCount}";
        }

        private static int GetLinkedTargetNodeUid(DiagNode sourceNode, ReplyChoiceNode link)
        {
            return sourceNode.Node.IsReply ? link.Index : link.Index + 1000;
        }

        private ReplyChoiceNode CreateIncomingLinkRow(DiagNode sourceNode, ReplyChoiceNode sourceLink)
        {
            return new ReplyChoiceNode(sourceLink)
            {
                IsIncomingConnection = true,
                NavigationNodeUid = sourceNode.NodeUID,
                Ordinal = "In",
                NodeIDLink = GetDialogueNodeLinkLabel(sourceNode),
                TgtFireCnd = sourceNode.Node.FiresConditional ? "Conditional" : "Bool",
                TgtCondition = sourceNode.Node.ConditionalOrBool,
                TgtLine = sourceNode.Node.Line,
                TgtSpeaker = sourceNode.Node.SpeakerTag?.SpeakerName ?? "Unknown"
            };
        }

        private static ReplyChoiceNode CreateIncomingStartRow(int startOrder, int targetNodeIndex)
        {
            return new ReplyChoiceNode
            {
                IsIncomingConnection = true,
                NavigationNodeUid = 2000 + targetNodeIndex,
                Ordinal = "In",
                NodeIDLink = $"{DialogueEditorWindow.AddOrdinal(startOrder + 1)} Start",
                TgtLine = $"Start node -> E{targetNodeIndex}",
                TgtFireCnd = string.Empty,
                TgtSpeaker = string.Empty,
                ReplyLine = string.Empty
            };
        }

        private List<ReplyChoiceNode> GetEditableLinks()
        {
            return linkTable.Where(link => link.IsEditableLink).OrderBy(link => link.Order).ToList();
        }

        private List<ReplyChoiceNode> GetIncomingLinkRows()
        {
            List<ReplyChoiceNode> incomingRows = [];

            if (!Dnode.Node.IsReply)
            {
                foreach (var startLink in ParentWindow.SelectedConv.StartingList.Where(kvp => kvp.Value == Dnode.Node.NodeCount).OrderBy(kvp => kvp.Key))
                {
                    incomingRows.Add(CreateIncomingStartRow(startLink.Key, startLink.Value));
                }
            }

            foreach (var sourceNode in ParentWindow.CurrentObjects.OfType<DiagNode>().OrderBy(o => o.NodeUID))
            {
                foreach (var sourceLink in sourceNode.Links.OrderBy(link => link.Order))
                {
                    if (GetLinkedTargetNodeUid(sourceNode, sourceLink) == Dnode.NodeUID)
                    {
                        incomingRows.Add(CreateIncomingLinkRow(sourceNode, sourceLink));
                    }
                }
            }

            return incomingRows;
        }

        private void RebuildDisplayRows(IEnumerable<ReplyChoiceNode> editableLinks = null, ReplyChoiceNode selectedLink = null)
        {
            var outgoingLinks = (editableLinks ?? GetEditableLinks()).OrderBy(link => link.Order).ToList();
            var incomingRows = GetIncomingLinkRows();

            linkTable.ClearEx();

            if (incomingRows.Count > 0)
            {
                linkTable.Add(CreateLinkSectionDivider("Incoming Connections"));
                foreach (var incomingRow in incomingRows)
                {
                    linkTable.Add(incomingRow);
                }
            }

            linkTable.Add(CreateLinkSectionDivider("Outgoing Connections"));
            foreach (var outgoingLink in outgoingLinks)
            {
                ParseLink(outgoingLink);
                linkTable.Add(outgoingLink);
            }

            if (selectedLink != null)
            {
                datagrid_Links.SelectedItem = linkTable.FirstOrDefault(link => ReferenceEquals(link, selectedLink));
            }
        }

        private void GenerateTable()
        {
            datagrid_Links.ItemsSource = linkTable;

            var readOnlyBrush = (Brush)FindResource("ReadOnlyColumnTextBrush");

            var clnO = new DataGridTextColumn
            {
                Header = "#",
                Binding = new Binding(nameof(ReplyChoiceNode.Ordinal)),
                Width = 30,
                IsReadOnly = true,
                Foreground = readOnlyBrush
            };
            datagrid_Links.Columns.Add(clnO);

            var clnA = new DataGridTextColumn
            {
                Header = "Link",
                Binding = new Binding(nameof(ReplyChoiceNode.NodeIDLink)),
                IsReadOnly = true,
                Width = 40,
                FontWeight = FontWeights.Heavy
            };
            datagrid_Links.Columns.Add(clnA);

            if (!IsReply)
            {
                var clnB = new DataGridTextColumn
                {
                    Header = "GUI StrRef",
                    Binding = new Binding(nameof(ReplyChoiceNode.ReplyStrRef)),
                    IsReadOnly = false,
                    Width = 70,
                    FontWeight = FontWeights.Bold
                };
                datagrid_Links.Columns.Add(clnB);

                var clnC = new DataGridTemplateColumn
                {
                    Header = "GUI Choice Line",
                    Width = 120
                };

                var choiceLineTemplate = new DataTemplate();
                var choiceLineTextBlock = new FrameworkElementFactory(typeof(TextBlock));
                choiceLineTextBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReplyChoiceNode.ReplyLine)));
                choiceLineTextBlock.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(ReplyChoiceNode.RCategory)) { Converter = ReplyCategoryBrushConverter });
                choiceLineTextBlock.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                choiceLineTextBlock.SetValue(TextBlock.MarginProperty, new Thickness(2));
                choiceLineTemplate.VisualTree = choiceLineTextBlock;
                clnC.CellTemplate = choiceLineTemplate;
                datagrid_Links.Columns.Add(clnC);

                var clnD = new DataGridTemplateColumn
                {
                    Header = "GUI Category",
                    Width = 150
                };

                var categoryOptions = GetReplyCategoryValues()
                    .Select(category => new ReplyCategoryOption(category, (Brush)ReplyCategoryBrushConverter.Convert(category, typeof(Brush), null, CultureInfo.CurrentCulture)))
                    .ToList();

                // CellTemplate: display the current value as text
                var cellTemplate = new DataTemplate();
                var cellTextBlock = new FrameworkElementFactory(typeof(TextBlock));
                cellTextBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReplyChoiceNode.RCategory)));
                cellTextBlock.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(ReplyChoiceNode.RCategory)) { Converter = ReplyCategoryBrushConverter });
                cellTextBlock.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                cellTextBlock.SetValue(TextBlock.MarginProperty, new Thickness(2));
                cellTemplate.VisualTree = cellTextBlock;
                clnD.CellTemplate = cellTemplate;

                // CellEditingTemplate: show a ComboBox when editing
                var editTemplate = new DataTemplate();
                var cellComboBox = new FrameworkElementFactory(typeof(ComboBox));
                cellComboBox.SetValue(ComboBox.ItemsSourceProperty, categoryOptions);
                cellComboBox.SetValue(ComboBox.SelectedValuePathProperty, nameof(ReplyCategoryOption.Category));
                cellComboBox.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(ReplyChoiceNode.RCategory)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

                var comboItemTemplate = new DataTemplate();
                var comboItemTextBlock = new FrameworkElementFactory(typeof(TextBlock));
                comboItemTextBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReplyCategoryOption.DisplayText)));
                comboItemTextBlock.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(ReplyCategoryOption.Brush)));
                comboItemTemplate.VisualTree = comboItemTextBlock;
                cellComboBox.SetValue(ComboBox.ItemTemplateProperty, comboItemTemplate);

                cellComboBox.SetValue(ComboBox.IsDropDownOpenProperty, true);
                editTemplate.VisualTree = cellComboBox;
                clnD.CellEditingTemplate = editTemplate;
                clnB.FontWeight = FontWeights.Bold;
                datagrid_Links.Columns.Add(clnD);
            }

            var clnE = new DataGridTextColumn
            {
                Header = "Target Check",
                Binding = new Binding(nameof(ReplyChoiceNode.TgtFireCnd)),
                IsReadOnly = true,
                Width = 80,
                Foreground = readOnlyBrush
            };
            datagrid_Links.Columns.Add(clnE);

            var clnF = new DataGridTextColumn
            {
                Header = "Plot Check",
                Binding = new Binding(nameof(ReplyChoiceNode.TgtCondition)),
                IsReadOnly = true,
                Width = 65,
                Foreground = readOnlyBrush
            };
            datagrid_Links.Columns.Add(clnF);

            var clnH = new DataGridTextColumn
            {
                Header = "Speaker",
                Binding = new Binding(nameof(ReplyChoiceNode.TgtSpeaker)),
                IsReadOnly = true,
                Width = 100,
                Foreground = readOnlyBrush
            };
            datagrid_Links.Columns.Add(clnH);

            var clnG = new DataGridTextColumn
            {
                Header = "Target Line",
                Binding = new Binding(nameof(ReplyChoiceNode.TgtLine)),
                IsReadOnly = true,
                Foreground = readOnlyBrush
            };
            datagrid_Links.Columns.Add(clnG);

            var goToTargetColumn = new DataGridTemplateColumn
            {
                Header = "Go",
                Width = 50
            };

            var goToTargetTemplate = new DataTemplate();
            var goToTargetButton = new FrameworkElementFactory(typeof(Button));
            goToTargetButton.SetBinding(Button.ContentProperty, new Binding(nameof(ReplyChoiceNode.NavigationButtonText)));
            goToTargetButton.SetValue(Button.MarginProperty, new Thickness(2));
            var goToTargetButtonStyle = new Style(typeof(Button), TryFindResource(typeof(Button)) as Style);
            goToTargetButtonStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(2)));
            goToTargetButtonStyle.Triggers.Add(new DataTrigger
            {
                Binding = new Binding(nameof(ReplyChoiceNode.HasNavigationTarget)),
                Value = false,
                Setters = { new Setter(Button.VisibilityProperty, Visibility.Collapsed) }
            });
            goToTargetButton.SetValue(Button.StyleProperty, goToTargetButtonStyle);
            goToTargetButton.SetBinding(Button.CommandProperty, new Binding("DataContext.GoToTargetCommand")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1)
            });
            goToTargetButton.SetBinding(Button.CommandParameterProperty, new Binding());
            goToTargetTemplate.VisualTree = goToTargetButton;
            goToTargetColumn.CellTemplate = goToTargetTemplate;
            datagrid_Links.Columns.Add(goToTargetColumn);

            if (!IsReply)
            {
                clnH.Width = 60;
            }

            datagrid_Links.MouseDoubleClick += Datagrid_Table_MouseDoubleClick;
            datagrid_Links.CellEditEnding += Datagrid_Links_CellEditEnding;
        }

        private void Datagrid_Links_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Row.Item is not ReplyChoiceNode { IsEditableLink: true } link)
                {
                    return;
                }

                ParseLink(link);
                NeedsSave = true;
                ReOrderTable(link);
                SaveAndRefreshNode();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Datagrid_Table_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            EditItem();
        }

        private void EditItem()
        {
            if (!HasActiveLink())
                return;

            var editLink = datagrid_Links.SelectedItem as ReplyChoiceNode;

            //Set new Link
            var links = new List<string>();
            int l = editLink.Index; //Get current link
            if (IsReply)
            {
                foreach (var entry in ParentWindow.SelectedConv.EntryList)
                {
                    links.Add($"{entry.NodeCount}: {entry.LineStrRef} {entry.Line}");
                }
            }
            else
            {
                foreach (var entry in ParentWindow.SelectedConv.ReplyList)
                {
                    links.Add($"{entry.NodeCount}: {entry.LineStrRef} {entry.Line}");
                }
            }
            string currentSelection = l >= 0 && l < links.Count ? links[l] : links.FirstOrDefault();
            if (currentSelection is null)
                return;

            if (!DialogueLinkEditDialog.TryEditLink(
                    this,
                    links,
                    currentSelection,
                    !IsReply,
                    editLink.ReplyStrRef,
                    id => GlobalFindStrRefbyID(id, ParentWindow.Pcc),
                    GetReplyCategoryValues().Select(v => v.ToString()),
                    editLink.RCategory.ToString(),
                    out var dialogResult))
            {
                return;
            }

            editLink.Index = links.FindIndex(dialogResult.SelectedTarget.Equals);
            if (!IsReply)
            {
                editLink.ReplyStrRef = dialogResult.ReplyStrRef;
                editLink.RCategory = Enums.Parse<EReplyCategory>(dialogResult.SelectedCategory);
            }

            ParseLink(editLink);
            ReOrderTable(editLink);
            NeedsSave = true;
            SaveAndRefreshNode();
        }

        private void ReOrderTable(ReplyChoiceNode selectedLink = null)
        {
            var editableLinks = GetEditableLinks().OrderBy(link => link.Order).ToList();

            int n = 0;
            foreach (ReplyChoiceNode link in editableLinks)
            {
                link.Order = n;
                link.Ordinal = DialogueEditorWindow.AddOrdinal(link.Order + 1);
                n++;
            }

            RebuildDisplayRows(editableLinks, selectedLink);
        }

        private void Close_LinkEditor()
        {
            SaveToProperties();
            Close();
        }

        private void SaveToProperties()
        {
            var editableLinks = GetEditableLinks();
            if (IsReply)
            {
                Dnode.NodeProp.Properties.AddOrReplaceProp(new ArrayProperty<IntProperty>(editableLinks.Select(link => new IntProperty(link.Index)), "EntryList"));
            }
            else
            {
                Dnode.NodeProp.Properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(editableLinks.Select(link =>
                    new StructProperty("BioDialogReplyListDetails", new PropertyCollection
                    {
                        new IntProperty(link.Index, "nIndex"),
                        new StringRefProperty(link.ReplyStrRef, "srParaphrase"),
                        new StrProperty("", "sParaphrase"),
                        new EnumProperty(link.RCategory.ToString(), "EReplyCategory", ParentWindow.Pcc.Game, "Category"),
                        new NoneProperty()
                    })
                ), "ReplyListNew"));
            }
            NeedsPush = true;
            NeedsSave = false;
        }

        private void SaveAndRefreshNode()
        {
            if (!NeedsSave)
            {
                return;
            }

            SaveToProperties();
            ParentWindow.PushLocalGraphChanges(Dnode);
            NeedsPush = false;
        }

        private bool HasActiveLink()
        {
            return datagrid_Links.SelectedItem is ReplyChoiceNode { IsEditableLink: true };
        }

        private void DeleteLink()
        {
            if (datagrid_Links.SelectedItem is not ReplyChoiceNode { IsEditableLink: true } result)
            {
                return;
            }

            var editableLinks = GetEditableLinks();
            editableLinks.Remove(result);
            NeedsSave = true;
            ReOrderTable();
            SaveAndRefreshNode();
        }

        private void CloneLink()
        {
            if (datagrid_Links.SelectedItem is not ReplyChoiceNode { IsEditableLink: true } donor)
            {
                return;
            }

            var editableLinks = GetEditableLinks();
            editableLinks.Add(new ReplyChoiceNode(donor) { Order = editableLinks.Count + 1 });
            NeedsSave = true;
            RebuildDisplayRows(editableLinks, donor);
            SaveAndRefreshNode();
        }

        private void MoveLink(object obj)
        {
            if (datagrid_Links.SelectedItem is not ReplyChoiceNode { IsEditableLink: true } selectedLink)
            {
                return;
            }

            string command = obj as string;
            var editableLinks = GetEditableLinks();
            int moveLinkID = editableLinks.IndexOf(selectedLink);
            if ((moveLinkID == 0 && command is "Up" or "Top") || (moveLinkID >= editableLinks.Count - 1 && command is "Down" or "Bottom"))
                return;

            int numSwaps = 1;
            if (command is "Top")
            {
                numSwaps = moveLinkID;
            }
            else if (command is "Bottom")
            {
                numSwaps = editableLinks.Count - 1 - moveLinkID;
            }

            int swapDir = command is "Up" or "Top" ? -1 : 1; //"Up" is down in index

            for (int i = 0; Math.Abs(i) < numSwaps; i += swapDir)
            {
                ReplyChoiceNode moveNode = editableLinks[moveLinkID];
                ReplyChoiceNode swapNode = editableLinks[moveLinkID + i + swapDir];
                (moveNode.Order, swapNode.Order) = (swapNode.Order, moveNode.Order);
            }
            NeedsSave = true;
            ReOrderTable(selectedLink);
            SaveAndRefreshNode();
        }

        private void GoToTargetNode(object obj)
        {
            ReplyChoiceNode targetLink = obj as ReplyChoiceNode ?? datagrid_Links.SelectedItem as ReplyChoiceNode;
            if (targetLink is not { HasNavigationTarget: true })
            {
                return;
            }

            datagrid_Links.SelectedItem = targetLink;
            ParentWindow.SelectGraphObjectByUid(targetLink.NavigationNodeUid, centerView: true);
            ParentWindow.Activate();
        }

        private void LinkEd_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (NeedsSave)
            {
                switch (MessageBox.Show("There are unsaved changes. Do you wish to save now?", "Link Editor", MessageBoxButton.YesNoCancel))
                {
                    case MessageBoxResult.Yes:
                        SaveToProperties();
                        break;
                    case MessageBoxResult.Cancel:
                        e.Cancel = true;
                        break;
                }
            }
        }

        private EReplyCategory[] GetReplyCategoryValues()
        {
            if (ParentWindow.Pcc.Game.IsGame1())
            {
                return new[]
                {
                    EReplyCategory.REPLY_CATEGORY_DEFAULT,
                    EReplyCategory.REPLY_CATEGORY_AGREE,
                    EReplyCategory.REPLY_CATEGORY_DISAGREE,
                    EReplyCategory.REPLY_CATEGORY_FRIENDLY,
                    EReplyCategory.REPLY_CATEGORY_HOSTILE,
                    EReplyCategory.REPLY_CATEGORY_INVESTIGATE,
                };
            }
            return Enums.GetValues<EReplyCategory>();
        }

        private sealed class ReplyCategoryOption
        {
            public ReplyCategoryOption(EReplyCategory category, Brush brush)
            {
                Category = category;
                DisplayText = category.ToString();
                Brush = brush;
            }

            public EReplyCategory Category { get; }
            public string DisplayText { get; }
            public Brush Brush { get; }
        }

        private sealed class ReplyCategoryToBrushConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                var category = value is EReplyCategory eReplyCategory ? eReplyCategory : EReplyCategory.REPLY_CATEGORY_DEFAULT;
                var color = category switch
                {
                    EReplyCategory.REPLY_CATEGORY_PARAGON_INTERRUPT => DObj.paraintColor,
                    EReplyCategory.REPLY_CATEGORY_RENEGADE_INTERRUPT => DObj.renintColor,
                    EReplyCategory.REPLY_CATEGORY_AGREE => DObj.agreeColor,
                    EReplyCategory.REPLY_CATEGORY_DISAGREE => DObj.disagreeColor,
                    EReplyCategory.REPLY_CATEGORY_FRIENDLY => DObj.friendlyColor,
                    EReplyCategory.REPLY_CATEGORY_HOSTILE => DObj.hostileColor,
                    _ => DObj.connectionColor
                };

                return new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }
    }
}
