using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.TLK;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

internal static class TLKEditorSearchTests
{
    internal static void AssertToolbarSearchScopesMatchesAndEnterRepeatsTheLastMode()
    {
        using var editor = new TLKEditorExportLoader { CurrentLoadedFile = "SearchTest_INT.tlk" };
        var partialId = new TLKStringRef(1234, "First nebula line");
        var textMatch = new TLKStringRef(500, "Contains 123 in the text");
        var exactId = new TLKStringRef(123, "Second NEBULA line");
        var lastTextMatch = new TLKStringRef(700, "Third Nebula line");
        editor.LoadedStrings = [partialId, textMatch, exactId, lastTextMatch];
        editor.CleanedStrings.AddRange(editor.LoadedStrings);
        var grid = (DataGrid)editor.FindName("DisplayedString_ListBox");
        var searchBox = (TextBox)editor.FindName("boxSearch");
        var window = new Window
        {
            Content = editor, Width = 1000, Height = 600,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000
        };
        try
        {
            window.Show();
            FlushDispatcher();
            Assert.AreEqual(string.Empty, searchBox.Text, "The toolbar input must start blank.");
            Select(grid, partialId, 0);

            searchBox.Text = "123";
            PressEnter(searchBox);
            AssertCurrent(grid, textMatch, 1, "Enter should initially search only string text.");
            AssertHighlight(grid, textMatch, 1, 9, "123");

            editor.SearchIdCommand.Execute(null);
            AssertCurrent(grid, exactId, 0, "An ID search must match numeric ID 123 exactly, not 1234 or string text.");
            AssertHighlight(grid, exactId, 0, 0, "123");

            Select(grid, partialId, 0);
            editor.SearchTextCommand.Execute(null);
            AssertCurrent(grid, textMatch, 1, "A text search must ignore matching digits in the ID column.");

            searchBox.Text = "nEbUlA";
            editor.SearchTextCommand.Execute(null);
            AssertCurrent(grid, exactId, 1, "Text search must be case-insensitive.");
            AssertHighlight(grid, exactId, 1, 7, "NEBULA");
            editor.SearchTextCommand.Execute(null);
            AssertCurrent(grid, lastTextMatch, 1, "Repeating text search must advance to the next matching row.");
            editor.SearchTextCommand.Execute(null);
            AssertCurrent(grid, partialId, 1, "Text search must wrap to an earlier matching row.");
            PressEnter(searchBox);
            AssertCurrent(grid, exactId, 1, "Enter must repeat the most recently selected text search mode.");
            AssertHighlight(grid, exactId, 1, 7, "NEBULA");

            searchBox.Text = " 123 ";
            editor.SearchIdCommand.Execute(null);
            AssertCurrent(grid, exactId, 0, "Whitespace around a numeric ID must not change its exact match.");
            AssertHighlight(grid, exactId, 0, 0, "123");
            Select(grid, textMatch, 1);
            PressEnter(searchBox);
            AssertCurrent(grid, exactId, 0, "Enter must repeat ID mode after the ID search button is used.");
            AssertHighlight(grid, exactId, 0, 0, "123");

            foreach (string blank in new[] { string.Empty, "   " })
            {
                searchBox.Text = blank;
                editor.SearchIdCommand.Execute(null);
                AssertCurrent(grid, exactId, 0, "Blank ID input must leave the current string unchanged.");
                editor.SearchTextCommand.Execute(null);
                AssertCurrent(grid, exactId, 0, "Blank text input must leave the current string unchanged.");
                PressEnter(searchBox);
                AssertCurrent(grid, exactId, 0, "Enter with blank input must leave the current string unchanged.");
            }

            Assert.HasCount(4, editor.LoadedStrings);
            Assert.AreEqual("Contains 123 in the text", textMatch.Data);
            Assert.AreEqual("Second NEBULA line", exactId.Data);

            searchBox.Focus();
            AssertHighlight(grid, exactId, 0, 0, "123");

            // Force a match outside the realized rows and beyond the first line.
            editor.LoadedStrings.AddRange(Enumerable.Range(1000, 200).Select(id => new TLKStringRef(id, "Other line")));
            var distantMatch = new TLKStringRef(9999, "First line\nFind this NEBULA here");
            editor.LoadedStrings.Add(distantMatch);
            editor.CleanedStrings.ReplaceAll(editor.LoadedStrings);
            Select(grid, partialId, 0);
            searchBox.Text = "this nebula";
            editor.SearchTextCommand.Execute(null);
            AssertCurrent(grid, distantMatch, 1, "Search must bring a virtualized matching row into view.");
            AssertHighlight(grid, distantMatch, 1, 16, "this NEBULA");
            Assert.IsFalse(editor.FileModified, "Highlighting a search match must not modify the TLK.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void Select(DataGrid grid, TLKStringRef item, int columnIndex)
    {
        grid.CurrentCell = new DataGridCellInfo(item, grid.Columns[columnIndex]);
        grid.SelectedItem = item;
    }

    private static void AssertCurrent(DataGrid grid, TLKStringRef item, int columnIndex, string message)
    {
        Assert.AreSame(item, grid.CurrentCell.Item, message);
        Assert.AreSame(item, grid.SelectedItem, message);
        Assert.AreSame(grid.Columns[columnIndex], grid.CurrentCell.Column, message);
    }

    private static void AssertHighlight(DataGrid grid, TLKStringRef item, int columnIndex, int start, string selectedText)
    {
        FlushDispatcher();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item);
        Assert.IsNotNull(row, "The matching row must be realized before selecting the match.");
        var textBox = FindTextBoxes(row).Single(box => box.Name == (columnIndex == 0 ? "IdColumnEditor" : "TextColumnEditor"));
        Assert.AreEqual(start, textBox.SelectionStart);
        Assert.AreEqual(selectedText, textBox.SelectedText, "Search must highlight the matched characters in the inline editor.");
        Assert.IsTrue(textBox.IsInactiveSelectionHighlightEnabled, "The match must remain visible while the search input has focus.");
    }

    private static IEnumerable<TextBox> FindTextBoxes(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox textBox)
            {
                yield return textBox;
            }
            foreach (TextBox descendant in FindTextBoxes(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PressEnter(TextBox searchBox) => searchBox.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(searchBox)!, Environment.TickCount, Key.Enter)
    {
        RoutedEvent = Keyboard.KeyUpEvent
    });

    private static void FlushDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
