using System.Reflection;
using System.Windows;
using LegendaryExplorer.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests;

[TestClass]
public class TestAssemblyInitialization
{
    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        // The test host is the entry assembly, so WPF's public setter cannot be used.
        // Set the application resource assembly before any pack URI or icon is cached.
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(ListDialog).Assembly);
    }
}
