using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using IbkrManager.App.ViewModels;

namespace IbkrManager.App.Views;

public partial class AccountsWindow : Window
{
    public AccountsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is AccountsViewModel viewModel)
        {
            viewModel.CloseRequested += (_, saved) => Close(saved);
        }
    }
}
