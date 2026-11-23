using System.Diagnostics;
using System.Windows;
using EasyLive.Core;

namespace EasyLive.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _vm.RefreshTargets();

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, _vm.HelpText, "Where do I find my stream key?", MessageBoxButton.OK, MessageBoxImage.Information);
        try { Process.Start(new ProcessStartInfo(_vm.HelpUrl) { UseShellExecute = true }); }
        catch { /* no browser: the message above is enough */ }
    }
}
