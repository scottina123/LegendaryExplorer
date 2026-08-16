using LE1GalaxyMapEditor.Infrastructure;

namespace LE1GalaxyMapEditor.ViewModels;

public abstract class MapViewModelBase : ObservableObject
{
    private int _refreshToken;

    public int RefreshToken { get => _refreshToken; private set => SetProperty(ref _refreshToken, value); }

    public void Refresh() => RefreshToken++;
}
