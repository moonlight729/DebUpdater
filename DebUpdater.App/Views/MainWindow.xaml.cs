using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using DebUpdater.App.Services;
using DebUpdater.App.ViewModels;

namespace DebUpdater.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _viewModel = new MainViewModel(ToolSettingsStore.Load(settingsPath), settingsPath);
        DataContext = _viewModel;

        ((INotifyCollectionChanged)_viewModel.Logs).CollectionChanged += OnLogsCollectionChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PasswordBox.Password = _viewModel.Password;

        // 打开即自动探测设备，不需要手工填 IP；未找到会自动重试，方便先开软件再上电。
        await _viewModel.ScanDevicesAsync(autoRetry: true);
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordBox.Password;

    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.SaveSettings();

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogListBox.Items.Count == 0)
        {
            return;
        }

        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
            paths.Length == 0)
        {
            return;
        }

        var path = paths[0];
        if (File.Exists(path))
        {
            path = Path.GetDirectoryName(path) ?? path;
        }

        if (Directory.Exists(path))
        {
            _viewModel.LoadPackages(path);
        }

        e.Handled = true;
    }
}
