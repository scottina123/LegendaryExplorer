using System;
using System.Windows;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Tools.PlotEditor.Dialogs
{
	public partial class AddMultipleStateEventElementsDialog
	{
		private const string DefaultContentText = "Add multiple state event elements";
		private const string DefaultHeaderText = "Specify the starting value and count.";

		public static readonly DependencyProperty ContentTextProperty = DependencyProperty.Register("ContentText", typeof(string),
			typeof(AddMultipleStateEventElementsDialog), new PropertyMetadata(default(string)));

		public static readonly DependencyProperty HeaderTextProperty = DependencyProperty.Register("HeaderText", typeof(string),
			typeof(AddMultipleStateEventElementsDialog), new PropertyMetadata(default(string)));

		public static readonly DependencyProperty StartingValueProperty = DependencyProperty.Register("StartingValue", typeof(int),
			typeof(AddMultipleStateEventElementsDialog), new PropertyMetadata(default(int)));

		public static readonly DependencyProperty CountProperty = DependencyProperty.Register("Count", typeof(int),
			typeof(AddMultipleStateEventElementsDialog), new PropertyMetadata(1));

		public AddMultipleStateEventElementsDialog()
		{
			InitializeComponent();
			CustomWindowChrome.ApplyCustomChrome(this);

			ContentText = DefaultContentText;
			HeaderText = DefaultHeaderText;
		}

		public string ContentText
		{
			get { return (string)GetValue(ContentTextProperty); }
			set { SetValue(ContentTextProperty, value); }
		}

		public string HeaderText
		{
			get { return (string)GetValue(HeaderTextProperty); }
			set { SetValue(HeaderTextProperty, value); }
		}

		public int StartingValue
		{
			get { return (int)GetValue(StartingValueProperty); }
			set { SetValue(StartingValueProperty, value); }
		}

		public int Count
		{
			get { return (int)GetValue(CountProperty); }
			set { SetValue(CountProperty, value); }
		}

		private void CancelButton_OnClick(object sender, RoutedEventArgs e)
		{
			DialogResult = false;
		}

		private void Dialog_OnContentRendered(object sender, EventArgs e)
		{
			StartValueSpinner.Focus();
		}

		private void OkButton_OnClick(object sender, RoutedEventArgs e)
		{
			DialogResult = true;
		}
	}
}
