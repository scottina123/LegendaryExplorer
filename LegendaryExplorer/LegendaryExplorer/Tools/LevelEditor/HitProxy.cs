using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LegendaryExplorer.Tools.LevelEditor;

public interface IHitProxy
{
    int HitID { get; set; }

    bool IsUI { get; }
}


public class AxisHitProxy(EWidgetAxis axis) : IHitProxy
{
    public EWidgetAxis Axis { get; } = axis;
    public int HitID { get; set; }
    public bool IsUI => true;
}