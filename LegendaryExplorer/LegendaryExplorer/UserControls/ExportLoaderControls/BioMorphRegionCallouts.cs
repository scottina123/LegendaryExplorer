using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LegendaryExplorerCore.Misc;
using Point = System.Windows.Point;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public abstract class MorphRegionEditorItem : NotifyPropertyChangedBase
{
    private IReadOnlyList<MorphViewportRegion> regionSwatches = [];
    private bool isViewportSelected;
    public IReadOnlyList<MorphViewportRegion> RegionSwatches
    {
        get => regionSwatches;
        internal set
        {
            if (SetProperty(ref regionSwatches, value)) OnPropertyChanged(nameof(RegionBrush));
        }
    }
    public Brush RegionBrush => RegionSwatches.FirstOrDefault()?.Brush ?? Brushes.Transparent;
    public bool IsViewportSelected { get => isViewportSelected; internal set => SetProperty(ref isViewportSelected, value); }
}

public partial class MeshRenderer
{
    private sealed class MorphRegionCallout
    {
        internal MorphViewportRegion Region;
        internal MorphRegionSurface Surface;
        internal int Triangle;
        internal Vector3 Barycentric;
        internal Button Caption;
        internal Line Leader;
        internal Line Shadow;
        internal Ellipse Dot;
    }

    private readonly Dictionary<MorphViewportRegion, MorphRegionCallout> morphRegionCallouts = [];
    private long morphRegionVisibilityCheck;

    private MorphViewportRegion GetPaintedMorphRegion(MorphViewportHit hit)
    {
        var surface = morphRegionSurfaces.FirstOrDefault(item => item.Hair == hit.Hair && item.Lod == hit.Lod);
        if (surface == null || (uint)hit.TriangleIndex >= surface.TriangleOwners.Length) return null;
        int owner = surface.TriangleOwners[hit.TriangleIndex];
        return (uint)owner < surface.Regions.Length && surface.Regions[owner].Mode == MorphViewportPickMode ? surface.Regions[owner] : null;
    }

    private MorphViewportMatch CreateMorphRegionMatch(MorphViewportRegion region)
    {
        if (MorphViewportPickMode == MorphViewportPickMode.Materials)
        {
            var surface = morphRegionSurfaces.FirstOrDefault(item => item.Regions.Contains(region));
            return surface == null ? null : GetMorphViewportMatches(surface.Hit(region)).FirstOrDefault();
        }
        var skeleton = region.Hair ? MorphHairBindSkeleton : MorphBindSkeleton;
        int bone = Array.FindIndex(skeleton, item => item.Name.Instanced.Equals(region.Name, StringComparison.OrdinalIgnoreCase));
        return new MorphViewportMatch
        {
            Mode = MorphViewportPickMode, TargetName = region.Name, Description = $"{region.Surface} colored region",
            Feature = MorphViewportPickMode == MorphViewportPickMode.Features
                ? MorphFeatureItems.FirstOrDefault(item => item.Name.Equals(region.Name, StringComparison.OrdinalIgnoreCase)) : null,
            Bone = MorphViewportPickMode == MorphViewportPickMode.Skeleton
                ? MorphSkeletonItems.FirstOrDefault(item => item.Name.Equals(region.Name, StringComparison.OrdinalIgnoreCase)) : null,
            BonePosition = bone >= 0 ? skeleton[bone].Position : Vector3.Zero
        };
    }

    private void UpdateMorphEditorRegionAccents()
    {
        var selected = FindSelectedMorphRegion();
        var regions = MorphViewportRegionsList.Where(region => region.Mode == MorphViewportPickMode).ToArray();
        foreach (var item in MorphFeatureItems)
        {
            item.RegionSwatches = MorphViewportPickMode == MorphViewportPickMode.Features
                ? AccentsFor(region => region.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) : [];
            item.IsViewportSelected = SelectedMorphViewportMatch is { Mode: MorphViewportPickMode.Features } match
                && match.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var item in MorphSkeletonItems)
        {
            item.RegionSwatches = MorphViewportPickMode == MorphViewportPickMode.Skeleton
                ? AccentsFor(region => region.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) : [];
            item.IsViewportSelected = SelectedMorphViewportMatch is { Mode: MorphViewportPickMode.Skeleton } match
                && match.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase);
        }
        if (MorphViewportPickMode == MorphViewportPickMode.Materials)
            foreach (var region in regions)
                region.Material ??= CreateMorphRegionMatch(region)?.Material;
        foreach (var item in MorphScalarOverrides) SetMaterialAccents(item, region => region.Material?.DefinesScalarParameter(item.Name) == true);
        foreach (var item in MorphColorOverrides) SetMaterialAccents(item, region => region.Material?.DefinesVectorParameter(item.Name) == true);
        foreach (var item in MorphTextureOverrides) SetMaterialAccents(item, region => region.Material?.DefinesTextureParameter(item.Name) == true);

        void SetMaterialAccents(MorphRegionEditorItem item, Func<MorphViewportRegion, bool> defines)
        {
            item.RegionSwatches = MorphViewportPickMode == MorphViewportPickMode.Materials ? AccentsFor(defines) : [];
            item.IsViewportSelected = selected != null && item.RegionSwatches.Contains(selected);
        }

        // Shared materials and bones can affect both surfaces. Put the selected color first so
        // the row's main accent matches the clicked patch, retaining swatches for other surfaces.
        MorphViewportRegion[] AccentsFor(Func<MorphViewportRegion, bool> predicate) => regions.Where(predicate)
            .OrderByDescending(region => ReferenceEquals(region, selected)).ToArray();
    }

    private void RevealMorphSelectedControl()
    {
        if (SelectedMorphViewportMatch == null || MorphEditorTabs == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FindRow(MorphEditorTabs)?.BringIntoView()));

        static FrameworkElement FindRow(DependencyObject parent)
        {
            if (parent == null) return null;
            if (parent is Border { Tag: "MorphRegionEditorRow", DataContext: MorphRegionEditorItem { IsViewportSelected: true } } row) return row;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindRow(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
            return null;
        }
    }

    private void ResetMorphRegionCallouts()
    {
        morphRegionCallouts.Clear();
        MorphRegionCalloutCanvas?.Children.Clear();
        morphRegionVisibilityCheck = 0;
    }

    private bool ProjectMorphRegionPoint(Vector3 position, out Point point)
    {
        var clip = Vector4.Transform(new Vector4(position, 1), MeshContext.Camera.ViewMatrix * MeshContext.Camera.ProjectionMatrix);
        point = default;
        if (!float.IsFinite(clip.W) || clip.W <= 0 || clip.Z < 0 || clip.Z > clip.W) return false;
        point = new Point((clip.X / clip.W + 1) * SceneViewer.ActualWidth / 2, (1 - clip.Y / clip.W) * SceneViewer.ActualHeight / 2);
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private void UpdateMorphRegionCallouts()
    {
        if (MorphRegionCalloutCanvas == null || !ShowMorphEditorPanel || !ShowMorphRegionLabels || SceneViewer == null
            || SceneViewer.ActualWidth <= 0 || SceneViewer.ActualHeight <= 0) return;
        var viewport = new Size(SceneViewer.ActualWidth, SceneViewer.ActualHeight);
        if (Environment.TickCount64 >= morphRegionVisibilityCheck)
        {
            RefreshVisibleMorphRegionAnchors(viewport);
            morphRegionVisibilityCheck = Environment.TickCount64 + 150;
        }
        var visible = new List<(MorphRegionCallout Callout, Point Anchor)>();
        foreach (var callout in morphRegionCallouts.Values)
        {
            int corner = callout.Triangle * 3;
            var center = callout.Surface.Position(corner) * callout.Barycentric.X
                + callout.Surface.Position(corner + 1) * callout.Barycentric.Y + callout.Surface.Position(corner + 2) * callout.Barycentric.Z;
            if (ProjectMorphRegionPoint(center, out var anchor) && new Rect(viewport).Contains(anchor)) visible.Add((callout, anchor));
            else SetVisible(callout, false);
        }
        var positions = MorphRegionCalloutLayout.Place(viewport, visible.Select(item => item.Anchor).ToArray());
        var selected = FindSelectedMorphRegion();
        for (int index = 0; index < visible.Count; index++)
        {
            var (callout, anchor) = visible[index];
            Rect bounds = positions[index];
            SetVisible(callout, true);
            callout.Caption.Width = bounds.Width;
            callout.Caption.Height = bounds.Height;
            callout.Caption.FontWeight = ReferenceEquals(selected, callout.Region) ? FontWeights.Bold : FontWeights.Normal;
            Canvas.SetLeft(callout.Caption, bounds.X);
            Canvas.SetTop(callout.Caption, bounds.Y);
            double x = anchor.X < bounds.X + bounds.Width / 2 ? bounds.Left : bounds.Right;
            foreach (var line in new[] { callout.Shadow, callout.Leader })
            {
                line.X1 = anchor.X; line.Y1 = anchor.Y; line.X2 = x; line.Y2 = bounds.Y + bounds.Height / 2;
            }
            Canvas.SetLeft(callout.Dot, anchor.X - 3);
            Canvas.SetTop(callout.Dot, anchor.Y - 3);
        }

        static void SetVisible(MorphRegionCallout callout, bool value)
        {
            var visibility = value ? Visibility.Visible : Visibility.Collapsed;
            callout.Caption.Visibility = callout.Leader.Visibility = callout.Shadow.Visibility = callout.Dot.Visibility = visibility;
        }
    }

    private void RefreshVisibleMorphRegionAnchors(Size viewport)
    {
        var candidates = new Dictionary<MorphViewportRegion, List<(MorphRegionSurface Surface, int Triangle, Point Point, double Area)>>();
        foreach (var surface in morphRegionSurfaces)
            for (int triangle = 0; triangle < surface.TriangleOwners.Length; triangle++)
            {
                int owner = surface.TriangleOwners[triangle];
                if (owner < 0) continue;
                int corner = triangle * 3;
                if (!ProjectMorphRegionPoint(surface.Position(corner), out var a)
                    || !ProjectMorphRegionPoint(surface.Position(corner + 1), out var b)
                    || !ProjectMorphRegionPoint(surface.Position(corner + 2), out var c)) continue;
                var point = new Point((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3);
                if (!new Rect(viewport).Contains(point)) continue;
                double area = Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
                if (area < 0.5) continue;
                var region = surface.Regions[owner];
                if (!candidates.TryGetValue(region, out var list)) candidates[region] = list = [];
                if (list.Count == 4 && area <= list[^1].Area) continue;
                list.Add((surface, triangle, point, area));
                list.Sort((first, second) => second.Area.CompareTo(first.Area));
                if (list.Count > 4) list.RemoveAt(4);
            }
        var found = new HashSet<MorphViewportRegion>();
        foreach (var (region, samples) in candidates)
            foreach (var sample in samples)
            {
                var hit = FindMorphViewportHit(sample.Point);
                // A leader must end on this visible color, never on the back of the head or under hair.
                if (hit == null || !ReferenceEquals(GetPaintedMorphRegion(hit), region)) continue;
                if (!morphRegionCallouts.TryGetValue(region, out var callout))
                {
                    callout = AddMorphRegionCallout(region);
                    morphRegionCallouts.Add(region, callout);
                }
                callout.Surface = sample.Surface;
                callout.Triangle = hit.TriangleIndex;
                callout.Barycentric = hit.Barycentric;
                found.Add(region);
                break;
            }
        foreach (var region in morphRegionCallouts.Keys.Where(region => !found.Contains(region)).ToArray())
        {
            var callout = morphRegionCallouts[region];
            MorphRegionCalloutCanvas.Children.Remove(callout.Caption);
            MorphRegionCalloutCanvas.Children.Remove(callout.Leader);
            MorphRegionCalloutCanvas.Children.Remove(callout.Shadow);
            MorphRegionCalloutCanvas.Children.Remove(callout.Dot);
            morphRegionCallouts.Remove(region);
        }
    }

    private MorphRegionCallout AddMorphRegionCallout(MorphViewportRegion region)
    {
        var callout = new MorphRegionCallout
        {
            Region = region,
            Caption = new Button
            {
                DataContext = region, Content = new TextBlock { Text = region.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                Style = (Style)FindResource("MorphRegionCalloutStyle"), ToolTip = region.Detail
            },
            Shadow = new Line { Stroke = Brushes.Black, StrokeThickness = 3, Opacity = 0.65, IsHitTestVisible = false },
            Leader = new Line { Stroke = region.Brush, StrokeThickness = 1.4, IsHitTestVisible = false },
            Dot = new Ellipse { Fill = region.Brush, Stroke = Brushes.Black, StrokeThickness = 0.5, Width = 6, Height = 6, IsHitTestVisible = false }
        };
        callout.Caption.Click += MorphRegionLabel_Click;
        Panel.SetZIndex(callout.Caption, 2);
        Panel.SetZIndex(callout.Dot, 1);
        MorphRegionCalloutCanvas.Children.Add(callout.Shadow);
        MorphRegionCalloutCanvas.Children.Add(callout.Leader);
        MorphRegionCalloutCanvas.Children.Add(callout.Dot);
        MorphRegionCalloutCanvas.Children.Add(callout.Caption);
        return callout;
    }
}

internal static class MorphRegionCalloutLayout
{
    internal static Rect[] Place(Size viewport, IReadOnlyList<Point> anchors)
    {
        var result = new Rect[anchors.Count];
        if (anchors.Count == 0 || viewport.Width < 1 || viewport.Height < 1) return result;
        const double margin = 8, gap = 4;
        double height = Math.Min(24, viewport.Height);
        int rows = Math.Max(1, (int)((viewport.Height - 2 * margin) / (height + gap)));
        int columns = Math.Max(2, (int)Math.Ceiling((double)anchors.Count / rows));
        double width = Math.Max(1, Math.Min(184, (viewport.Width - 2 * margin) / columns - gap));
        var ordered = Enumerable.Range(0, anchors.Count).OrderBy(index => anchors[index].X).ToArray();
        int perColumn = (int)Math.Ceiling((double)anchors.Count / columns);
        for (int column = 0; column < columns; column++)
        {
            var group = ordered.Skip(column * perColumn).Take(perColumn).OrderBy(index => anchors[index].Y).ToArray();
            double x = columns == 2 ? column == 0 ? margin : viewport.Width - width - margin
                : margin + column * (viewport.Width - 2 * margin - width) / (columns - 1);
            double previous = margin - height - gap;
            foreach (int index in group)
            {
                double y = Math.Max(previous + height + gap, Math.Min(anchors[index].Y - height / 2, viewport.Height - height - margin));
                result[index] = new Rect(Math.Clamp(x, 0, Math.Max(0, viewport.Width - width)), y, width, height);
                previous = y;
            }
            double next = viewport.Height - margin;
            foreach (int index in group.Reverse())
            {
                var rect = result[index];
                rect.Y = Math.Max(0, Math.Min(rect.Y, next - height));
                result[index] = rect;
                next = rect.Y - gap;
            }
        }
        return result;
    }
}
