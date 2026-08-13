using System;
using System.Linq;
using LegendaryExplorer.Misc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Misc;

[TestClass]
public class BinkPlayerLauncherTests
{
    [TestMethod]
    public void CreateStartInfo_AddsBinkPlayCommandForCombinedRadExecutables()
    {
        var startInfo = BinkPlayerLauncher.CreateStartInfo(
            @"C:\Tools\Bink2ForUnreal.exe",
            @"C:\Movies\intro movie.bik");

        CollectionAssert.AreEqual(
            new[] { "BinkPlay", @"C:\Movies\intro movie.bik" },
            startInfo.ArgumentList.ToArray());
        Assert.IsFalse(startInfo.UseShellExecute);
    }

    [TestMethod]
    public void CreateStartInfo_UsesMovieAsFirstArgumentForStandalonePlayer()
    {
        var startInfo = BinkPlayerLauncher.CreateStartInfo(
            @"C:\Tools\BinkPlay.exe",
            @"C:\Movies\intro movie.bik");

        CollectionAssert.AreEqual(
            new[] { @"C:\Movies\intro movie.bik" },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void SupportsBink2Version_RejectsLegacyBinkOnePlayer()
    {
        Assert.IsTrue(BinkPlayerLauncher.SupportsBink2Version("Bink2ForUnreal.exe", null));
        Assert.IsTrue(BinkPlayerLauncher.SupportsBink2Version("RADVideo64.exe", "2026.06"));
        Assert.IsTrue(BinkPlayerLauncher.SupportsBink2Version("BinkPlay.exe", "2.7f"));
        Assert.IsFalse(BinkPlayerLauncher.SupportsBink2Version("BinkPlay.exe", "1.9h"));
        Assert.IsFalse(BinkPlayerLauncher.SupportsBink2Version("Notepad.exe", "2026.06"));
    }

    [TestMethod]
    public void CreateStartInfo_RejectsUnrecognizedExecutable()
    {
        Assert.Throws<ArgumentException>(() =>
            BinkPlayerLauncher.CreateStartInfo(@"C:\Windows\notepad.exe", @"C:\Movies\intro.bik"));
    }
}
