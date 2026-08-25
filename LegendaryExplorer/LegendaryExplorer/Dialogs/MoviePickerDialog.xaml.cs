using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Packages;
using Forms = System.Windows.Forms;

namespace LegendaryExplorer.Dialogs;

public partial class MoviePickerDialog : Window
{
    private readonly MEGame _game;
    private readonly string _currentMovieName;
    private readonly EmbeddedBinkPlayerHost _previewPlayer;
    private readonly string _playerExecutable;
    private IReadOnlyList<MovieFileCatalogItem> _allMovies = [];
    private string _previewPath;
    private bool _loopPreview;
    private bool _closing;

    public string SelectedMovieName { get; private set; }

    public MoviePickerDialog(MEGame game, string currentMovieName, Window owner = null)
    {
        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);

        _game = game;
        _currentMovieName = NormalizeMovieName(currentMovieName);
        if (owner is not null)
        {
            Owner = owner;
        }

        var previewViewport = new Forms.Panel
        {
            BackColor = System.Drawing.Color.Black,
            Dock = Forms.DockStyle.Fill
        };
        PreviewFormsHost.Child = previewViewport;
        _previewPlayer = new EmbeddedBinkPlayerHost(previewViewport);
        _previewPlayer.PlayerAttached += PreviewPlayer_PlayerAttached;
        _previewPlayer.PlayerExited += PreviewPlayer_PlayerExited;
        _previewPlayer.EmbeddingFailed += PreviewPlayer_EmbeddingFailed;

        try
        {
            string playerExecutable = BinkPlayerLauncher.FindExecutable(Settings.BIKExternal_BinkPlayerPath);
            if (BinkPlayerLauncher.SupportsBink2(playerExecutable))
            {
                _playerExecutable = playerExecutable;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine("Could not locate the Bink 2 preview player: " + exception.Message);
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchTextBox.IsEnabled = false;
        CatalogStatusTextBlock.Text = $"Finding {_game} movies...";
        PreviewStatusTextBlock.Text = _playerExecutable is null
            ? "A Bink 2-capable RAD player was not found. Configure Bink2ForUnreal.exe or a current RAD Video Tools player in Settings > Export Loaders to enable previews."
            : "Select a movie to preview it.";

        try
        {
            _allMovies = await Task.Run(() => MovieFileCatalog.FindMovies(_game));
        }
        catch (Exception exception)
        {
            CatalogStatusTextBlock.Text = "The movie folders could not be scanned: " + exception.Message;
            return;
        }

        if (_closing)
        {
            return;
        }

        SearchTextBox.IsEnabled = true;
        RefreshMovieList();
        SearchTextBox.Focus();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshMovieList();
        }
    }

    private void RefreshMovieList()
    {
        string search = SearchTextBox.Text.Trim();
        List<MovieFileCatalogItem> filtered = _allMovies
            .Where(movie => search.Length == 0
                            || movie.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                            || movie.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                            || movie.Source.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                            || movie.FilePath.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        MovieFileCatalogItem previousSelection = MovieListBox.SelectedItem as MovieFileCatalogItem;
        MovieListBox.ItemsSource = filtered;
        CatalogStatusTextBlock.Text = filtered.Count == _allMovies.Count
            ? $"{filtered.Count} movie(s) found in basegame and DLC Movies folders."
            : $"{filtered.Count} of {_allMovies.Count} movie(s) shown.";

        MovieListBox.SelectedItem = previousSelection is not null && filtered.Contains(previousSelection)
            ? previousSelection
            : filtered.FirstOrDefault(movie => movie.Name.Equals(_currentMovieName, StringComparison.OrdinalIgnoreCase))
              ?? filtered.FirstOrDefault();
    }

    private void MovieListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _loopPreview = false;
        _previewPath = null;
        _previewPlayer.StopPlayback();

        var selectedMovie = MovieListBox.SelectedItem as MovieFileCatalogItem;
        ChooseButton.IsEnabled = selectedMovie is not null;
        SelectedMovieTextBlock.Text = selectedMovie is null
            ? string.Empty
            : $"{selectedMovie.Name} — {selectedMovie.Source}";

        if (selectedMovie is null)
        {
            PreviewStatusTextBlock.Text = "Select a movie to preview it.";
            return;
        }

        if (_playerExecutable is null)
        {
            PreviewStatusTextBlock.Text = "Preview unavailable: configure Bink2ForUnreal.exe or a current RAD Video Tools player in Settings > Export Loaders.";
            return;
        }

        _previewPath = selectedMovie.FilePath;
        _loopPreview = true;
        StartPreview();
    }

    private void StartPreview()
    {
        if (_closing || !_loopPreview || !File.Exists(_previewPath))
        {
            return;
        }

        try
        {
            PreviewStatusTextBlock.Text = "Starting looping preview...";
            _previewPlayer.Start(BinkPlayerLauncher.CreateEmbeddedStartInfo(_playerExecutable, _previewPath));
        }
        catch (Exception exception)
        {
            _loopPreview = false;
            PreviewStatusTextBlock.Text = "The movie preview could not start: " + exception.Message;
            Debug.WriteLine("Error launching movie picker preview: " + exception);
        }
    }

    private void PreviewPlayer_PlayerAttached(object sender, EventArgs e)
    {
        PreviewStatusTextBlock.Text = "Looping preview";
    }

    private void PreviewPlayer_PlayerExited(object sender, EventArgs e)
    {
        if (!_closing && _loopPreview)
        {
            Dispatcher.BeginInvoke(StartPreview, DispatcherPriority.Background);
        }
    }

    private void PreviewPlayer_EmbeddingFailed(Exception exception)
    {
        _loopPreview = false;
        PreviewStatusTextBlock.Text = "The Bink player could not be embedded for preview: " + exception.Message;
    }

    private void MovieListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AcceptSelection();
    }

    private void ChooseButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptSelection();
    }

    private void AcceptSelection()
    {
        if (MovieListBox.SelectedItem is not MovieFileCatalogItem selectedMovie)
        {
            return;
        }

        SelectedMovieName = selectedMovie.Name;
        DialogResult = true;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _closing = true;
        _loopPreview = false;
        _previewPlayer.PlayerAttached -= PreviewPlayer_PlayerAttached;
        _previewPlayer.PlayerExited -= PreviewPlayer_PlayerExited;
        _previewPlayer.EmbeddingFailed -= PreviewPlayer_EmbeddingFailed;
        _previewPlayer.Dispose();
    }

    private static string NormalizeMovieName(string movieName)
    {
        if (string.IsNullOrWhiteSpace(movieName))
        {
            return string.Empty;
        }

        string fileName = Path.GetFileName(movieName.Trim());
        string extension = Path.GetExtension(fileName);
        return extension.Equals(".bik", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bk2", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }
}
