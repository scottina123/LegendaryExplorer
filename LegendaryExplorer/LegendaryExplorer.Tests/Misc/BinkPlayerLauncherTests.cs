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

    [TestMethod]
    public void CreateEmbeddedStartInfo_RequestsNativeBorderlessWindow()
    {
        var startInfo = BinkPlayerLauncher.CreateEmbeddedStartInfo(
            @"C:\Tools\Bink2ForUnreal.exe",
            @"C:\Movies\intro movie.bik");

        CollectionAssert.AreEqual(
            new[] { "BinkPlay", @"C:\Movies\intro movie.bik", "/I2" },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void TryGetBink2Duration_ReadsFrameRateFromHeader()
    {
        byte[] header = new byte[36];
        header[0] = (byte)'K';
        header[1] = (byte)'B';
        header[2] = (byte)'2';
        BitConverter.GetBytes(300u).CopyTo(header, 8);
        BitConverter.GetBytes(30u).CopyTo(header, 28);
        BitConverter.GetBytes(1u).CopyTo(header, 32);

        Assert.IsTrue(BinkPlayerLauncher.TryGetBink2Duration(header, out TimeSpan duration));
        Assert.AreEqual(TimeSpan.FromSeconds(10), duration);
    }

    [TestMethod]
    public void TryGetBink2Duration_DoesNotTreatBinkOneAsBinkTwo()
    {
        byte[] header = new byte[36];
        header[0] = (byte)'B';
        header[1] = (byte)'I';
        header[2] = (byte)'K';

        Assert.IsFalse(BinkPlayerLauncher.TryGetBink2Duration(header, out _));
    }
}
