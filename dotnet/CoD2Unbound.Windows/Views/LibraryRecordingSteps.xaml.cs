using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows.Views;

public sealed partial class LibraryRecordingSteps : UserControl
{
    public LibraryRecordingSteps()
    {
        InitializeComponent();
        if (new global::Windows.UI.ViewManagement.UISettings().TextScaleFactor <= 1.2)
            return;
        foreach (var row in Instructions.Children.OfType<Grid>())
        {
            row.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            foreach (var description in row.Children.OfType<TextBlock>().Where(text => Grid.GetColumn(text) == 1))
                description.TextWrapping = TextWrapping.Wrap;
            row.Padding = new Thickness(20, 6, 20, 6);
        }
    }
}
