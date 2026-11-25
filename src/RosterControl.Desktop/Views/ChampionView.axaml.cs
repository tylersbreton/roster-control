using Avalonia.Controls;
using Avalonia.Input;
using RosterControl.Desktop.ViewModels;

namespace RosterControl.Desktop.Views;

public partial class ChampionView : UserControl
{
    public ChampionView()
    {
        InitializeComponent();
    }

    private void OnTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ChampionDetailViewModel vm)
        {
            vm.AddTagCommand.Execute(null);
            e.Handled = true;
        }
    }
}
