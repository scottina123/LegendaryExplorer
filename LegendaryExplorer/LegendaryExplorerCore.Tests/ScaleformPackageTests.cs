using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Scaleform;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ScaleformPackageTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void AppliedMovieSurvivesPackageSerializationAndPreservesOtherProperties(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("NativeScaleform.pcc", game);
        var movie = CreateMovie(package, "Movie");
        byte[] before = movie.Data;
        var session = new ScaleformExportSession(movie);
        Assert.IsFalse(session.Apply(), "Opening and applying an untouched draft must not rewrite the export.");
        CollectionAssert.AreEqual(before, movie.Data);
        movie.WriteProperty(new StrProperty("changed elsewhere", "SourceFilePath"));
        session.Document.Tags[0].SetBackgroundColor(0x20, 0x40, 0x60);
        Assert.IsTrue(session.Apply());
        Assert.IsFalse(session.HasExternalChanges);
        Assert.IsFalse(session.Document.IsModified);
        Assert.AreEqual("changed elsewhere", movie.GetProperty<StrProperty>("SourceFilePath").Value);
        Assert.AreEqual("keep timestamp", movie.GetProperty<StrProperty>("SourceFileTimestamp").Value);
        Assert.AreEqual(1234, movie.GetProperty<IntProperty>("UserData").Value);

        using var stream = package.SaveToStream(compress: false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream, "NativeScaleform.pcc");
        var reopenedMovie = reopened.GetUExport(movie.UIndex);
        var document = new ScaleformExportSession(reopenedMovie).Document;
        CollectionAssert.AreEqual(new byte[] { 0x20, 0x40, 0x60 }, document.Tags[0].Payload.ToArray());
        Assert.AreEqual("changed elsewhere", reopenedMovie.GetProperty<StrProperty>("SourceFilePath").Value);
        Assert.AreEqual("keep timestamp", reopenedMovie.GetProperty<StrProperty>("SourceFileTimestamp").Value);
        Assert.AreEqual(1234, reopenedMovie.GetProperty<IntProperty>("UserData").Value);
        CollectionAssert.AreEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, reopenedMovie.GetBinaryData());
    }

    [TestMethod]
    public void ConcurrentMovieEditsAreRejectedWithoutChangingPackageOrLosingDraft()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("Concurrent.pcc", MEGame.LE3);
        var movie = CreateMovie(package, "Movie");
        var first = new ScaleformExportSession(movie);
        var second = new ScaleformExportSession(movie);
        first.Document.Tags[0].SetBackgroundColor(10, 20, 30);
        second.Document.Tags[0].SetBackgroundColor(40, 50, 60);
        Assert.IsTrue(second.Apply());
        byte[] afterSecond = movie.Data;
        Assert.IsTrue(first.HasExternalChanges);
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Apply());
        CollectionAssert.AreEqual(afterSecond, movie.Data);
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30 }, ScaleformDocument.Parse(first.Compile()).Tags[0].Payload.ToArray());
    }

    [TestMethod]
    public void ResetDraftKeepsOriginalSnapshotAndCannotAcceptAnotherEditorsMovie()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("Reset.pcc", MEGame.LE3);
        var movie = CreateMovie(package, "Movie");
        var session = new ScaleformExportSession(movie);
        byte[] original = session.Compile();
        session.Document.Tags[0].SetBackgroundColor(30, 40, 50);
        var other = new ScaleformExportSession(movie);
        other.Document.FrameRate = 60;
        other.Apply();
        session.ResetDraft();
        CollectionAssert.AreEqual(original, session.Compile());
        Assert.IsTrue(session.HasExternalChanges);
    }

    [TestMethod]
    public void InvalidMovieAndScriptDoNotModifyExport()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("Invalid.pcc", MEGame.LE3);
        var movie = CreateMovie(package, "Movie");
        var session = new ScaleformExportSession(movie);
        byte[] before = movie.Data;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.Document.FrameRate = double.NaN);
        Assert.ThrowsExactly<InvalidDataException>(() => session.Document.Tags[1].SetActionBytes(new byte[] { 0x99, 2, 0, 0xFF, 0x7F, 0 }));
        CollectionAssert.AreEqual(before, movie.Data);
        movie.WriteProperty(new ImmutableByteArrayProperty(new byte[] { 1, 2, 3 }, "RawData"));
        before = movie.Data;
        Assert.ThrowsExactly<InvalidDataException>(() => new ScaleformExportSession(movie));
        CollectionAssert.AreEqual(before, movie.Data);
    }

    private static ExportEntry CreateMovie(IMEPackage package, string name)
    {
        bool bioSwf = package.Game == MEGame.ME1;
        var movie = package.CreateExport(name, bioSwf ? "BioSWF" : "GFxMovieInfo", indexed: false);
        // GFX v8, minimal RECT, rate24, one frame, background, action End, ShowFrame, End.
        byte[] bytes = { 0x47, 0x46, 0x58, 8, 26, 0, 0, 0, 8, 0, 0, 24, 1, 0,
            0x43, 2, 1, 2, 3, 1, 3, 0, 0x40, 0, 0, 0 };
        movie.WriteProperties(new PropertyCollection
        {
            new ImmutableByteArrayProperty(bytes, bioSwf ? "Data" : "RawData"),
            new StrProperty("keep source", "SourceFilePath"),
            new StrProperty("keep timestamp", "SourceFileTimestamp"),
            new IntProperty(1234, "UserData")
        });
        movie.WriteBinary(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        return movie;
    }
}
