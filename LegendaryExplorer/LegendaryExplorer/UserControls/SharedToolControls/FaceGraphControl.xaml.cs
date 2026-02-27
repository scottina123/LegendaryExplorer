using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.SharedToolControls;

/// <summary>
/// Displays the CompiledFaceGraph of a FaceFXAsset as a scrollable DAG with live FinalValue readouts.
/// Clicking a node highlights its connected edges.
/// </summary>
public partial class FaceGraphControl : NotifyPropertyChangedControlBase
{
    const double NODE_W = 140, NODE_H = 65, COL_GAP = 80, ROW_GAP = 15;

    private static readonly SolidColorBrush EdgeDefaultBrush    = new(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly SolidColorBrush EdgeCorrectiveBrush = new(Colors.IndianRed);
    private static readonly SolidColorBrush EdgeHighlightBrush  = new(Color.FromRgb(0xFF, 0xD7, 0x00)); // gold
    private static readonly SolidColorBrush SelectionBorderBrush = new(Colors.White);

    private readonly record struct EdgeInfo(Path PathElement, int SrcNode, int DstNode, bool IsCorrective);

    private FaceFXAsset _fxActor;
    private TextBlock[] _finalValueBlocks;
    private EdgeInfo[]  _edgeInfos;
    private Border[]    _nodeBorders;
    private Color[]     _nodeBorderColors; // base border color per node
    private int         _selectedNodeIndex = -1;

    public FaceGraphControl()
    {
        InitializeComponent();
        GraphCanvas.MouseLeftButtonDown += OnCanvasBackground_Click;
    }

    public void LoadFxActor(FaceFXAsset fxActor)
    {
        _fxActor = fxActor;
        _selectedNodeIndex = -1;
        BuildGraph();
    }

    public void RefreshValues()
    {
        if (_fxActor == null || _finalValueBlocks == null || !IsVisible) return;
        var nodes = _fxActor.CompiledFaceGraph;
        var blocks = _finalValueBlocks;
        for (int i = 0; i < Math.Min(blocks.Length, nodes.Count); i++)
            blocks[i].Text = nodes[i].FinalValue.ToString("F4");
    }

    private void BuildGraph()
    {
        GraphCanvas.Children.Clear();
        _finalValueBlocks = null;
        _edgeInfos = null;
        _nodeBorders = null;
        _nodeBorderColors = null;

        if (_fxActor == null)
        {
            InfoBar.Text = string.Empty;
            GraphCanvas.Width = 0;
            GraphCanvas.Height = 0;
            return;
        }

        var nodes = _fxActor.CompiledFaceGraph;
        int n = nodes.Count;
        if (n == 0)
        {
            InfoBar.Text = "No nodes in CompiledFaceGraph.";
            return;
        }

        // --- Topological layer assignment ---
        // The CompiledFaceGraph list is already in evaluation order: each node's
        // inputs are at lower indices, so a single forward pass is sufficient.
        int[] layer = new int[n];
        for (int i = 0; i < n; i++)
        {
            int maxInputLayer = -1;
            foreach (var link in nodes[i].InputLinks)
            {
                int idx = link.NodeIndex;
                if (idx >= 0 && idx < i)
                    maxInputLayer = Math.Max(maxInputLayer, layer[idx]);
            }
            layer[i] = maxInputLayer + 1;
        }

        int maxLayer = 0;
        foreach (int l in layer) maxLayer = Math.Max(maxLayer, l);

        int[] rowCount = new int[maxLayer + 1];
        int[] rowIndex = new int[n];
        for (int i = 0; i < n; i++)
            rowIndex[i] = rowCount[layer[i]]++;

        int maxNodesInLayer = 0;
        foreach (int rc in rowCount) maxNodesInLayer = Math.Max(maxNodesInLayer, rc);

        // --- Compute node positions ---
        double[] posX = new double[n];
        double[] posY = new double[n];
        double canvasH = maxNodesInLayer * (NODE_H + ROW_GAP) - ROW_GAP + NODE_H;

        for (int i = 0; i < n; i++)
        {
            int l = layer[i];
            int r = rowIndex[i];
            int layerSize = rowCount[l];

            posX[i] = l * (NODE_W + COL_GAP);
            double layerH = layerSize * (NODE_H + ROW_GAP) - ROW_GAP;
            double topOffset = (canvasH - layerH) / 2.0;
            posY[i] = topOffset + r * (NODE_H + ROW_GAP);
        }

        double canvasW = (maxLayer + 1) * (NODE_W + COL_GAP) - COL_GAP + NODE_W;
        GraphCanvas.Width = canvasW;
        GraphCanvas.Height = canvasH;

        // --- Draw edges first (behind nodes) ---
        var edgeList = new List<EdgeInfo>();

        for (int i = 0; i < n; i++)
        {
            double destX = posX[i];
            double destY = posY[i] + NODE_H / 2.0;

            foreach (var link in nodes[i].InputLinks)
            {
                int src = link.NodeIndex;
                if (src < 0 || src >= n) continue;

                double srcX = posX[src] + NODE_W;
                double srcY = posY[src] + NODE_H / 2.0;
                double cpOffset = Math.Abs(destX - srcX) * 0.5;

                var seg = new BezierSegment(
                    new Point(srcX + cpOffset, srcY),
                    new Point(destX - cpOffset, destY),
                    new Point(destX, destY),
                    isStroked: true);

                var fig = new PathFigure { StartPoint = new Point(srcX, srcY) };
                fig.Segments.Add(seg);
                var geo = new PathGeometry();
                geo.Figures.Add(fig);

                bool isCorrective = link.LinkFunction == FxLinkFunction.Corrective;
                var path = new Path
                {
                    Data = geo,
                    Stroke = isCorrective ? EdgeCorrectiveBrush : EdgeDefaultBrush,
                    StrokeThickness = 1.5,
                    ToolTip = BuildLinkTooltip(link)
                };

                GraphCanvas.Children.Add(path);
                edgeList.Add(new EdgeInfo(path, src, i, isCorrective));
            }
        }

        _edgeInfos = edgeList.ToArray();

        // --- Draw node boxes ---
        _finalValueBlocks  = new TextBlock[n];
        _nodeBorders       = new Border[n];
        _nodeBorderColors  = new Color[n];

        for (int i = 0; i < n; i++)
        {
            string nodeName = nodes[i].Name >= 0 && nodes[i].Name < _fxActor.Names.Count
                ? _fxActor.Names[nodes[i].Name]
                : $"#{i}";

            var (bg, borderColor) = NodeColors(nodes[i].NodeType);
            _nodeBorderColors[i] = borderColor;

            var nameBlock = new TextBlock
            {
                Text = nodeName,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(3, 0, 3, 0)
            };

            var typeBlock = new TextBlock
            {
                Text = $"{nodes[i].NodeType} · {nodes[i].InputOperation}",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(3, 0, 3, 0)
            };

            var valueBlock = new TextBlock
            {
                Text = nodes[i].FinalValue.ToString("F4"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xFF, 0x88)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(3, 1, 3, 1)
            };
            _finalValueBlocks[i] = valueBlock;

            var stack = new StackPanel();
            stack.Children.Add(nameBlock);
            stack.Children.Add(typeBlock);
            stack.Children.Add(valueBlock);

            var box = new Border
            {
                Width = NODE_W,
                Height = NODE_H,
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(3),
                Child = stack,
                Cursor = Cursors.Hand,
                ToolTip = $"[{i}] {nodeName}\nType: {nodes[i].NodeType}\nInputOp: {nodes[i].InputOperation}\nLinks: {nodes[i].InputLinks.Count}"
            };

            // Capture loop variable for the lambda
            int nodeIndex = i;
            box.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; // prevent canvas background handler from firing
                OnNodeClicked(nodeIndex);
            };

            Canvas.SetLeft(box, posX[i]);
            Canvas.SetTop(box, posY[i]);
            GraphCanvas.Children.Add(box);
            _nodeBorders[i] = box;
        }

        InfoBar.Text = $"{n} nodes · {_edgeInfos.Length} edges";
    }

    private void OnNodeClicked(int nodeIndex)
    {
        // Toggle off if clicking the already-selected node
        if (_selectedNodeIndex == nodeIndex)
            nodeIndex = -1;

        _selectedNodeIndex = nodeIndex;
        ApplySelection();
    }

    private void OnCanvasBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selectedNodeIndex == -1) return;
        _selectedNodeIndex = -1;
        ApplySelection();
    }

    private void ApplySelection()
    {
        if (_edgeInfos == null || _nodeBorders == null) return;

        bool hasSelection = _selectedNodeIndex >= 0;

        // Update edges
        foreach (var edge in _edgeInfos)
        {
            bool connected = hasSelection &&
                             (edge.SrcNode == _selectedNodeIndex || edge.DstNode == _selectedNodeIndex);

            if (!hasSelection)
            {
                // No selection: restore defaults
                edge.PathElement.Stroke = edge.IsCorrective ? EdgeCorrectiveBrush : EdgeDefaultBrush;
                edge.PathElement.StrokeThickness = 1.5;
                edge.PathElement.Opacity = 1.0;
            }
            else if (connected)
            {
                edge.PathElement.Stroke = EdgeHighlightBrush;
                edge.PathElement.StrokeThickness = 2.5;
                edge.PathElement.Opacity = 1.0;
            }
            else
            {
                // Restore base color and dim
                edge.PathElement.Stroke = edge.IsCorrective ? EdgeCorrectiveBrush : EdgeDefaultBrush;
                edge.PathElement.StrokeThickness = 1.5;
                edge.PathElement.Opacity = 0.2;
            }
        }

        // Update node borders
        for (int i = 0; i < _nodeBorders.Length; i++)
        {
            if (!hasSelection || i == _selectedNodeIndex)
            {
                _nodeBorders[i].BorderBrush = i == _selectedNodeIndex
                    ? SelectionBorderBrush
                    : new SolidColorBrush(_nodeBorderColors[i]);
                _nodeBorders[i].BorderThickness = i == _selectedNodeIndex
                    ? new Thickness(2.5)
                    : new Thickness(1.5);
                _nodeBorders[i].Opacity = 1.0;
            }
            else
            {
                _nodeBorders[i].BorderBrush = new SolidColorBrush(_nodeBorderColors[i]);
                _nodeBorders[i].BorderThickness = new Thickness(1.5);
                _nodeBorders[i].Opacity = 0.5;
            }
        }
    }

    private static string BuildLinkTooltip(FxCompiledFaceGraphLink link)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(link.LinkFunction.ToString());
        if (link.Parameters is { Count: > 0 })
        {
            sb.Append(" (");
            for (int p = 0; p < link.Parameters.Count; p++)
            {
                if (p > 0) sb.Append(", ");
                sb.Append(link.Parameters[p].ToString("F3"));
            }
            sb.Append(')');
        }
        return sb.ToString();
    }

    private static (Color bg, Color border) NodeColors(FxNodeType nodeType) => nodeType switch
    {
        FxNodeType.BonePose       => (Color.FromRgb(0x1A, 0x3A, 0x6A), Color.FromRgb(0x4A, 0x7A, 0xCA)),
        FxNodeType.MorphTarget    => (Color.FromRgb(0x1A, 0x4A, 0x2A), Color.FromRgb(0x4A, 0xCA, 0x6A)),
        FxNodeType.MorphTargetUE3 => (Color.FromRgb(0x1A, 0x4A, 0x2A), Color.FromRgb(0x4A, 0xCA, 0x6A)),
        FxNodeType.GenericTarget  => (Color.FromRgb(0x5A, 0x3A, 0x1A), Color.FromRgb(0xCA, 0x7A, 0x4A)),
        FxNodeType.EmotionsWeight => (Color.FromRgb(0x4A, 0x1A, 0x3A), Color.FromRgb(0xCA, 0x4A, 0x7A)),
        FxNodeType.Combiner       => (Color.FromRgb(0x2A, 0x2A, 0x2A), Color.FromRgb(0x77, 0x77, 0x77)),
        _                         => (Color.FromRgb(0x22, 0x22, 0x22), Color.FromRgb(0x55, 0x55, 0x55)),
    };
}
