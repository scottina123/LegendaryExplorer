using System.Reflection;
using LegendaryExplorer.Tools.AssetDatabase;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.AssetDatabase;

[TestClass]
public class TlkDisplayTests
{
    private static bool HasTlkData(string value) =>
        (bool)typeof(AssetDatabaseWindow)
            .GetMethod("HasTlkData", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [value])!;

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("No Data")]
    [DataRow("no data")]
    public void EmptyTlkValuesAreNotData(string value)
    {
        Assert.IsFalse(HasTlkData(value));
    }

    [TestMethod]
    public void ResolvedTlkValueIsData()
    {
        Assert.IsTrue(HasTlkData("I should go."));
    }
}
