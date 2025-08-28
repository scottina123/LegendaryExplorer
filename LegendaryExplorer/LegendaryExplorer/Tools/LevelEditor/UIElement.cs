using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Unreal.Collections;
using System;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

public class UIElement
{
    //we assume that begindraw has already been called
    public virtual void Draw(LevelEditorRenderContext context)
    {

    }
}

[Flags]
public enum EWidgetAxis
{
    None = 0,
    X = 1,
    Y = 2,
    Z = 4,
    XY = X | Y,
    XZ = X | Z,
    YZ = Y | Z,
    XYZ = X | Y | Z,
}

public enum EWidgetMode
{
    Translate,
    Rotate,
    UniformScale,
    Scale
}

public class Widget : UIElement
{

    public ActorProxy Attach;

    public EWidgetMode Mode = EWidgetMode.Rotate;

    public EWidgetAxis CurrentAxis;
    public bool IsDragging;
    private Vector2 DragStart;
    private Vector2 PrevDragPos;

    Vector2 Origin, XAxisEnd, YAxisEnd, ZAxisEnd;
    private readonly int[] AxisHitIds = new int[8];

    static readonly Vector4 XColor = new Vector4(1, 0, 0, 1);
    static readonly Vector4 YColor = new Vector4(0, 1, 0, 1);
    static readonly Vector4 ZColor = new Vector4(0, 0, 1, 1);
    static readonly Vector4 SelectedColor = new Vector4(1, 1, 0, 1);

    public override void Draw(LevelEditorRenderContext context)
    {
        if (Attach is null) return;

        var ltw = Attach.LocalToWorld;
        var origin = ltw.Translation;

        context.WorldToPixel(origin, out Origin);

        float scale = context.WorldToScreen(origin).W * (4f / context.Width / context.Camera.ProjectionMatrix[0, 0]);

        var xColor = CurrentAxis.HasFlag(EWidgetAxis.X) ? SelectedColor : XColor;
        var yColor = CurrentAxis.HasFlag(EWidgetAxis.Y) ? SelectedColor : YColor;
        var zColor = CurrentAxis.HasFlag(EWidgetAxis.Z) ? SelectedColor : ZColor;

        var xMatrix = Matrix4x4.CreateTranslation(origin);
        var yMatrix = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(origin);
        var zMatrix = Matrix4x4.CreateRotationY(-MathF.PI / 2) * Matrix4x4.CreateTranslation(origin);

        if (Mode is EWidgetMode.Rotate)
        {
            XAxisEnd = DrawQuarterCircle(context, scale, xMatrix, xColor, AxisHitIds[(int)EWidgetAxis.X]);
            YAxisEnd = DrawQuarterCircle(context, scale, yMatrix, yColor, AxisHitIds[(int)EWidgetAxis.Y]);
            ZAxisEnd = DrawQuarterCircle(context, scale, zMatrix, zColor, AxisHitIds[(int)EWidgetAxis.Z]);
        }
        else
        {
            bool useConeGrabber = Mode is EWidgetMode.Translate;
            if (Mode is EWidgetMode.UniformScale)
            {
                yColor = zColor = xColor = CurrentAxis == EWidgetAxis.None ? XColor : SelectedColor;
            }

            XAxisEnd = DrawAxis(context, scale, xMatrix, xColor, AxisHitIds[(int)EWidgetAxis.X], useConeGrabber);
            YAxisEnd = DrawAxis(context, scale, yMatrix, yColor, AxisHitIds[(int)EWidgetAxis.Y], useConeGrabber);
            ZAxisEnd = DrawAxis(context, scale, zMatrix, zColor, AxisHitIds[(int)EWidgetAxis.Z], useConeGrabber);
        }
    }

    private static Vector2 DrawAxis(LevelEditorRenderContext context, float scale, Matrix4x4 matrix, Vector4 color, int hitId, bool useConeGrabber)
    {
        const float lineStart = 2f;
        const float lineEnd = 40f;

        const int numArrowSegments = 6;
        const float arrowRadius = 6f;
        const float arrowHeight = 12f;

        const float cubeWidth = 5;

        var ltw = Matrix4x4.CreateScale(scale) * matrix;

        var p1 = Vector3.Transform(new Vector3(lineStart, 0, 0), ltw);
        var p2 = Vector3.Transform(new Vector3(lineEnd, 0, 0), ltw);

        context.WorldToPixel(p2, out Vector2 axisEnd);

        context.Primitives.AddLine(p1, p2, color, hitId);

        var mesh = context.Primitives.BuildMesh(color, hitId, ltw);

        if (useConeGrabber)
        {
            //base ring
            for (int i = 0; i < numArrowSegments; i++)
            {
                float theta = MathF.PI * 2f * i / numArrowSegments;
                mesh.AddVertex(lineEnd, arrowRadius * MathF.Sin(theta) * 0.5f, arrowRadius * MathF.Cos(theta) * 0.5f);
            }

            //point
            mesh.AddVertex(lineEnd + arrowHeight, 0, 0);

            for (int s = 0; s < numArrowSegments; s++)
            {
                mesh.AddTriangle(numArrowSegments, s, (s + 1) % numArrowSegments);
            }
        }
        else
        {   //cube
            const float halfCubeWidth = cubeWidth / 2;
            mesh.AddVertex(lineEnd, halfCubeWidth, halfCubeWidth);
            mesh.AddVertex(lineEnd, halfCubeWidth, -halfCubeWidth);
            mesh.AddVertex(lineEnd, -halfCubeWidth, -halfCubeWidth);
            mesh.AddVertex(lineEnd, -halfCubeWidth, halfCubeWidth);
            mesh.AddVertex(lineEnd + cubeWidth, halfCubeWidth, halfCubeWidth);
            mesh.AddVertex(lineEnd + cubeWidth, halfCubeWidth, -halfCubeWidth);
            mesh.AddVertex(lineEnd + cubeWidth, -halfCubeWidth, -halfCubeWidth);
            mesh.AddVertex(lineEnd + cubeWidth, -halfCubeWidth, halfCubeWidth);

            mesh.AddTriangle(0, 3, 2);
            mesh.AddTriangle(2, 1, 0);

            mesh.AddTriangle(0, 1, 4);
            mesh.AddTriangle(4, 1, 5);

            mesh.AddTriangle(4, 5, 6);
            mesh.AddTriangle(6, 7, 4);

            mesh.AddTriangle(7, 6, 2);
            mesh.AddTriangle(2, 3, 7);

            mesh.AddTriangle(5, 1, 2);
            mesh.AddTriangle(2, 6, 5);

            mesh.AddTriangle(0, 4, 7);
            mesh.AddTriangle(7, 3, 0);
        }

        return axisEnd;
    }

    private static Vector2 DrawQuarterCircle(LevelEditorRenderContext context, float scale, Matrix4x4 matrix, Vector4 color, int hitId)
    {
        const int numRingSegments = 24; 
        Span<float> radii = [70f, 60f];

        var ltw = Matrix4x4.CreateScale(scale) * matrix;

        var mesh = context.Primitives.BuildMesh(color with { W = 0.5f }, hitId, ltw);

        Vector3 prevPoint = default;

        for (int i = 0; i < radii.Length; i++)
        {
            for (int j = 0; j < numRingSegments; j++)
            {
                float theta = (MathF.PI / 2f) / (numRingSegments - 1) * j;
                var point = new Vector3(0, radii[i] * MathF.Sin(theta) * 0.5f, radii[i] * MathF.Cos(theta) * 0.5f);
                mesh.AddVertex(point);
                if (j > 0)
                {
                    context.Primitives.AddLine(Vector3.Transform(prevPoint, ltw), Vector3.Transform(point, ltw), color, hitId);
                }
                prevPoint = point;
            }
        }

        for (int i = 1; i < numRingSegments; i++)
        {
            mesh.AddTriangle(i - 1, i - 1 + numRingSegments, i);
            mesh.AddTriangle(i, i - 1 + numRingSegments, i + numRingSegments);
        }

        context.WorldToPixel(Vector3.Transform(new Vector3(radii[1], 0, 0), ltw), out Vector2 axisEnd);
        return axisEnd;
    }

    public void GetAxisHitProxies(ref USparseArray<IHitProxy> hitProxies)
    {
        foreach (EWidgetAxis axis in Enum.GetValues<EWidgetAxis>())
        {
            AxisHitIds[(int)axis] = hitProxies.Add(new AxisHitProxy(axis));
        }
    }

    public void Drag(int x, int y)
    {
        if (Attach is null) return;
        Vector2 mousePos = new(x, y);
        Vector2 dragDiff = PrevDragPos - mousePos;

        if (Mode is EWidgetMode.Rotate)
        {
            Vector2 startDir = Vector2.Normalize(PrevDragPos - Origin);
            Vector2 mouseDir = Vector2.Normalize(mousePos - Origin);
            float theta = MathF.Acos(Vector2.Dot(startDir, mouseDir));
            switch (CurrentAxis)
            {
                case EWidgetAxis.X:
                    break;
                case EWidgetAxis.Y:
                    break;
                case EWidgetAxis.Z:
                    Attach.Rotation += new LegendaryExplorerCore.Unreal.BinaryConverters.Rotator(0, theta.RadiansToUnrealRotationUnits(), 0);
                    break;
            }
            return;
        }

        Vector2 axisEnd = CurrentAxis switch
        {
            EWidgetAxis.X => XAxisEnd,
            EWidgetAxis.Y => YAxisEnd,
            EWidgetAxis.Z => ZAxisEnd,
            EWidgetAxis.XY => dragDiff.X != 0 ? XAxisEnd : YAxisEnd,
            EWidgetAxis.XZ => dragDiff.X != 0 ? XAxisEnd : ZAxisEnd,
            EWidgetAxis.YZ => dragDiff.X != 0 ? YAxisEnd : ZAxisEnd,
            EWidgetAxis.XYZ => dragDiff.X != 0 ? YAxisEnd : ZAxisEnd,
            _ => XAxisEnd
        };


        //dragDiff.Y *= -1;
        Vector2 axisDir = Vector2.Normalize(axisEnd - Origin);

        float dragAmount = Vector2.Dot(dragDiff, axisDir);

        switch (Mode)
        {
            case EWidgetMode.Translate:
            case EWidgetMode.Scale:
                Vector3 vecDiff = CurrentAxis switch
                {
                    EWidgetAxis.X => new Vector3(dragAmount, 0, 0),
                    EWidgetAxis.Y => new Vector3(0, dragAmount, 0),
                    EWidgetAxis.Z => new Vector3(0, 0, dragAmount),
                    EWidgetAxis.XY => throw new NotImplementedException(),
                    EWidgetAxis.XZ => throw new NotImplementedException(),
                    EWidgetAxis.YZ => throw new NotImplementedException(),
                    EWidgetAxis.XYZ => throw new NotImplementedException(),
                };
                if (Mode is EWidgetMode.Translate)
                {
                    Attach.Location -= vecDiff;
                }
                else
                {
                    Attach.DrawScale3D -= vecDiff / 10;
                }
                break;
            case EWidgetMode.UniformScale:
                Attach.DrawScale -= dragAmount / 10;
                break;
        }
        PrevDragPos = new Vector2(x, y);
    }

    public void BeginDrag(int x, int y)
    {
        IsDragging = true;
        PrevDragPos = DragStart = new Vector2(x, y);
    }

    public void EndDrag()
    {
        IsDragging = false;
    }
}
