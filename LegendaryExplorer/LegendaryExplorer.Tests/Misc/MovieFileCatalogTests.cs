using System;
using System.IO;
using System.Linq;
using LegendaryExplorer.Misc;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Misc;

[TestClass]
public class MovieFileCatalogTests
{
    private string _testRoot;

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "LegendaryExplorerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [TestMethod]
    public void FindMovies_ScansBasegameAndNestedDlcMovieFolders()
    {
        string bioGame = Path.Combine(_testRoot, "BioGame");
        string baseMovies = Path.Combine(bioGame, "Movies", "Localized");
        string dlc = Path.Combine(bioGame, "DLC", "DLC_MOD_Test");
        string dlcMovies = Path.Combine(dlc, "CookedPCConsole", "Movies");
        Directory.CreateDirectory(baseMovies);
        Directory.CreateDirectory(dlcMovies);
        Directory.CreateDirectory(Path.Combine(dlc, "CookedPCConsole", "NotMovies"));

        File.WriteAllBytes(Path.Combine(baseMovies, "Intro.BIK"), []);
        File.WriteAllBytes(Path.Combine(dlcMovies, "DlcOutro.bk2"), []);
        File.WriteAllBytes(Path.Combine(dlcMovies, "Readme.txt"), []);
        File.WriteAllBytes(Path.Combine(dlc, "CookedPCConsole", "NotMovies", "Ignored.bik"), []);

        var movies = MovieFileCatalog.FindMovies(bioGame, [dlc]);

        Assert.AreEqual(2, movies.Count);
        Assert.IsTrue(movies.Any(movie => movie.Name == "Intro" && movie.Source == "Basegame"));
        Assert.IsTrue(movies.Any(movie => movie.Name == "DlcOutro" && movie.Source == "DLC_MOD_Test"));
    }

    [TestMethod]
    public void FindMovies_KeepsDuplicateNamesFromDifferentSources()
    {
        string bioGame = Path.Combine(_testRoot, "BioGame");
        string baseMovies = Path.Combine(bioGame, "Movies");
        string dlc = Path.Combine(bioGame, "DLC", "DLC_MOD_Override");
        string dlcMovies = Path.Combine(dlc, "Movies");
        Directory.CreateDirectory(baseMovies);
        Directory.CreateDirectory(dlcMovies);
        File.WriteAllBytes(Path.Combine(baseMovies, "SharedName.bik"), []);
        File.WriteAllBytes(Path.Combine(dlcMovies, "SharedName.bik"), []);

        var movies = MovieFileCatalog.FindMovies(bioGame, [dlc]);

        Assert.AreEqual(2, movies.Count(movie => movie.Name == "SharedName"));
    }

    [TestMethod]
    public void PropertyNode_ShowsMoviePickerOnlyForMovieNameStrings()
    {
        var movieNameNode = new UPropertyTreeViewEntry(new StrProperty("Intro", "m_sMovieName"));
        var unrelatedStringNode = new UPropertyTreeViewEntry(new StrProperty("Intro", "OtherName"));
        var unrelatedTypeNode = new UPropertyTreeViewEntry(new NameProperty("Intro", "m_sMovieName"));

        Assert.IsTrue(movieNameNode.ShowMoviePicker);
        Assert.IsFalse(unrelatedStringNode.ShowMoviePicker);
        Assert.IsFalse(unrelatedTypeNode.ShowMoviePicker);
    }
}
