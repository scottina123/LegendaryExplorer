using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Editing;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor.IDE
{
    internal class LEXCompletionWindow : CompletionWindow
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "toolTip")]
        private extern static ref ToolTip GetSetToolTip(CompletionWindow c);

        ToolTip ToolTip => GetSetToolTip(this);

        public LEXCompletionWindow(TextArea textArea) : base(textArea)
        {
            ToolTip.Background = SyntaxInfo.BackgroundBrush;
        }
    }
}
