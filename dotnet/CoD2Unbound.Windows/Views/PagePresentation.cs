using CoD2Unbound.Core;
using Microsoft.UI.Xaml;

namespace CoD2Unbound.Windows.Views;

internal static class PagePresentation
{
    internal static Visibility IsVisible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    internal static string DataSummary(SetupDataChoice choice) => choice.State switch
    {
        SetupDataState.Found => "Game data found",
        SetupDataState.Damaged or SetupDataState.UnfinishedSteam => "Game data is incomplete",
        _ => "Game data not found"
    };
}
