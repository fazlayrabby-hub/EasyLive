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
        // Never taller than the screen (the content scrolls when it does not fit).
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 10);
        _vm = vm;
        DataContext = vm;
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // Same close path as before: closing the main window ends the app (OnExit stops the stream and rolls back an unverified key).
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _vm.RefreshTargets();

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, _vm.HelpText, "Where do I find my stream key?", MessageBoxButton.OK, MessageBoxImage.Information);
        try { Process.Start(new ProcessStartInfo(_vm.HelpUrl) { UseShellExecute = true }); }
        catch { /* no browser: the message above is enough */ }
    }
}


