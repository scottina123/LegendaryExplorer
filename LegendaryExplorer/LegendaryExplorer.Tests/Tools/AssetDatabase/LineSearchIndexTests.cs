using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LegendaryExplorer.Tools.AssetDatabase;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
public class LineSearchIndexTests
{
    [TestMethod]
    public void IndexPreservesColumnsOwnerNamesUsageFilesAndSpeakerFilters()
    {
        var first = new ConvoLine(123, "Owner", "Conv_First") { Line = "I should go." };
        var second = new ConvoLine(456, "Shepard", "Conv_Second") { Line = "Goodbye." };
        var source = new[] { first, second }.Select(line => new LineSearchIndex.SourceLine(
            line, line.StrRef, line.Speaker, line.DisplayLine, line.Convo)).ToArray();
        AssetDatabaseWindow.FileDirPair[] files = [new("B.pcc", "BioGame", 0), new("A.pcc", "DLC_MOD_Test", 1)];
        var conversations = new Dictionary<string, LineSearchIndex.SourceConversation>(StringComparer.OrdinalIgnoreCase)
        {
            ["Conv_First"] = new(0, "Liara"), ["Conv_Second"] = new(0, null)
        };
        var usages = new Dictionary<int, TlkUsage[]>
        {
            [123] = [new(0, 1, false, false, TlkUsageContext.Package), new(1, 2, true, true, TlkUsageContext.Package),
                new(1, 3, true, true, TlkUsageContext.Package), new(-1, 0, false, false, TlkUsageContext.Package)]
        };
        LineSearchIndex index = LineSearchIndex.Build(source, files, conversations, usages, CancellationToken.None);
        foreach (var (query, column) in new[]
                 {
                     ("liara", "Speaker"), ("123", "TLK String Ref"), ("SHOULD GO", "Line"),
                     ("conv_first", "Line Conversation"), ("a.pcc", "File"), ("DLC_MOD_Test", "Location"),
                     ("Liara", "All Columns"), ("A.pcc, B.pcc", "All Columns")
                 })
            CollectionAssert.AreEquivalent(new[] { first }, index.Search(query, column, null, CancellationToken.None).ToArray());
        foreach (string speaker in new[] { "Owner", "Liara", "Owner (Liara)" })
            CollectionAssert.AreEquivalent(new[] { first }, index.Search("", "All Columns", speaker, CancellationToken.None).ToArray());
        Assert.HasCount(0, index.Search("Goodbye", "Line", "Liara", CancellationToken.None));
        Assert.HasCount(2, index.Search(" ", "All Columns", null, CancellationToken.None));
        Assert.IsTrue(index.TryGetUsageText(second, out string fallbackFiles, out string fallbackLocations));
        Assert.AreEqual("B.pcc", fallbackFiles);
        Assert.AreEqual("BioGame", fallbackLocations);
        // Snapshots remain valid when the live database changes during background work.
        first.Line = "Changed";
        CollectionAssert.AreEquivalent(new[] { first }, index.Search("I should go", "Line", null, CancellationToken.None).ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => index.Search("x", "Line", null, cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => LineSearchIndex.Build(source, files, conversations, usages, cancellation.Token));
    }
}
