using System;
using System.Reflection;
using System.Windows;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.MainWindow
{
    /// <summary>
    /// Interaction logic for Help.xaml
    /// </summary>
    public partial class Help : Window
    {
        public Help()
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
        }
    }
}
