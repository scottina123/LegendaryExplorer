using System;
using System.Reflection;
using System.Windows;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.MainWindow
{
    /// <summary>
    /// Interaction logic for About.xaml
    /// </summary>
    public partial class About : Window
    {
        public About()
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
        }
    }
}
