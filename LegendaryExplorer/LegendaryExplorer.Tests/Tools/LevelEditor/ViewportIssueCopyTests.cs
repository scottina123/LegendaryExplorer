using System;
using System.Reflection;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class ViewportIssueCopyTests
{
    [STATestMethod]
    public void CopyActionIncludesLatestExceptionDetailsAndAllExistingLevelIssues()
    {
        var context = new MeshRenderContext();
        using var viewport = new SceneRenderControl { Context = context };
        string copiedText = null;
        string issues = "BioD_Hub.pcc unrendered: UnknownActor";
        var command = viewport.CreateCopyViewportIssuesCommand(() => issues, text => copiedText = text);

        // The command is created before the viewport fails, as it is when opening the editor.
        string resourceError = CaptureError("Resource allocation failed").FlattenException();
        typeof(SceneRenderControl).GetProperty(nameof(SceneRenderControl.ResourceErrorText))!
            .SetValue(viewport, resourceError);
        context.ErrorText = CaptureError("Material rendering failed").FlattenException();
        issues += Environment.NewLine + "Navigation generator: disconnected path node 42";

        Assert.IsTrue(command.CanExecute(null));
        command.Execute(null);

        StringAssert.Contains(copiedText, resourceError);
        StringAssert.Contains(copiedText, context.ErrorText);
        StringAssert.Contains(copiedText, nameof(InvalidOperationException));
        StringAssert.Contains(copiedText, nameof(CaptureError));
        StringAssert.Contains(copiedText, "Inner device failure");
        StringAssert.Contains(copiedText, issues);

        context.ErrorText = "A later viewport failure";
        issues = "A later level issue";
        command.Execute(null);
        StringAssert.Contains(copiedText, "A later viewport failure");
        StringAssert.Contains(copiedText, "A later level issue");
        Assert.IsFalse(copiedText.Contains("Material rendering failed", StringComparison.Ordinal));
        Assert.IsFalse(copiedText.Contains("disconnected path node", StringComparison.Ordinal));
    }

    [STATestMethod]
    public void CopyActionWorksBeforeAnyLevelHasLoaded()
    {
        using var viewport = new SceneRenderControl();
        string copiedText = null;
        var command = viewport.CreateCopyViewportIssuesCommand(() => null, text => copiedText = text);

        Assert.IsTrue(command.CanExecute(null));
        command.Execute(null);

        Assert.AreEqual("No viewport errors or level issues have been recorded.", copiedText);
    }

    private static Exception CaptureError(string message)
    {
        try
        {
            throw new InvalidOperationException(message, new Exception("Inner device failure"));
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
