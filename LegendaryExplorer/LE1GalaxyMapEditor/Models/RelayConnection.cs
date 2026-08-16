namespace LE1GalaxyMapEditor.Models;

public sealed class RelayConnection : GalaxyMapRow
{
    private int _startClusterEncoded;
    private int _endClusterEncoded;
    private Cluster? _startCluster;
    private Cluster? _endCluster;

    public override GalaxyMapTable Table => GalaxyMapTable.Relay;

    public int StartClusterEncoded { get => _startClusterEncoded; set => SetProperty(ref _startClusterEncoded, value); }
    public int EndClusterEncoded { get => _endClusterEncoded; set => SetProperty(ref _endClusterEncoded, value); }
    public Cluster? StartCluster
    {
        get => _startCluster;
        internal set
        {
            if (SetProperty(ref _startCluster, value)) OnPropertyChanged(nameof(IsResolved));
        }
    }

    public Cluster? EndCluster
    {
        get => _endCluster;
        internal set
        {
            if (SetProperty(ref _endCluster, value)) OnPropertyChanged(nameof(IsResolved));
        }
    }

    public bool IsResolved => StartCluster is not null && EndCluster is not null;
}
