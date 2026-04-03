using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.Unreal;
using Piccolo;
using Piccolo.Event;
using Piccolo.Nodes;
using Piccolo.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using static LegendaryExplorer.Tools.TlkManagerNS.TLKManagerWPF;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.DialogueEditor
{
    public class PlotFieldEditorInfo
    {
        public string FieldTag;
        public RectangleF Bounds;
        public bool IsConditionalSection;
        public bool IsFloat;
    }

    [DebuggerDisplay("DiagEdEdge | {originator} to {inputIndex}")]
    public class DiagEdEdge : PPath
    {
        public PNode start;
        public PNode end;
        public DBox originator;
        public int inputIndex;
        public Color BaseColor;

        public DObj GetEndOwner()
        {
            for (PNode node = end; node is not null; node = node.Parent)
            {
                if (node is DObj owner)
                {
                    return owner;
                }
            }

            return null;
        }

        public void ApplyVisualState(bool isHighlighted, bool isDimmed)
        {
            Pen = DObj.CreateConnectionPen(BaseColor, isHighlighted, isDimmed);
        }
    }

    [DebuggerDisplay("DObj | #{" + nameof(NodeUID) + "}")]
    public abstract class DObj : PNode, IDisposable
    {
        public IMEPackage pcc;
        public ConvGraphEditor g;
        public static Color paraintColor = Color.Blue;
        public static Color renintColor = Color.Red;
        public static Color agreeColor = Color.DodgerBlue;
        public static Color disagreeColor = Color.Tomato;
        public static Color friendlyColor = Color.FromArgb(3, 3, 116);//dark blue
        public static Color hostileColor = Color.FromArgb(116, 3, 3);//dark red
        public static Color entryColor = Color.DarkGoldenrod;
        public static Color entryPenColor = Color.Black;
        public static Color replyColor = Color.CadetBlue;
        public static Color replyPenColor = Color.Black;
        public static Color connectionColor = Color.Black;  // Base connection line color (black for light mode, white for dark mode)
        protected static readonly Color EventColor = Color.FromArgb(214, 30, 28);
        public static Color titleColor = Color.FromArgb(255, 255, 128);
        public static Color titleBoxColor = Color.FromArgb(112, 112, 112);
        public static Color backgroundColor = Color.FromArgb(128, 128, 128);
        public static Color graphBackgroundColor = Color.FromArgb(64, 64, 64);
        public static Color boxColor = Color.FromArgb(140, 140, 140);
        public static Color boxTextColor = Color.White;
        public static Color linkTextColor = Color.Black;  // Color for link paraphrase text
        protected static Brush titleBoxBrush = new SolidBrush(Color.FromArgb(112, 112, 112));
        public static Brush _titleBoxBrush
        {
            get => titleBoxBrush;
            set
            {
                titleBoxBrush?.Dispose();
                titleBoxBrush = value;
            }
        }
        protected static readonly Brush mostlyTransparentBrush = new SolidBrush(Color.FromArgb(1, 255, 255, 255));
        public static Brush _nodeBrush = new SolidBrush(Color.FromArgb(140, 140, 140));
        protected static Brush nodeBrush => _nodeBrush;
        protected static readonly Pen selectedPen = new Pen(Color.FromArgb(255, 255, 0));
        private const float DefaultConnectionWidth = 1f;
        private const float HighlightedConnectionWidth = 2.5f;
        private const int DefaultConnectionAlpha = 255;
        private const int DimmedConnectionAlpha = 72;
        public static bool draggingOutlink;
        public static PNode dragTarget;
        public static bool OutputNumbers;

        public RectangleF posAtDragStart;

        protected string listname;
        public string ListName => listname;
        public int NodeUID;
        public ExportEntry Export => export;
        public virtual bool IsSelected { get; set; }

        protected ExportEntry export;
        protected Pen outlinePen;
        protected DText comment;

        protected DObj(ConvGraphEditor ConvGraphEditor)
        {
            g = ConvGraphEditor;
        }

        public virtual void CreateConnections(IList<DObj> objects) { }
        public virtual void Layout(float x, float y) => SetOffset(x, y);
        public virtual IEnumerable<DiagEdEdge> Edges => Enumerable.Empty<DiagEdEdge>();

        protected Color getColor(EReplyCategory t) =>
            t switch
            {
                EReplyCategory.REPLY_CATEGORY_PARAGON_INTERRUPT => paraintColor,
                EReplyCategory.REPLY_CATEGORY_RENEGADE_INTERRUPT => renintColor,
                EReplyCategory.REPLY_CATEGORY_AGREE => agreeColor,
                EReplyCategory.REPLY_CATEGORY_DISAGREE => disagreeColor,
                EReplyCategory.REPLY_CATEGORY_FRIENDLY => friendlyColor,
                EReplyCategory.REPLY_CATEGORY_HOSTILE => hostileColor,
                _ => connectionColor
            };

        protected static string GetReplyCategoryAcronym(EReplyCategory category) =>
            category switch
            {
                EReplyCategory.REPLY_CATEGORY_DEFAULT => "D",
                EReplyCategory.REPLY_CATEGORY_AGREE => "A",
                EReplyCategory.REPLY_CATEGORY_DISAGREE => "DI",
                EReplyCategory.REPLY_CATEGORY_FRIENDLY => "F",
                EReplyCategory.REPLY_CATEGORY_HOSTILE => "H",
                EReplyCategory.REPLY_CATEGORY_INVESTIGATE => "I",
                EReplyCategory.REPLY_CATEGORY_RENEGADE_INTERRUPT => "RI",
                EReplyCategory.REPLY_CATEGORY_PARAGON_INTERRUPT => "PI",
                _ => "D"
            };

        public static Pen CreateConnectionPen(Color baseColor, bool isHighlighted = false, bool isDimmed = false)
        {
            int alpha = isDimmed ? DimmedConnectionAlpha : DefaultConnectionAlpha;
            float width = isHighlighted ? HighlightedConnectionWidth : DefaultConnectionWidth;

            var pen = new Pen(Color.FromArgb(alpha, baseColor), width)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };

            return pen;
        }

        protected static Brush CreateTitleBoxBrush()
        {
            return titleBoxBrush is not null ? (Brush)titleBoxBrush.Clone() : new SolidBrush(boxColor);
        }

        protected static Brush CreateNodeBrush()
        {
            return _nodeBrush is not null ? (Brush)_nodeBrush.Clone() : new SolidBrush(boxColor);
        }

        internal static T FindAncestor<T>(PNode node) where T : class
        {
            for (PNode current = node; current is not null; current = current.Parent)
            {
                if (current is T match)
                {
                    return match;
                }
            }

            return null;
        }

        public virtual void Dispose()
        {
            g = null;
            pcc = null;
            export = null;
        }
    }

    [DebuggerDisplay("DBox | #{" + nameof(NodeUID) + "}")]
    public abstract class DBox : DObj
    {
        public override IEnumerable<DiagEdEdge> Edges => Outlinks.SelectMany(l => l.Edges);
        protected static readonly Brush outputBrush = new SolidBrush(Color.Black);
        public static Color lineColor = Color.FromArgb(74, 63, 190);
        public static float LineScaleOption = 1.0f;
        public static bool LinesAtTop;
        public struct OutputLink
        {
            public PPath node;
            public List<int> Links;
            public int InputIndices;
            public string Desc;
            public string Detail;
            public List<DiagEdEdge> Edges;
            public EReplyCategory RCat;
        }

        public struct InputLink
        {
            public PPath node;
            public string Desc;
            public int index;
            public bool hasName;
            public List<DiagEdEdge> Edges;
        }

        protected PPath titleBox;
        protected PPath outLinkBox;
        public readonly List<OutputLink> Outlinks = new List<OutputLink>();
        protected readonly OutputDragHandler outputDragHandler;
        protected PPath CreateActionLinkBox() => PPath.CreateRectangle(0, -4, 10, 8);

        protected DBox(ConvGraphEditor ConvGraphEditor)
            : base(ConvGraphEditor)
        {
            outputDragHandler = new OutputDragHandler(ConvGraphEditor, this);
        }

        protected PBasicInputEventHandler CreateOutgoingLinkDoubleClickHandler(int linkIndex)
        {
            var handler = new PBasicInputEventHandler();
            handler.DoubleClick = (_, e) =>
            {
                if (this is DiagNode diagNode && e.Button == MouseButtons.Left)
                {
                    diagNode.EditOutgoingLink(linkIndex);
                    e.Handled = true;
                }
            };
            return handler;
        }

        public override void CreateConnections(IList<DObj> objects)
        {
            foreach (OutputLink outLink in Outlinks)
            {
                foreach (int link in outLink.Links)
                {
                    foreach (DiagNode destAction in objects.OfType<DiagNode>())
                    {
                        if (destAction.NodeID == link)
                        {
                            PPath p1 = outLink.node;
                            var edge = new DiagEdEdge();
                            if (p1.Tag == null)
                                p1.Tag = new List<DiagEdEdge>();
                            ((List<DiagEdEdge>)p1.Tag).Add(edge);
                            destAction.InputEdges.Add(edge);
                            edge.BaseColor = getColor(outLink.RCat);
                            edge.ApplyVisualState(isHighlighted: false, isDimmed: false);
                            edge.start = p1;
                            edge.end = destAction;
                            edge.originator = this;
                            edge.inputIndex = outLink.InputIndices;
                            g.addEdge(edge);
                            outLink.Edges.Add(edge);
                        }
                    }
                }
            }
        }

        public void RecreateConnections(IList<DObj> objects)
        {
            foreach (OutputLink outLink in Outlinks)
            {
                foreach (int link in outLink.Links)
                {
                    foreach (DiagNode destAction in objects.OfType<DiagNode>())
                    {
                        if (destAction.NodeID == link)
                        {
                            PPath p1 = outLink.node;
                            var edge = new DiagEdEdge();
                            if (p1.Tag == null)
                                p1.Tag = new List<DiagEdEdge>();
                            ((List<DiagEdEdge>)p1.Tag).Add(edge);
                            destAction.InputEdges.Add(edge);
                            edge.BaseColor = getColor(outLink.RCat);
                            edge.ApplyVisualState(isHighlighted: false, isDimmed: false);
                            edge.start = p1;
                            edge.end = destAction;
                            edge.originator = this;
                            edge.inputIndex = outLink.InputIndices;
                            g.addEdge(edge);
                            outLink.Edges.Add(edge);
                            destAction.RefreshInputLinks();
                        }
                    }
                }
            }
        }

        public void RemoveConnections()
        {
            foreach (OutputLink outLink in Outlinks)
            {
                DiagEdEdge[] edges = outLink.Edges.ToArray();
                foreach (var e in edges)
                {
                    if (e.GetEndOwner() is DiagNode destAction)
                    {
                        destAction.InputEdges.Remove(e);
                        if (destAction.InLinks != null)
                        {
                            foreach (var inputLink in destAction.InLinks)
                            {
                                inputLink.Edges?.Remove(e);
                            }
                        }
                    }

                    if (outLink.node?.Tag is List<DiagEdEdge> taggedEdges)
                    {
                        taggedEdges.Remove(e);
                        if (taggedEdges.Count == 0)
                        {
                            outLink.node.Tag = null;
                        }
                    }

                    g.edgeLayer.RemoveChild(e);
                }
                outLink.Edges.Clear();
            }
        }

        protected float GetTitleBox(string s, float w)
        {
            DText title = new DText(s, boxTextColor)
            {
                TextAlignment = StringAlignment.Center,
                ConstrainWidthToTextWidth = false,
                X = 0,
                Y = 3,
                Pickable = false
            };
            if (title.Width + 20 > w)
            {
                w = title.Width + 20;
            }
            title.Width = w;
            titleBox = PPath.CreateRectangle(0, 0, w, title.Height + 5);
            titleBox.Pen = outlinePen;
            titleBox.Brush = CreateTitleBoxBrush();
            titleBox.AddChild(title);
            titleBox.Pickable = false;
            return w;
        }

        protected float GetTitlePlusLineBox(string s, string l, string n, float w)
        {
            DText nodeID = new DText(n, boxTextColor) //Add node count to left side
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = false,
                X = 0,
                Y = 3,
                Pickable = false
            };
            float nodeIDWidth = nodeID.Width + 4;

            DText title = new DText(s, boxTextColor)
            {
                TextAlignment = StringAlignment.Center,
                ConstrainWidthToTextWidth = false,
                X = nodeIDWidth,
                Y = 3,
                Pickable = false
            };
            if (title.Width + nodeIDWidth + 20 > w)
            {
                w = title.Width + nodeIDWidth + 20;
            }
            title.Width = w - nodeIDWidth;

            float lineX = w / LineScaleOption + 5;
            float lineY = 3;
            if (LinesAtTop)
            {
                lineX = 2;
                lineY = -title.Height + 2;
            }
            DText line = null;
            if (LineScaleOption > 0)
            {
                line = new DText(l, lineColor, false, LineScaleOption) //Add line string to right side
                {
                    TextAlignment = StringAlignment.Near,
                    ConstrainWidthToTextWidth = false,
                    ConstrainHeightToTextHeight = false,
                    X = lineX,
                    Y = lineY,
                    Pickable = false
                };
            }

            titleBox = PPath.CreateRectangle(0, 0, w, title.Height + 5);
            titleBox.Pen = new Pen(entryPenColor);
            if (NodeUID < 1000)
            {
                titleBox.Brush = new SolidBrush(entryColor); ;
            }
            else if (NodeUID < 2000)
            {
                titleBox.Brush = new SolidBrush(replyColor); ;
            }
            else
            {
                titleBox.Brush = CreateTitleBoxBrush();
            }
            titleBox.AddChild(nodeID);
            titleBox.AddChild(title);
            if (LineScaleOption > 0)
            {
                titleBox.AddChild(line);
            }
            titleBox.Pickable = false;
            return w;
        }

        protected class OutputDragHandler : PDragEventHandler
        {
            private readonly ConvGraphEditor ConvGraphEditor;
            private readonly DBox DObj;

            public OutputDragHandler(ConvGraphEditor graph, DBox obj)
            {
                ConvGraphEditor = graph;
                DObj = obj;
            }

            private static PNode ResolveDropTarget(PInputEventArgs e)
            {
                if (dragTarget != null)
                {
                    return dragTarget;
                }

                for (PNode node = e.InputManager?.MouseOver?.PickedNode; node is not null; node = node.Parent)
                {
                    if (node is PPath path
                        && global::LegendaryExplorer.DialogueEditor.DObj.FindAncestor<DiagNode>(path) is DiagNode inputOwner
                        && inputOwner.InLinks != null
                        && inputOwner.InLinks.Any(link => ReferenceEquals(link.node, path)))
                    {
                        return path;
                    }

                    if (node is DiagNode diagNode && diagNode.InLinks?.Count > 0)
                    {
                        return diagNode;
                    }
                }

                return null;
            }

            public override bool DoesAcceptEvent(PInputEventArgs e)
            {
                return e.IsMouseEvent && (e.Button != MouseButtons.None || e.IsMouseEnterOrMouseLeave) && !e.Handled;
            }

            protected override void OnStartDrag(object sender, PInputEventArgs e)
            {
                DObj.MoveToBack();
                e.Handled = true;
                PNode p1 = ((PNode)sender).Parent;
                PNode p2 = (PNode)sender;
                var edge = new DiagEdEdge();
                if (p1.Tag == null)
                    p1.Tag = new List<DiagEdEdge>();
                if (p2.Tag == null)
                    p2.Tag = new List<DiagEdEdge>();
                ((List<DiagEdEdge>)p1.Tag).Add(edge);
                ((List<DiagEdEdge>)p2.Tag).Add(edge);
                edge.start = p1;
                edge.end = p2;
                Color dragColor = global::LegendaryExplorer.DialogueEditor.DObj.connectionColor;
                if (p1 is PPath startPath)
                {
                    if (startPath.Pen is not null)
                    {
                        dragColor = startPath.Pen.Color;
                    }
                    else if (startPath.Brush is SolidBrush brush)
                    {
                        dragColor = brush.Color;
                    }
                }
                edge.BaseColor = dragColor;
                edge.ApplyVisualState(isHighlighted: false, isDimmed: false);
                edge.originator = DObj;
                ConvGraphEditor.addEdge(edge);
                base.OnStartDrag(sender, e);
                draggingOutlink = true;
            }

            protected override void OnDrag(object sender, PInputEventArgs e)
            {
                base.OnDrag(sender, e);
                e.Handled = true;
                ConvGraphEditor.UpdateEdge(((List<DiagEdEdge>)((PNode)sender).Tag)[0]);
            }

            protected override void OnEndDrag(object sender, PInputEventArgs e)
            {
                DiagEdEdge edge = ((List<DiagEdEdge>)((PNode)sender).Tag)[0];
                ((PNode)sender).SetOffset(0, 0);
                ((List<DiagEdEdge>)((PNode)sender).Parent.Tag).Remove(edge);
                ConvGraphEditor.edgeLayer.RemoveChild(edge);
                ((List<DiagEdEdge>)((PNode)sender).Tag).RemoveAt(0);
                base.OnEndDrag(sender, e);
                draggingOutlink = false;

                PNode resolvedDropTarget = ResolveDropTarget(e);
                if (resolvedDropTarget != null)
                {
                    DObj.CreateOutlink(((PPath)sender).Parent, resolvedDropTarget);
                }

                dragTarget = null;
            }
        }

        public virtual void CreateOutlink(PNode n1, PNode n2) { }

        public void RemoveOutlink(DiagEdEdge edge)
        {
            for (int i = 0; i < Outlinks.Count; i++)
            {
                OutputLink outLink = Outlinks[i];
                for (int j = 0; j < outLink.Edges.Count; j++)
                {
                    DiagEdEdge DiagEdEdge = outLink.Edges[j];
                    if (DiagEdEdge == edge)
                    {
                        RemoveOutlink(i, j);
                        return;
                    }
                }
            }
        }

        public virtual void RemoveOutlink(int linkconnection, int linkIndex) { }

        public override void Dispose()
        {
            base.Dispose();
            if (outputDragHandler != null)
            {
                foreach (var x in Outlinks) x.node[0].RemoveInputEventListener(outputDragHandler);
            }
        }
    }

    [DebuggerDisplay("DStart | #{" + nameof(NodeUID) + "}")]
    public sealed class DStart : DBox
    {
        public int StartNumber;
        public int Order;
        private readonly DialogueEditorWindow Editor;

        public DStart(DialogueEditorWindow editor, int orderKey, int StartNbr, float x, float y, ConvGraphEditor ConvGraphEditor)
            : base(ConvGraphEditor)
        {
            NodeUID = 2000 + StartNbr;
            Editor = editor;
            Order = orderKey;
            string ordinal = DialogueEditorWindow.AddOrdinal(orderKey + 1);
            StartNumber = StartNbr;
            outlinePen = new Pen(EventColor);
            listname = $"{ordinal} Start Node: {StartNbr}"; ;

            float starty = 0;
            float w = 15;
            float midW = 50;
            GetTitleBox(listname, 20);

            w += titleBox.Width;
            OutputLink l = new OutputLink
            {
                Links = new List<int>(StartNbr),
                InputIndices = new int(),
                Edges = new List<DiagEdEdge>(),
                Desc = $"Out {StartNbr}",
                RCat = EReplyCategory.REPLY_CATEGORY_DEFAULT
            };
            int linkedOp = StartNbr;
            l.Links.Add(linkedOp);
            l.InputIndices = 0;
            l.node = CreateActionLinkBox();
            l.node.Brush = new SolidBrush(connectionColor);
            l.node.Pen = new Pen(connectionColor);
            l.node.Pickable = false;

            PPath dragger = CreateActionLinkBox();
            dragger.Brush = mostlyTransparentBrush;
            dragger.Pen = new Pen(connectionColor);
            dragger.X = l.node.X;
            dragger.Y = l.node.Y;
            dragger.AddInputEventListener(outputDragHandler);
            l.node.AddChild(dragger);
            Outlinks.Add(l);
            outLinkBox = new PPath();
            DText t2 = new DText($"{StartNbr} :");
            if (t2.Width + 10 > midW) midW = t2.Width + 10;
            t2.X = 0 - t2.Width;
            t2.Y = starty - 10;
            t2.Pickable = false;
            t2.AddChild(l.node);
            outLinkBox.AddChild(t2);
            outLinkBox.AddPolygon(new[] { new PointF(0, 0), new PointF(0, starty), new PointF(-0.5f * midW, starty + 30), new PointF(0 - midW, starty), new PointF(0 - midW, 0), new PointF(midW / -2, -30) });
            outLinkBox.Pickable = false;
            outLinkBox.Pen = outlinePen;
            outLinkBox.Brush = CreateNodeBrush();
            float h = titleBox.Height + 1;
            outLinkBox.TranslateBy(titleBox.Width / 2 + midW / 2, h + 30);

            h += outLinkBox.Height + 1;
            bounds = new RectangleF(0, 0, w, h);
            AddChild(titleBox);
            AddChild(outLinkBox);
            Pickable = true;
            SetOffset(x, y);
            MouseEnter += OnMouseEnter;
            MouseLeave += OnMouseLeave;
        }

        private bool _isSelected;
        public override bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                if (value)
                {
                    titleBox.Pen = selectedPen;
                    outLinkBox.Pen = selectedPen;
                    MoveToFront();
                }
                else
                {
                    titleBox.Pen = outlinePen;
                    outLinkBox.Pen = outlinePen;
                }
            }
        }

        public override void CreateOutlink(PNode n1, PNode n2)
        {
            DStart start = FindAncestor<DStart>(n1);
            DiagNode end = FindAncestor<DiagNode>(n2);
            if (start == null || end == null)
            {
                return;
            }

            if (end.GetType() != typeof(DiagNodeEntry))
            {
                MessageBox.Show("You cannot link start nodes to replies.\r\nStarts must link to entries.", "Dialogue Editor");
                return;
            }
            Editor.SelectedConv.StartingList[Order] = end.NodeID;
            Editor.RecreateNodesToProperties(Editor.SelectedConv);
        }

        public void OnMouseEnter(object sender, PInputEventArgs e)
        {
        }

        public void OnMouseLeave(object sender, PInputEventArgs e)
        {
        }

    }

    [DebuggerDisplay("DiagNode | #{NodeUID}")]
    public abstract class DiagNode : DBox
    {
        private static readonly Color SpeakerHighlightOutlineColor = Color.DarkOrange;
        public override IEnumerable<DiagEdEdge> Edges => InLinks.SelectMany(l => l.Edges).Union(base.Edges);
        public List<DiagEdEdge> InputEdges = new List<DiagEdEdge>();
        public List<InputLink> InLinks;
        protected PNode inputLinkBox;
        protected PPath box;
        protected float originalX;
        protected float originalY;
        public StructProperty NodeProp;
        public DialogueNodeExtended Node;
        public int NodeID;
        public ObservableCollectionExtended<ReplyChoiceNode> Links = new ObservableCollectionExtended<ReplyChoiceNode>();
        static readonly Color insideTextColor = Color.FromArgb(213, 213, 213);//white
        protected InputDragHandler inputDragHandler = new InputDragHandler();
        protected DialogueEditorWindow Editor;
        private RectangleF lineStrRefEditorBounds;
        private readonly List<PlotFieldEditorInfo> plotFieldEditors = [];
        private static readonly Dictionary<string, (bool checksExpanded, bool transitionsExpanded, bool matineeExpanded)> plotSectionStates = [];
        private readonly string plotSectionStateKey;
        private PNode plotChecksSection;
        private PNode plotTransitionsSection;
        private PNode matineeSection;
        private float plotSectionsStartY;
        private float baseBoxHeightWithoutPlotSections;
        private float nodeBoxWidth;
        private bool isSpeakerHighlighted;

        public DiagNode(DialogueEditorWindow editor, DialogueNodeExtended node, float x, float y, ConvGraphEditor ConvGraphEditor)
            : base(ConvGraphEditor)
        {
            Editor = editor;
            Node = node;
            NodeProp = node.NodeProp;
            NodeID = Node.NodeCount;
            pcc = editor.Pcc;
            originalX = x;
            originalY = y;

            plotSectionStateKey = $"{editor.SelectedConv?.Export?.UIndex}:{node.IsReply}:{node.NodeCount}";
            if (plotSectionStates.TryGetValue(plotSectionStateKey, out var savedState))
            {
                Node.PlotChecksExpanded = savedState.checksExpanded;
                Node.PlotTransitionsExpanded = savedState.transitionsExpanded;
                Node.MatineeExpanded = savedState.matineeExpanded;
            }
            else
            {
                plotSectionStates[plotSectionStateKey] = (Node.PlotChecksExpanded, Node.PlotTransitionsExpanded, Node.MatineeExpanded);
            }
        }

        public void EditOutgoingLink(int linkIndex)
        {
            Editor?.EditGraphOutgoingLink(this, linkIndex);
        }

        public bool IsSpeakerHighlighted
        {
            get => isSpeakerHighlighted;
            set
            {
                if (isSpeakerHighlighted == value)
                {
                    return;
                }

                isSpeakerHighlighted = value;
                ApplyAccentVisualState();
            }
        }

        public void SyncIdentityFromNode()
        {
            NodeProp = Node.NodeProp;
            NodeID = Node.IsReply ? Node.NodeCount + 1000 : Node.NodeCount;
            NodeUID = NodeID;
            listname = $"{(Node.IsReply ? "R" : "E")}{Node.NodeCount} {Node.Line}";
        }

        private void SavePlotSectionState()
        {
            if (!string.IsNullOrEmpty(plotSectionStateKey))
            {
                plotSectionStates[plotSectionStateKey] = (Node.PlotChecksExpanded, Node.PlotTransitionsExpanded, Node.MatineeExpanded);
            }
        }

        protected void ApplyAccentVisualState()
        {
            if (titleBox == null || box == null)
            {
                return;
            }

            if (IsSelected)
            {
                titleBox.Pen = selectedPen;
                box.Pen = selectedPen;
                MoveToFront();
                return;
            }

            var outlineColor = IsSpeakerHighlighted ? SpeakerHighlightOutlineColor : GetDefaultOutlineColor();
            var outline = new Pen(outlineColor);
            titleBox.Pen = outline;
            box.Pen = outline;
        }

        private Color GetDefaultOutlineColor()
        {
            return NodeUID switch
            {
                < 1000 => entryPenColor,
                < 2000 => replyPenColor,
                _ => Color.Black
            };
        }

        private string GetListenerDisplayName()
        {
            var listener = Editor?.ListenersList?.FirstOrDefault(s => s.SpeakerID == Node.Listener);
            if (listener != null)
            {
                return listener.DisplayName;
            }

            return Node.Listener.ToString();
        }

        private PPath CreateNodeParticipantSelectorButton(float x, float y, bool isSpeaker)
        {
            var button = PPath.CreateRectangle(x, y, 12, 12);
            button.Brush = new SolidBrush(Color.FromArgb(30, boxTextColor));
            button.Pen = new Pen(Color.FromArgb(150, boxTextColor));

            button.Click += (_, e) =>
            {
                if (e.Button != MouseButtons.Left)
                {
                    return;
                }

                e.Handled = true;
                ShowNodeParticipantSelectorMenu(isSpeaker);
            };

            titleBox.AddChild(button);
            return button;
        }

        private void ShowNodeParticipantSelectorMenu(bool isSpeaker)
        {
            if (Editor?.SelectedConv == null)
            {
                return;
            }

            var menu = new ContextMenuStrip();
            var source = isSpeaker ? Editor.SelectedSpeakerList : Editor.ListenersList;

            foreach (var speaker in source)
            {
                int speakerId = speaker.SpeakerID;
                var item = new ToolStripMenuItem(speaker.DisplayName)
                {
                    Checked = (isSpeaker ? Node.SpeakerIndex : Node.Listener) == speakerId
                };

                item.Click += (_, __) =>
                {
                    if (isSpeaker)
                    {
                        Editor.UpdateNodeSpeakerFromGraph(Node, speakerId);
                    }
                    else
                    {
                        Editor.UpdateNodeListenerFromGraph(Node, speakerId);
                    }
                };

                menu.Items.Add(item);
            }

            ApplyThemeToSelectorMenu(menu);

            if (menu.Items.Count > 0)
            {
                menu.Show(Cursor.Position);
            }
            else
            {
                menu.Dispose();
            }
        }

        private static void ApplyThemeToSelectorMenu(ContextMenuStrip menu)
        {
            bool isDarkMode = LegendaryExplorer.Misc.AppSettings.Settings.Global_DarkMode_Enabled;
            if (isDarkMode)
            {
                Color bg = Color.FromArgb(0x2D, 0x2D, 0x30);
                Color fg = Color.FromArgb(0xE0, 0xE0, 0xE0);
                menu.Renderer = new DarkSelectorMenuRenderer();
                menu.BackColor = bg;
                menu.ForeColor = fg;
                foreach (ToolStripItem item in menu.Items)
                {
                    item.BackColor = bg;
                    item.ForeColor = fg;
                }
            }
            else
            {
                menu.Renderer = null;
                menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                menu.BackColor = SystemColors.Control;
                menu.ForeColor = SystemColors.ControlText;
                foreach (ToolStripItem item in menu.Items)
                {
                    item.BackColor = SystemColors.Control;
                    item.ForeColor = SystemColors.ControlText;
                }
            }
        }

        private sealed class DarkSelectorMenuRenderer : ToolStripProfessionalRenderer
        {
            private static readonly Color BackgroundColor = Color.FromArgb(0x2D, 0x2D, 0x30);
            private static readonly Color BorderColor = Color.FromArgb(0x3F, 0x3F, 0x46);
            private static readonly Color SeparatorColor = Color.FromArgb(0x3F, 0x3F, 0x46);
            private static readonly Color HighlightColor = Color.FromArgb(0x3E, 0x3E, 0x42);
            private static readonly Color TextColor = Color.FromArgb(0xE0, 0xE0, 0xE0);
            private static readonly Color DisabledTextColor = Color.FromArgb(0x65, 0x65, 0x69);

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using var brush = new SolidBrush(BackgroundColor);
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using var pen = new Pen(BorderColor);
                var rect = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
                e.Graphics.DrawRectangle(pen, rect);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                using var brush = new SolidBrush(e.Item.Selected && e.Item.Enabled ? HighlightColor : BackgroundColor);
                e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? TextColor : DisabledTextColor;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using var pen = new Pen(SeparatorColor);
                e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
            }
        }

        private float BuildSpeakerListenerTitleBox(float minimumWidth)
        {
            string nodeIdText = Node.IsReply ? $"R{Node.NodeCount}" : $"E{Node.NodeCount}";
            string speakerText = $"S: {Node.SpeakerTag?.DisplayName ?? "Unknown"}";
            string listenerText = $"L: {GetListenerDisplayName()}";

            const float rowTextPadding = 2f;
            const float leftPadding = 4f;
            const float rightPadding = 4f;
            const float segmentSpacing = 8f;

            var nodeId = new DText(nodeIdText, boxTextColor)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = false,
                Pickable = false
            };

            var speaker = new DText(speakerText, boxTextColor)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = false,
                Pickable = true
            };
            speaker.Click += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                e.Handled = true;
                ShowNodeParticipantSelectorMenu(true);
            };

            var listener = new DText(listenerText, boxTextColor)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = false,
                Pickable = true
            };
            listener.Click += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                e.Handled = true;
                ShowNodeParticipantSelectorMenu(false);
            };

            float rowHeight = MathF.Max(nodeId.Height, MathF.Max(speaker.Height, listener.Height)) + (rowTextPadding * 2);

            float x = leftPadding;
            nodeId.X = x;
            nodeId.Y = rowTextPadding;
            x += nodeId.Width + segmentSpacing;
            float divider1X = x - (segmentSpacing / 2);

            speaker.X = x;
            speaker.Y = rowTextPadding;
            x += speaker.Width + segmentSpacing;
            float divider2X = x - (segmentSpacing / 2);

            listener.X = x;
            listener.Y = rowTextPadding;
            x += listener.Width + rightPadding;

            float titleWidth = MathF.Max(minimumWidth, x);
            float titleHeight = rowHeight;

            titleBox = PPath.CreateRectangle(0, 0, titleWidth, titleHeight);
            titleBox.Pen = new Pen(entryPenColor);
            if (NodeUID < 1000)
            {
                titleBox.Brush = new SolidBrush(entryColor);
            }
            else if (NodeUID < 2000)
            {
                titleBox.Brush = new SolidBrush(replyColor);
            }
            else
            {
                titleBox.Brush = CreateTitleBoxBrush();
            }

            var divider1 = PPath.CreateLine(divider1X, 2, divider1X, titleHeight - 2);
            divider1.Pen = new Pen(Color.FromArgb(150, boxTextColor));
            divider1.Pickable = false;

            var divider2 = PPath.CreateLine(divider2X, 2, divider2X, titleHeight - 2);
            divider2.Pen = new Pen(Color.FromArgb(150, boxTextColor));
            divider2.Pickable = false;

            titleBox.AddChild(nodeId);
            titleBox.AddChild(speaker);
            titleBox.AddChild(listener);
            titleBox.AddChild(divider1);
            titleBox.AddChild(divider2);

            titleBox.Pickable = false;
            return titleWidth;
        }

        private PNode CreateLineStringRefEditor(float width, float y)
        {
            float editorX = 4;
            float editorWidth = MathF.Max(width - 8, 80);

            var text = new DText(Node.LineStrRef.ToString(), boxTextColor)
            {
                X = editorX + 4,
                Pickable = false,
                ConstrainWidthToTextWidth = false,
                Width = editorWidth - 8
            };

            float editorHeight = MathF.Max(18, text.Height + 4);
            lineStrRefEditorBounds = new RectangleF(editorX, y, editorWidth, editorHeight);
            text.Y = y + ((editorHeight - text.Height) / 2);

            var editorBox = PPath.CreateRectangle(editorX, y, editorWidth, editorHeight);
            editorBox.Brush = CreateNodeBrush();
            editorBox.Pen = new Pen(Color.FromArgb(120, boxTextColor));

            editorBox.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left)
                {
                    return;
                }

                e.Handled = true;
                PointF relativeToNode = e.GetPositionRelativeTo(this);
                var clickOffsetInEditor = new PointF(relativeToNode.X - editorX, relativeToNode.Y - y);
                clickOffsetInEditor.X = MathF.Max(0, MathF.Min(editorWidth, clickOffsetInEditor.X));
                clickOffsetInEditor.Y = MathF.Max(0, MathF.Min(editorHeight, clickOffsetInEditor.Y));
                Editor.BeginInlineLineStrRefEdit(this, Cursor.Position, editorWidth, editorHeight, clickOffsetInEditor);
            };

            var container = new PNode();
            container.AddChild(editorBox);
            container.AddChild(text);
            container.Pickable = true;
            return container;
        }

        private PNode CreateMatineeSection(float y, float width, out float sectionHeight)
        {
            var container = new PNode();
            float innerY = y;

            var divider = PPath.CreateLine(4, innerY, width - 4, innerY);
            divider.Pen = new Pen(Color.FromArgb(120, boxTextColor));
            divider.Pickable = false;
            container.AddChild(divider);
            innerY += 3;

            string arrow = Node.MatineeExpanded ? "\u25BE" : "\u25B8";
            var arrowText = new DText(arrow, PlotHeaderColor, false)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = true,
                X = 6,
                Y = innerY,
                Pickable = false
            };
            var headerLabel = new DText("Matinee", PlotHeaderColor, false)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = false,
                X = arrowText.X + arrowText.Width + 8,
                Y = innerY,
                Pickable = false,
                Width = width - (arrowText.X + arrowText.Width + 14)
            };

            var headerHitBox = PPath.CreateRectangle(arrowText.X - 2, innerY, arrowText.Width + 6, arrowText.Height);
            headerHitBox.Brush = mostlyTransparentBrush;
            headerHitBox.Pen = null;
            headerHitBox.Pickable = true;
            headerHitBox.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                e.Handled = true;
                Node.MatineeExpanded = !Node.MatineeExpanded;
                SavePlotSectionState();
                RefreshPlotSectionsInPlace();
                g?.Refresh();
            };

            container.AddChild(arrowText);
            container.AddChild(headerLabel);
            container.AddChild(headerHitBox);
            innerY += Math.Max(arrowText.Height, headerLabel.Height);

            Node.ExportID = Node.NodeProp.GetProp<IntProperty>("nExportID")?.Value ?? 0;
            Node.CameraIntimacy = Node.NodeProp.GetProp<IntProperty>("nCameraIntimacy")?.Value ?? 0;
            Node.InterpLength = Node.InterpData?.GetProperty<FloatProperty>("InterpLength")?.Value ?? 0f;

            if (Node.MatineeExpanded)
            {
                container.AddChild(CreatePlotFieldEditor(
                    "Interp Length:",
                    Node.InterpData != null ? Node.InterpLength.ToString("0.###", CultureInfo.InvariantCulture) : "No data",
                    "InterpLength",
                    ref innerY,
                    width,
                    false,
                    isEditable: Node.InterpData != null,
                    isFloatField: true));

                container.AddChild(CreatePlotFieldEditor(
                    "Export ID:",
                    Node.ExportID.ToString(),
                    "ExportID",
                    ref innerY,
                    width,
                    false,
                    isEditable: false));

                container.AddChild(CreatePlotFieldEditor(
                    "Cam Intimacy:",
                    Node.CameraIntimacy.ToString(),
                    "CameraIntimacy",
                    ref innerY,
                    width,
                    false));
            }

            innerY += 3;
            sectionHeight = innerY - y;
            container.Pickable = false;
            return container;
        }

        public Rectangle GetLineStrRefEditorViewBounds()
        {
            if (g?.Camera == null || box == null)
            {
                return Rectangle.Empty;
            }

            RectangleF globalBounds = box.LocalToGlobal(lineStrRefEditorBounds);
            RectangleF screenBounds = g.Camera.ViewToLocal(globalBounds);
            return Rectangle.Round(screenBounds);
        }

        public void SyncInlineLineStrRefEditorPosition()
        {
            Editor?.UpdateInlineLineStrRefEditorPosition(this);
        }

        private float GetLineStringRefEditorHeight(float width)
        {
            float editorWidth = MathF.Max(width - 8, 80);
            var text = new DText(Node.LineStrRef.ToString(), boxTextColor)
            {
                ConstrainWidthToTextWidth = false,
                Width = editorWidth - 8,
                Pickable = false
            };

            return MathF.Max(18, text.Height + 4);
        }

        private static readonly Color PlotHeaderColor = Color.FromArgb(218, 165, 32);
        private static readonly Color PlotLabelColor = Color.FromArgb(180, 180, 180);

        private static string GetLastPathSection(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return "No data";
            }

            int lastDot = fullPath.LastIndexOf('.');
            return lastDot >= 0 && lastDot < fullPath.Length - 1
                ? fullPath[(lastDot + 1)..]
                : fullPath;
        }

        private PNode CreatePlotChecksSection(float y, float width, out float sectionHeight)
        {
            var container = new PNode();
            float innerY = y;

            // Divider line
            var divider = PPath.CreateLine(4, innerY, width - 4, innerY);
            divider.Pen = new Pen(Color.FromArgb(120, boxTextColor));
            divider.Pickable = false;
            container.AddChild(divider);
            innerY += 3;

            // Only the arrow toggles collapse so the rest of the header can drag the node.
            string arrow = Node.PlotChecksExpanded ? "\u25BE" : "\u25B8";
            var arrowText = new DText(arrow, PlotHeaderColor, false)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = true,
                X = 6,
                Y = innerY,
                Pickable = false
            };
            var headerLabel = new DText("Plot checks", PlotHeaderColor, false)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = false,
                X = arrowText.X + arrowText.Width + 8,
                Y = innerY,
                Pickable = false,
                Width = width - (arrowText.X + arrowText.Width + 14)
            };

            var headerHitBox = PPath.CreateRectangle(arrowText.X - 2, innerY, arrowText.Width + 6, arrowText.Height);
            headerHitBox.Brush = mostlyTransparentBrush;
            headerHitBox.Pen = null;
            headerHitBox.Pickable = true;
            headerHitBox.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                e.Handled = true;
                Node.PlotChecksExpanded = !Node.PlotChecksExpanded;
                SavePlotSectionState();
                RefreshPlotSectionsInPlace();
                g?.Refresh();
            };

            container.AddChild(arrowText);
            container.AddChild(headerLabel);
            container.AddChild(headerHitBox);
            innerY += Math.Max(arrowText.Height, headerLabel.Height);

            // Always-visible summary in header area for quick glance
            if (string.IsNullOrEmpty(Node.ConditionalPlotPath) && Node.ConditionalOrBool >= 0 && pcc != null)
            {
                Node.ConditionalPlotPath = Node.FiresConditional
                    ? PlotDatabases.FindPlotConditionalByID(Node.ConditionalOrBool, pcc.Game)?.Path
                    : PlotDatabases.FindPlotBoolByID(Node.ConditionalOrBool, pcc.Game)?.Path;
            }

            if (Node.ConditionalOrBool >= 0)
            {
                string condKind = Node.FiresConditional ? "Cnd" : "Bool";
                string condSummary = $"{condKind}:{Node.ConditionalOrBool} ({Node.ConditionalParam}) {GetLastPathSection(Node.ConditionalPlotPath)}";
                var condSummaryText = new DText(condSummary, PlotLabelColor, false)
                {
                    TextAlignment = StringAlignment.Near,
                    ConstrainWidthToTextWidth = false,
                    ConstrainHeightToTextHeight = true,
                    X = 6,
                    Y = innerY,
                    Pickable = false,
                    Width = width - 12
                };
                container.AddChild(condSummaryText);

                if (!string.IsNullOrEmpty(Node.ConditionalPlotPath))
                {
                    var summaryOverlay = PPath.CreateRectangle(6, condSummaryText.Y, width - 12, condSummaryText.Height);
                    summaryOverlay.Brush = mostlyTransparentBrush;
                    summaryOverlay.Pen = null;
                    summaryOverlay.Pickable = true;
                    string fullPath = Node.ConditionalPlotPath;
                    summaryOverlay.MouseEnter += (_, _) => g?.ShowPlotTooltip(fullPath, Cursor.Position);
                    summaryOverlay.MouseLeave += (_, _) => g?.HidePlotTooltip();
                    container.AddChild(summaryOverlay);
                }

                innerY += condSummaryText.Height;
            }

            if (Node.PlotChecksExpanded)
            {
                // Conditional/Bool type — inline editable dropdown
                string typeLabel = Node.FiresConditional ? "Conditional" : "Bool";
                container.AddChild(CreatePlotFieldEditor("Cnd/Bool:", typeLabel,
                    "FiresConditional", ref innerY, width, true));

                // Conditional ID — inline editable box
                string cndLabel = Node.FiresConditional ? "Conditional" : "Bool";
                container.AddChild(CreatePlotFieldEditor($"{cndLabel}:", Node.ConditionalOrBool.ToString(),
                    "ConditionalOrBool", ref innerY, width, true));

                // Conditional Parameter — inline editable box
                container.AddChild(CreatePlotFieldEditor("Cnd Param:", Node.ConditionalParam.ToString(),
                    "ConditionalParam", ref innerY, width, true));

                // Plot path (always shown, resolved live from plot database)
                {
                    // Resolve now if not already cached
                    if (string.IsNullOrEmpty(Node.ConditionalPlotPath) && Node.ConditionalOrBool >= 0 && pcc != null)
                    {
                        Node.ConditionalPlotPath = Node.FiresConditional
                            ? PlotDatabases.FindPlotConditionalByID(Node.ConditionalOrBool, pcc.Game)?.Path
                            : PlotDatabases.FindPlotBoolByID(Node.ConditionalOrBool, pcc.Game)?.Path;
                    }

                    string displayPath = !string.IsNullOrEmpty(Node.ConditionalPlotPath)
                        ? Node.ConditionalPlotPath
                        : "No data";
                    var pathLine = new DText(displayPath, PlotLabelColor, false)
                    {
                        TextAlignment = StringAlignment.Near, ConstrainWidthToTextWidth = false,
                        ConstrainHeightToTextHeight = true,
                        X = 6, Y = innerY, Pickable = false, Width = width - 12
                    };
                    container.AddChild(pathLine);
                    innerY += pathLine.Height;

                    // Tooltip overlay (only when a real path exists)
                    if (!string.IsNullOrEmpty(Node.ConditionalPlotPath))
                    {
                        var overlay = PPath.CreateRectangle(6, pathLine.Y, width - 12, pathLine.Height);
                        overlay.Brush = mostlyTransparentBrush;
                        overlay.Pen = null;
                        overlay.Pickable = true;
                        overlay.MouseEnter += (_, _) => g?.ShowPlotTooltip(displayPath, Cursor.Position);
                        overlay.MouseLeave += (_, _) => g?.HidePlotTooltip();
                        overlay.MouseDown += (_, e) =>
                        {
                            if (e.Button != MouseButtons.Left) return;
                            e.Handled = true;
                            Editor?.OpenPlotToolFromGraph(this, false);
                        };
                        container.AddChild(overlay);
                    }
                }
            }

            innerY += 3;
            sectionHeight = innerY - y;
            container.Pickable = false;
            return container;
        }

        private PNode CreatePlotTransitionsSection(float y, float width, out float sectionHeight)
        {
            var container = new PNode();
            float innerY = y;

            // Divider line
            var divider = PPath.CreateLine(4, innerY, width - 4, innerY);
            divider.Pen = new Pen(Color.FromArgb(120, boxTextColor));
            divider.Pickable = false;
            container.AddChild(divider);
            innerY += 3;

            // Only the arrow toggles collapse so the rest of the header can drag the node.
            string arrow = Node.PlotTransitionsExpanded ? "\u25BE" : "\u25B8";
            var arrowText = new DText(arrow, PlotHeaderColor, false)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = true,
                X = 6,
                Y = innerY,
                Pickable = false
            };
            var headerLabel = new DText("Plot transitions", PlotHeaderColor, false)
            {
                TextAlignment = StringAlignment.Near,
                ConstrainWidthToTextWidth = false,
                X = arrowText.X + arrowText.Width + 8,
                Y = innerY,
                Pickable = false,
                Width = width - (arrowText.X + arrowText.Width + 14)
            };

            var headerHitBox = PPath.CreateRectangle(arrowText.X - 2, innerY, arrowText.Width + 6, arrowText.Height);
            headerHitBox.Brush = mostlyTransparentBrush;
            headerHitBox.Pen = null;
            headerHitBox.Pickable = true;
            headerHitBox.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                e.Handled = true;
                Node.PlotTransitionsExpanded = !Node.PlotTransitionsExpanded;
                SavePlotSectionState();
                RefreshPlotSectionsInPlace();
                g?.Refresh();
            };

            container.AddChild(arrowText);
            container.AddChild(headerLabel);
            container.AddChild(headerHitBox);
            innerY += Math.Max(arrowText.Height, headerLabel.Height);

            // Always-visible summary in header area for quick glance
            if (string.IsNullOrEmpty(Node.TransitionPlotPath) && Node.Transition >= 0 && pcc != null)
            {
                Node.TransitionPlotPath = PlotDatabases.FindPlotTransitionByID(Node.Transition, pcc.Game)?.Path;
            }

            if (Node.Transition >= 0)
            {
                string transSummary = $"Trans:{Node.Transition} ({Node.TransitionParam}) {GetLastPathSection(Node.TransitionPlotPath)}";
                var transSummaryText = new DText(transSummary, PlotLabelColor, false)
                {
                    TextAlignment = StringAlignment.Near,
                    ConstrainWidthToTextWidth = false,
                    ConstrainHeightToTextHeight = true,
                    X = 6,
                    Y = innerY,
                    Pickable = false,
                    Width = width - 12
                };
                container.AddChild(transSummaryText);

                if (!string.IsNullOrEmpty(Node.TransitionPlotPath))
                {
                    var summaryOverlay = PPath.CreateRectangle(6, transSummaryText.Y, width - 12, transSummaryText.Height);
                    summaryOverlay.Brush = mostlyTransparentBrush;
                    summaryOverlay.Pen = null;
                    summaryOverlay.Pickable = true;
                    string fullPath = Node.TransitionPlotPath;
                    summaryOverlay.MouseEnter += (_, _) => g?.ShowPlotTooltip(fullPath, Cursor.Position);
                    summaryOverlay.MouseLeave += (_, _) => g?.HidePlotTooltip();
                    container.AddChild(summaryOverlay);
                }

                innerY += transSummaryText.Height;
            }

            if (Node.PlotTransitionsExpanded)
            {
                // Transition ID — inline editable box
                container.AddChild(CreatePlotFieldEditor("Transition:", Node.Transition.ToString(),
                    "Transition", ref innerY, width, false));

                // Transition Parameter — inline editable box
                container.AddChild(CreatePlotFieldEditor("Trans Param:", Node.TransitionParam.ToString(),
                    "TransitionParam", ref innerY, width, false));

                // Plot path (always shown, resolved live from plot database)
                {
                    // Resolve now if not already cached
                    if (string.IsNullOrEmpty(Node.TransitionPlotPath) && Node.Transition >= 0 && pcc != null)
                    {
                        Node.TransitionPlotPath = PlotDatabases.FindPlotTransitionByID(Node.Transition, pcc.Game)?.Path;
                    }

                    string displayPath = !string.IsNullOrEmpty(Node.TransitionPlotPath)
                        ? Node.TransitionPlotPath
                        : "No data";
                    var pathLine = new DText(displayPath, PlotLabelColor, false)
                    {
                        TextAlignment = StringAlignment.Near, ConstrainWidthToTextWidth = false,
                        ConstrainHeightToTextHeight = true,
                        X = 6, Y = innerY, Pickable = false, Width = width - 12
                    };
                    container.AddChild(pathLine);
                    innerY += pathLine.Height;

                    // Tooltip overlay (only when a real path exists)
                    if (!string.IsNullOrEmpty(Node.TransitionPlotPath))
                    {
                        var overlay = PPath.CreateRectangle(6, pathLine.Y, width - 12, pathLine.Height);
                        overlay.Brush = mostlyTransparentBrush;
                        overlay.Pen = null;
                        overlay.Pickable = true;
                        overlay.MouseEnter += (_, _) => g?.ShowPlotTooltip(displayPath, Cursor.Position);
                        overlay.MouseLeave += (_, _) => g?.HidePlotTooltip();
                        overlay.MouseDown += (_, e) =>
                        {
                            if (e.Button != MouseButtons.Left) return;
                            e.Handled = true;
                            Editor?.OpenPlotToolFromGraph(this, true);
                        };
                        container.AddChild(overlay);
                    }
                }
            }

            innerY += 3;
            sectionHeight = innerY - y;
            container.Pickable = false;
            return container;
        }

        /// <summary>
        /// Creates an inline editable field box (like the LineStrRef editor) for a plot field.
        /// The box renders in the Piccolo graph; clicking it spawns a WinForms TextBox overlay at the exact position.
        /// </summary>
        private PNode CreatePlotFieldEditor(string label, string value, string fieldTag, ref float y, float width, bool isConditionalSection, bool isEditable = true, bool isFloatField = false)
        {
            float editorX = 4;
            float editorWidth = MathF.Max(width - 8, 80);

            var labelText = new DText($"{label} ", PlotLabelColor, false)
            {
                X = editorX + 2, Pickable = false,
                ConstrainWidthToTextWidth = true
            };
            float labelWidth = labelText.Width;

            var valueText = new DText(value, boxTextColor, false)
            {
                X = editorX + labelWidth + 4, Pickable = false,
                ConstrainWidthToTextWidth = false,
                Width = editorWidth - labelWidth - 8
            };

            float editorHeight = MathF.Max(18, MathF.Max(labelText.Height, valueText.Height) + 4);
            var bounds = new RectangleF(editorX, y, editorWidth, editorHeight);

            // Track this field's bounds for inline editing
            PlotFieldEditorInfo fieldInfo = null;
            if (isEditable)
            {
                fieldInfo = new PlotFieldEditorInfo { FieldTag = fieldTag, Bounds = bounds, IsConditionalSection = isConditionalSection, IsFloat = isFloatField };
                plotFieldEditors.Add(fieldInfo);
            }

            labelText.Y = y + ((editorHeight - labelText.Height) / 2);
            valueText.Y = y + ((editorHeight - valueText.Height) / 2);

            var editorBox = PPath.CreateRectangle(editorX, y, editorWidth, editorHeight);
            editorBox.Brush = CreateNodeBrush();
            editorBox.Pen = new Pen(Color.FromArgb(80, boxTextColor));

            if (fieldTag == "FiresConditional")
            {
                editorBox.DoubleClick += (_, e) =>
                {
                    if (!isEditable || e.Button != MouseButtons.Left)
                    {
                        return;
                    }

                    e.Handled = true;
                    Editor?.ToggleNodeFiresConditionalFromGraph(Node);
                };
            }
            else
            {
                // On click, spawn a TextBox overlay at this exact position (like LineStrRef editor)
                editorBox.MouseDown += (_, e) =>
                {
                    if (!isEditable)
                    {
                        return;
                    }
                    if (e.Button != MouseButtons.Left) return;
                    e.Handled = true;
                    Editor?.BeginInlinePlotFieldEdit(this, fieldInfo);
                };
            }

            var container = new PNode();
            container.AddChild(editorBox);
            container.AddChild(labelText);
            container.AddChild(valueText);
            container.Pickable = true;

            y += editorHeight;
            return container;
        }

        /// <summary>Gets the screen-space bounds for a plot field editor, for positioning a WinForms TextBox overlay.</summary>
        public Rectangle GetPlotFieldEditorViewBounds(PlotFieldEditorInfo fieldInfo)
        {
            if (g?.Camera == null || box == null) return Rectangle.Empty;
            RectangleF globalBounds = box.LocalToGlobal(fieldInfo.Bounds);
            RectangleF screenBounds = g.Camera.ViewToLocal(globalBounds);
            return Rectangle.Round(screenBounds);
        }

        /// <summary>Syncs the position of any active inline plot field editor when the node is dragged.</summary>
        public void SyncInlinePlotFieldEditorPosition()
        {
            Editor?.UpdateInlinePlotFieldEditorPosition(this);
        }

        public void RefreshPlotSectionsInPlace()
        {
            if (box == null || titleBox == null)
            {
                return;
            }

            plotFieldEditors.Clear();

            if (plotChecksSection != null)
            {
                box.RemoveChild(plotChecksSection);
                plotChecksSection = null;
            }

            if (plotTransitionsSection != null)
            {
                box.RemoveChild(plotTransitionsSection);
                plotTransitionsSection = null;
            }

            if (matineeSection != null)
            {
                box.RemoveChild(matineeSection);
                matineeSection = null;
            }

            float nextSectionY = plotSectionsStartY;
            plotChecksSection = CreatePlotChecksSection(nextSectionY, nodeBoxWidth, out float checksSectionHeight);
            nextSectionY += checksSectionHeight;

            plotTransitionsSection = CreatePlotTransitionsSection(nextSectionY, nodeBoxWidth, out float transitionsSectionHeight);
            nextSectionY += transitionsSectionHeight;

            matineeSection = CreateMatineeSection(nextSectionY, nodeBoxWidth, out float matineeSectionHeight);
            nextSectionY += matineeSectionHeight;

            box.AddChild(plotChecksSection);
            box.AddChild(plotTransitionsSection);
            box.AddChild(matineeSection);

            float newBoxHeight = baseBoxHeightWithoutPlotSections + checksSectionHeight + transitionsSectionHeight + matineeSectionHeight;
            box.Bounds = new RectangleF(0, titleBox.Height + 2, nodeBoxWidth, newBoxHeight);
            Bounds = new RectangleF(0, 0, nodeBoxWidth, titleBox.Height + 2 + newBoxHeight);

            MoveToFront();
            InvalidateFullBounds();
            InvalidatePaint();
        }

        private bool _isSelected;
        public override bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                ApplyAccentVisualState();
            }
        }

        public override void Layout(float x, float y)
        {
            plotFieldEditors.Clear();
            if (NodeUID < 1000)
            {
                outlinePen = new Pen(entryPenColor);
            }
            else if (NodeUID < 2000)
            {
                outlinePen = new Pen(replyPenColor);
            }
            else
            {
                outlinePen = new Pen(Color.Black);
            }
            float starty = 8;
            float w = 160;

            //OutputLinks
            outLinkBox = new PPath();
            float outW = 0;
            for (int i = 0; i < Outlinks.Count; i++)
            {
                string outLinkText = $"{Outlinks[i].Desc} ({GetReplyCategoryAcronym(Outlinks[i].RCat)})";
                if (!string.IsNullOrWhiteSpace(Outlinks[i].Detail))
                {
                    outLinkText = $"{outLinkText} {Outlinks[i].Detail}";
                }

                Color outLinkTextColor = Outlinks[i].RCat == EReplyCategory.REPLY_CATEGORY_DEFAULT
                    ? boxTextColor
                    : getColor(Outlinks[i].RCat);
                DText t2 = new DText(outLinkText, outLinkTextColor);
                if (i < Links.Count)
                {
                    var doubleClickHandler = CreateOutgoingLinkDoubleClickHandler(i);
                    t2.AddInputEventListener(doubleClickHandler);
                    if (Outlinks[i].node.Count() > 0)
                    {
                        Outlinks[i].node[0].AddInputEventListener(doubleClickHandler);
                    }
                    t2.Pickable = true;
                }
                if (!string.IsNullOrWhiteSpace(Outlinks[i].Detail))
                {
                    t2.ConstrainWidthToTextWidth = false;
                    t2.ConstrainHeightToTextHeight = true;
                    t2.Width = 180;
                }
                if (t2.Width + 10 > outW) outW = t2.Width + 10;
                t2.X = 0 - t2.Width;
                t2.Y = starty;
                starty += t2.Height;
                if (!t2.Pickable)
                {
                    t2.Pickable = false;
                }
                Outlinks[i].node.TranslateBy(0, t2.Y + t2.Height / 2);
                t2.AddChild(Outlinks[i].node);
                outLinkBox.AddChild(t2);

                if (i < Outlinks.Count - 1)
                {
                    float dividerY = t2.Y + t2.Height + 1;
                    PPath divider = PPath.CreateLine(-t2.Width, dividerY, 0, dividerY);
                    divider.Pen = new Pen(Color.FromArgb(120, boxTextColor));
                    divider.Pickable = false;
                    outLinkBox.AddChild(divider);
                    starty += 3;
                }
            }
            outLinkBox.Pickable = false;
            float outY = starty;

            //InputLinks
            inputLinkBox = new PNode();
            GetInputLinks(Node);
            float inW = 0;
            float inY = 8;
            for (int i = 0; i < InLinks.Count; i++)
            {
                DText t2 = new DText(InLinks[i].Desc);
                if (t2.Width > inW) inW = t2.Width;
                t2.X = 3;
                t2.Y = inY;
                inY += t2.Height;
                t2.Pickable = false;
                InLinks[i].node.X = -10;
                InLinks[i].node.Y = t2.Y + t2.Height / 2 - 5;
                t2.AddChild(InLinks[i].node);
                inputLinkBox.AddChild(t2);
            }
            inputLinkBox.Pickable = false;
            if (inY > outY) starty = inY;
            if (inW + outW + 10 > w) w = inW + outW + 10;

            //TitleBox
            float tW = BuildSpeakerListenerTitleBox(w);
            if (tW > w)
            {
                w = tW;
                titleBox.Width = w;
            }
            titleBox.X = 0;
            titleBox.Y = 0;
            float bodyTopY = titleBox.Height + 2;
            float h = bodyTopY;

            //Inside Text +  Box
            string type = "";
            if (Node.IsReply)
            {
                string t = Node.ReplyType.ToString().Substring(6);
                type = $"{t}";
            }
            string spokenLine = string.IsNullOrWhiteSpace(Node.Line) ? string.Empty : $"{Node.Line}\r\n";
            string d = $"{spokenLine}{type}";

            DText insidetext = new DText(d, boxTextColor, true)
            {
                TextAlignment = StringAlignment.Center,
                ConstrainWidthToTextWidth = false,
                ConstrainHeightToTextHeight = true,
                X = 0,
                Y = bodyTopY + 5,
                Pickable = false
            };
            insidetext.Width = MathF.Max(w - 12, 140);
            h += insidetext.Height;
            float iw = insidetext.Width;
            if (iw > w) { w = iw; }

            // String ref editor (placed before plot sections)
            float nextSectionY = insidetext.Y + insidetext.Height;
            float lineStrRefEditorHeight = GetLineStringRefEditorHeight(w);
            h += lineStrRefEditorHeight;
            float lineStrRefY = nextSectionY + 3;
            nextSectionY += lineStrRefEditorHeight + 3;

            bool hasTlkSection = !string.IsNullOrWhiteSpace(Node.Line) || Node.LineStrRef >= 0;
            float connectionSectionY = hasTlkSection ? nextSectionY + 5 : bodyTopY;
            inputLinkBox.TranslateBy(0, connectionSectionY);
            float connectionsBottomY = connectionSectionY + starty + 8;
            h = connectionsBottomY;

            // Plot conditional/bool and transition sections
            plotSectionsStartY = connectionsBottomY;
            baseBoxHeightWithoutPlotSections = connectionsBottomY;

            // Always show plot checks section (matches Plot Control tab)
            plotChecksSection = CreatePlotChecksSection(connectionsBottomY, w, out float checksSectionHeight);
            h += checksSectionHeight;
            nextSectionY = connectionsBottomY + checksSectionHeight;

            // Always show plot transitions section (matches Plot Control tab)
            plotTransitionsSection = CreatePlotTransitionsSection(nextSectionY, w, out float transitionsSectionHeight);
            h += transitionsSectionHeight;
            nextSectionY += transitionsSectionHeight;

            // Matinee data section
            matineeSection = CreateMatineeSection(nextSectionY, w, out float matineeSectionHeight);
            h += matineeSectionHeight;
            nextSectionY += matineeSectionHeight;
            nodeBoxWidth = w;

            outLinkBox.TranslateBy(w, connectionSectionY);
            box = PPath.CreateRectangle(0, titleBox.Height + 2, w, h - (titleBox.Height + 2));
            box.Brush = CreateNodeBrush();
            box.Pen = outlinePen;
            box.Pickable = false;

            if (hasTlkSection)
            {
                float dividerY = connectionSectionY;
                PPath tlkDivider = PPath.CreateLine(4, dividerY, w - 4, dividerY);
                tlkDivider.Pen = new Pen(Color.FromArgb(120, boxTextColor));
                tlkDivider.Pickable = false;
                box.AddChild(tlkDivider);
            }

            insidetext.TranslateBy((w - iw) / 2, 0);
            box.AddChild(insidetext);
            box.AddChild(CreateLineStringRefEditor(w, lineStrRefY));
            box.AddChild(plotChecksSection);
            box.AddChild(plotTransitionsSection);
            box.AddChild(matineeSection);
            Bounds = new RectangleF(0, 0, w, h);
            AddChild(box);
            AddChild(titleBox);
            AddChild(outLinkBox);
            AddChild(inputLinkBox);
            SetOffset(x, y);
            ApplyAccentVisualState();
        }
        public virtual void GetOutputLinks(DialogueNodeExtended node) { }
        public void GetInputLinks(DialogueNodeExtended node = null)
        {
            InLinks = new List<InputLink>();

            void CreateInputLink(string desc, int idx, bool hasName = true)
            {
                InputLink l = new InputLink
                {
                    Desc = desc,
                    hasName = hasName,
                    index = idx,
                    node = CreateActionLinkBox(),
                    Edges = new List<DiagEdEdge>()
                };
                l.node.Brush = new SolidBrush(connectionColor);
                l.node.Pen = new Pen(connectionColor);
                l.node.MouseEnter += OnMouseEnter;
                l.node.MouseLeave += OnMouseLeave;
                l.node.AddInputEventListener(inputDragHandler);
                InLinks.Add(l);
            }

            if (node != null && !node.IsReply)
            {
                CreateInputLink("Start", 0, true);
            }
            CreateInputLink("In", 1, true);

            if (InputEdges.Any())
            {
                int numInputs = InLinks.Count;
                foreach (DiagEdEdge edge in InputEdges)
                {
                    int inputNum = edge.inputIndex;
                    //if there are inputs with an index greater than is accounted for by
                    //the current number of inputs, create enough inputs to fill up to that index
                    //With current toolset advances this is unlikely to occur, but no harm in leaving it in
                    if (inputNum + 1 > numInputs)
                    {
                        for (int i = numInputs; i <= inputNum; i++)
                        {
                            CreateInputLink($":{i}", i, false);
                        }
                        numInputs = inputNum + 1;
                    }
                    //change the end of the edge to the input box, not the DiagNode
                    if (inputNum >= 0)
                    {
                        edge.end = InLinks[inputNum].node;
                        InLinks[inputNum].Edges.Add(edge);
                    }
                }
            }
        }
        public void RefreshInputLinks()
        {
            if (InputEdges.Any() && InLinks != null)
            {
                foreach (DiagEdEdge edge in InputEdges)
                {
                    int inputNum = edge.inputIndex;
                    if (inputNum >= 0)
                    {
                        edge.end = InLinks[inputNum].node;
                        InLinks[inputNum].Edges.Add(edge);
                    }
                }
            }
        }

        public class InputDragHandler : PDragEventHandler
        {
            public override bool DoesAcceptEvent(PInputEventArgs e)
            {
                return e.IsMouseEvent && (e.Button != MouseButtons.None || e.IsMouseEnterOrMouseLeave) && !e.Handled;
            }

            protected override void OnStartDrag(object sender, PInputEventArgs e)
            {
                e.Handled = true;
            }

            protected override void OnDrag(object sender, PInputEventArgs e)
            {
                e.Handled = true;
            }

            protected override void OnEndDrag(object sender, PInputEventArgs e)
            {
                e.Handled = true;
            }
        }

        public void OnMouseEnter(object sender, PInputEventArgs e)
        {
            if (draggingOutlink)
            {
                ((PPath)sender).Pen = selectedPen;
                dragTarget = (PPath)sender;
            }
        }

        public void OnMouseLeave(object sender, PInputEventArgs e)
        {
            ((PPath)sender).Pen = outlinePen;
            dragTarget = null;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (inputDragHandler != null)
            {
                InLinks.ForEach(x => x.node.RemoveInputEventListener(inputDragHandler));
            }
        }

        public abstract void CreateLink(DiagNode fromNode, DiagNode toNode);
    }

    public sealed class DiagNodeEntry : DiagNode
    {
        public DiagNodeEntry(DialogueEditorWindow editor, DialogueNodeExtended node, float x, float y, ConvGraphEditor ConvGraphEditor)
            : base(editor, node, x, y, ConvGraphEditor)
        {
            Node = node;
            NodeProp = node.NodeProp;
            NodeID = node.NodeCount;
            NodeUID = NodeID;
            originalX = x;
            originalY = y;
            listname = $"E{NodeID} {node.Line}";

            GetOutputLinks(Node);
        }

        private bool _isSelected;
        public override bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                ApplyAccentVisualState();
            }
        }
        public override void GetOutputLinks(DialogueNodeExtended node)
        {
            if (node != null)
            {
                Links.Clear();
                Outlinks.Clear();
                var rcarray = NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");
                if (rcarray != null)
                {
                    try
                    {
                        foreach (var rc in rcarray)
                        {
                            var replychoice = new ReplyChoiceNode(-1, "", -1, EReplyCategory.REPLY_CATEGORY_DEFAULT, "No data")
                            {
                                Order = Links.Count
                            };
                            var nIDprop = rc.GetProp<IntProperty>("nIndex");
                            if (nIDprop != null)
                            {
                                replychoice.Index = nIDprop.Value;
                            }

                            var strRefPara = rc.GetProp<StringRefProperty>("srParaphrase");
                            if (strRefPara != null)
                            {
                                replychoice.ReplyStrRef = strRefPara.Value;
                                replychoice.ReplyLine = GlobalFindStrRefbyID(replychoice.ReplyStrRef, pcc);
                            }

                            var rcatprop = rc.GetProp<EnumProperty>("Category");
                            if (rcatprop != null)
                            {
                                Enum.TryParse(rcatprop.Value.Name, out EReplyCategory eReply);
                                replychoice.RCategory = eReply;
                            }
                            Links.Add(replychoice);
                        }
                    }
                    catch
                    {
                        //ignore
                    }
                }
                if (Links.Count > 0)
                {
                    int n = 0;
                    foreach (var reply in Links)
                    {
                        OutputLink l = new OutputLink
                        {
                            Links = new List<int>(),
                            InputIndices = new int(),
                            Edges = new List<DiagEdEdge>(),
                            Desc = n.ToString(),
                            Detail = null,
                            RCat = reply.RCategory
                        };

                        int linkedOp = reply.Index + 1000;
                        l.Links.Add(linkedOp);
                        l.InputIndices = 0;

                        l.Desc = "R" + reply.Index;
                            if (!OutputNumbers)
                            {
                                l.Detail = reply.ReplyLine;
                            }
                        l.node = CreateActionLinkBox();
                        var linkcolor = getColor(reply.RCategory);
                        l.node.Brush = new SolidBrush(linkcolor);
                        l.node.Pen = new Pen(getColor(reply.RCategory));
                        l.node.Pickable = false;

                        PPath dragger = CreateActionLinkBox();
                        dragger.Brush = mostlyTransparentBrush;
                        dragger.Pen = new Pen(getColor(reply.RCategory));
                        dragger.X = l.node.X;
                        dragger.Y = l.node.Y;
                        dragger.AddInputEventListener(outputDragHandler);
                        l.node.AddChild(dragger);
                        Outlinks.Add(l);
                        n++;
                    }
                }
                else //Create default node.
                {
                    OutputLink l = new OutputLink
                    {
                        Links = new List<int>(),
                        InputIndices = new int(),
                        Edges = new List<DiagEdEdge>(),
                        Desc = "Out:",
                        RCat = EReplyCategory.REPLY_CATEGORY_DEFAULT,
                        node = CreateActionLinkBox()
                    };

                    l.node.Brush = new SolidBrush(connectionColor);
                    l.node.Pen = new Pen(connectionColor);
                    l.node.Pickable = false;
                    PPath dragger = CreateActionLinkBox();
                    dragger.Brush = mostlyTransparentBrush;
                    dragger.Pen = new Pen(connectionColor);
                    dragger.X = l.node.X;
                    dragger.Y = l.node.Y;
                    dragger.AddInputEventListener(outputDragHandler);
                    l.node.AddChild(dragger);
                    Outlinks.Add(l);
                }
            }
        }
        public override void CreateOutlink(PNode n1, PNode n2)
        {
            DiagNode start = FindAncestor<DiagNode>(n1);
            DiagNode end = FindAncestor<DiagNode>(n2);
            if (start == null || end == null)
            {
                return;
            }

            CreateLink(start, end);
        }
        public override void RemoveOutlink(int linkconnection, int linkIndex)
        {
            var oldEntriesProp = NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");
            oldEntriesProp.RemoveAt(linkconnection);
            NodeProp.Properties.AddOrReplaceProp(oldEntriesProp);
            //Editor.RecreateNodesToProperties(Editor.SelectedConv);
            Editor.PushLocalGraphChanges(this);
        }

        public override void CreateLink(DiagNode start, DiagNode end)
        {
            if (end.GetType() != typeof(DiagNodeReply))
            {
                MessageBox.Show("You cannot link entry nodes to entries.\r\nEntries must link to replies.", "Dialogue Editor");
                return;
            }
            var startNode = start.NodeID;
            var endNode = end.NodeID;

            var newReplyListProp = new ArrayProperty<StructProperty>("ReplyListNew");
            var oldReplyListProp = start.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");

            if (oldReplyListProp != null && oldReplyListProp.Count > 0)
            {
                foreach (var rprop in oldReplyListProp)
                {
                    newReplyListProp.Add(rprop);
                }
            }
            newReplyListProp.Add(new StructProperty("BioDialogReplyListDetails", new PropertyCollection
            {
                new IntProperty(endNode - 1000, "nIndex"),
                new StringRefProperty(663399, "srParaphrase"),
                new StrProperty("", "sParaphrase"),
                new EnumProperty("REPLY_CATEGORY_DEFAULT", "EReplyCategory", Editor.Pcc.Game, "Category"),
                new NoneProperty()
            }));

            Node.NodeProp.Properties.AddOrReplaceProp(newReplyListProp);
            Editor.PushLocalGraphChanges(this);
        }
    }

    public sealed class DiagNodeReply : DiagNode
    {
        public DiagNodeReply(DialogueEditorWindow editor, DialogueNodeExtended node, float x, float y, ConvGraphEditor ConvGraphEditor)
            : base(editor, node, x, y, ConvGraphEditor)
        {
            Editor = editor;
            Node = node;
            NodeProp = node.NodeProp;
            NodeID = Node.NodeCount + 1000;
            NodeUID = NodeID;
            listname = $"R{Node.NodeCount} {node.Line}";
            GetOutputLinks(Node);
            originalX = x;
            originalY = y;
        }
        private bool _isSelected;
        public override bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                ApplyAccentVisualState();
            }
        }
        public override void GetOutputLinks(DialogueNodeExtended node)
        {
            if (node != null)
            {
                Outlinks.Clear();
                Links.Clear();
                var replytoEntryList = node.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
                if (replytoEntryList != null)
                {
                    if (replytoEntryList.Count > 0)
                    {
                        int n = 0;
                        foreach (var prop in replytoEntryList)
                        {
                            OutputLink l = new OutputLink
                            {
                                Links = new List<int>(),
                                InputIndices = new int(),
                                Edges = new List<DiagEdEdge>(),
                                Desc = n.ToString(),
                                RCat = EReplyCategory.REPLY_CATEGORY_DEFAULT
                            };

                            int linkedOp = prop.Value;
                            l.Links.Add(linkedOp);
                            l.InputIndices = 1;

                            l.Desc = "E" + linkedOp;
                            l.node = CreateActionLinkBox();
                            l.node.Brush = new SolidBrush(connectionColor);
                            l.node.Pen = new Pen(connectionColor);
                            l.node.Pickable = false;
                            PPath dragger = CreateActionLinkBox();
                            dragger.Brush = mostlyTransparentBrush;
                            dragger.Pen = new Pen(connectionColor);
                            dragger.X = l.node.X;
                            dragger.Y = l.node.Y;
                            dragger.AddInputEventListener(outputDragHandler);
                            l.node.AddChild(dragger);
                            Outlinks.Add(l);
                            n++;

                            //Add to links package
                            var replychoice = new ReplyChoiceNode(linkedOp, "", -1, EReplyCategory.REPLY_CATEGORY_DEFAULT, "No data")
                            {
                                Order = n
                            };
                            Links.Add(replychoice);
                        }
                    }
                    else //Create default node.
                    {
                        OutputLink l = new OutputLink
                        {
                            Links = new List<int>(),
                            InputIndices = new int(),
                            Edges = new List<DiagEdEdge>(),
                            Desc = "Out:",
                            RCat = EReplyCategory.REPLY_CATEGORY_DEFAULT,
                            node = CreateActionLinkBox()
                        };

                        l.node.Brush = new SolidBrush(connectionColor);
                        l.node.Pen = new Pen(connectionColor);
                        l.node.Pickable = false;
                        PPath dragger = CreateActionLinkBox();
                        dragger.Brush = mostlyTransparentBrush;
                        dragger.Pen = new Pen(connectionColor);
                        dragger.X = l.node.X;
                        dragger.Y = l.node.Y;
                        dragger.AddInputEventListener(outputDragHandler);
                        l.node.AddChild(dragger);
                        Outlinks.Add(l);
                    }
                }
            }
        }
        public override void CreateOutlink(PNode n1, PNode n2)
        {
            DiagNode start = FindAncestor<DiagNode>(n1);
            DiagNode end = FindAncestor<DiagNode>(n2);
            if (start == null || end == null)
            {
                return;
            }

            CreateLink(start, end);
        }
        public override void RemoveOutlink(int linkconnection, int linkIndex)
        {
            var oldEntriesProp = NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
            oldEntriesProp.RemoveAt(linkconnection);
            NodeProp.Properties.AddOrReplaceProp(oldEntriesProp);
            Editor.PushLocalGraphChanges(this);
        }

        public override void CreateLink(DiagNode start, DiagNode end)
        {
            if (end.GetType() != typeof(DiagNodeEntry))
            {
                MessageBox.Show("You cannot link reply nodes to replies.\r\nReplies must link to entries.", "Dialogue Editor");
                return;
            }

            var startNode = start.NodeID;
            var endNode = end.NodeID;

            var newEntriesProp = new ArrayProperty<IntProperty>("EntryList");
            var oldEntriesProp = start.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
            if (oldEntriesProp != null)
            {
                foreach (var i in oldEntriesProp)
                {
                    newEntriesProp.Add(i);
                }
            }

            newEntriesProp.Add(new IntProperty(endNode));
            start.NodeProp.Properties.AddOrReplaceProp(newEntriesProp);  //Push to Property

            Editor.PushLocalGraphChanges(this);
        }
    }
    public class DText : PText
    {
        private readonly Brush black = new SolidBrush(Color.Black);
        public bool shadowRendering { get; set; }

        public DText(string s, bool shadows = true, float scale = 1)
            : base(s)
        {
            base.TextBrush = new SolidBrush(DObj.boxTextColor);
            base.GlobalScale = scale;
            shadowRendering = shadows;
        }

        public DText(string s, Color c, bool shadows = true, float scale = 1)
            : base(s)
        {
            base.TextBrush = new SolidBrush(c);
            base.GlobalScale = scale;
            shadowRendering = shadows;
        }

        protected override void Paint(PPaintContext paintContext)
        {
            paintContext.Graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixel;
            //paints dropshadow
            if (shadowRendering && paintContext.Scale >= 1 && base.Text != null && base.TextBrush != null && base.Font != null)
            {
                Graphics g = paintContext.Graphics;
                float renderedFontSize = FontSizeInPoints * paintContext.Scale;
                if (renderedFontSize >= PUtil.GreekThreshold && renderedFontSize < PUtil.MaxFontSize)
                {
                    RectangleF shadowbounds = Bounds;
                    shadowbounds.Offset(1, 1);
                    var stringformat = new StringFormat { Alignment = base.TextAlignment };
                    g.DrawString(base.Text, base.Font, black, shadowbounds, stringformat);
                }
            }
            base.Paint(paintContext);
        }
    }
}