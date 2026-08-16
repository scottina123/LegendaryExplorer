using System.Windows;
using System.Windows.Controls;
using LE1GalaxyMapEditor.Services;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Controls;

public partial class StrRefLookupControl : UserControl
{
    public static readonly DependencyProperty StringRefTextProperty = DependencyProperty.Register(
        nameof(StringRefText),
        typeof(string),
        typeof(StrRefLookupControl),
        new PropertyMetadata(string.Empty, PresentationPropertyChanged));

    public static readonly DependencyProperty LocaleProperty = DependencyProperty.Register(
        nameof(Locale),
        typeof(MELocalization),
        typeof(StrRefLookupControl),
        new PropertyMetadata(MELocalization.INT, PresentationPropertyChanged));

    public static readonly DependencyProperty TlkServiceProperty = DependencyProperty.Register(
        nameof(TlkService),
        typeof(GalaxyMapTlkService),
        typeof(StrRefLookupControl),
        new PropertyMetadata(null, PresentationPropertyChanged));

    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(
        nameof(Presentation),
        typeof(GalaxyMapStrRefPresentation),
        typeof(StrRefLookupControl),
        new PropertyMetadata(null, PresentationPropertyChanged));

    public StrRefLookupControl()
    {
        InitializeComponent();
        UpdatePresentation();
    }

    public string StringRefText
    {
        get => (string)GetValue(StringRefTextProperty);
        set => SetValue(StringRefTextProperty, value);
    }

    public MELocalization Locale
    {
        get => (MELocalization)GetValue(LocaleProperty);
        set => SetValue(LocaleProperty, value);
    }

    public GalaxyMapTlkService? TlkService
    {
        get => (GalaxyMapTlkService?)GetValue(TlkServiceProperty);
        set => SetValue(TlkServiceProperty, value);
    }

    public GalaxyMapStrRefPresentation? Presentation
    {
        get => (GalaxyMapStrRefPresentation?)GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    private static void PresentationPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        => ((StrRefLookupControl)dependencyObject).UpdatePresentation();

    private void UpdatePresentation()
    {
        if (StateText is null)
        {
            return;
        }
        var presentation = Presentation ?? GalaxyMapStrRefPresenter.Present(TlkService, Locale, StringRefText);
        StateText.Text = presentation.State;
        ValueText.Text = presentation.Text;
        ContextText.Text = presentation.Context;
    }
}
