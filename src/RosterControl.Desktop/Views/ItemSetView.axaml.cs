using Avalonia.Controls;
using Avalonia.Input;
using RosterControl.Desktop.ViewModels;

namespace RosterControl.Desktop.Views;

public partial class ItemSetView : UserControl
{
    public ItemSetView()
    {
        InitializeComponent();
    }

    private void OnBlockTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ItemBlockViewModel block && DataContext is ItemSetViewModel set)
            set.SelectBlockCommand.Execute(block);
    }
}
