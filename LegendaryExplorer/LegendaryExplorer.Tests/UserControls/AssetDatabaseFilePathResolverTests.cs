using System.Collections.Generic;
using System.IO;
using LegendaryExplorer.Tools.AssetDatabase;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class AssetDatabaseFilePathResolverTests
{
    [TestMethod]
    public void BuildIndexDisambiguatesDuplicatePackageNamesByContentDirectory()
    {
        string baseGamePath = Path.Combine("C:\\Games\\Mass Effect", "BIOGame", "CookedPCConsole",
            "SharedPackage.pcc");
        string dlcPath = Path.Combine("C:\\Games\\Mass Effect", "BIOGame", "DLC", "DLC_MOD_Test",
            "CookedPCConsole", "SharedPackage.pcc");
        var files = new List<FileNameDirKeyPair>
        {
            new("SharedPackage.pcc", 0),
            new("SharedPackage.pcc", 1),
        };

        Dictionary<int, string> index = AssetDatabaseFilePathResolver.BuildIndex(files,
            ["BIOGame", "DLC_MOD_Test"], [dlcPath, baseGamePath]);

        Assert.AreEqual(baseGamePath, index[0]);
        Assert.AreEqual(dlcPath, index[1]);
    }

    [TestMethod]
    public void BuildIndexMatchesFileAndContentDirectoryCaseInsensitively()
    {
        string installedPath = Path.Combine("C:\\Games\\Mass Effect", "BIOGame", "DLC", "DLC_MOD_Test",
            "CookedPCConsole", "GesturePackage.PCC");
        var files = new List<FileNameDirKeyPair> { new("gesturepackage.pcc", 0) };

        Dictionary<int, string> index = AssetDatabaseFilePathResolver.BuildIndex(files,
            ["dlc_mod_test"], [installedPath]);

        Assert.AreEqual(installedPath, index[0]);
    }
}
