using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace mqttMultimeter.Main;

public sealed partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    void OnActivatePageRequested(object? sender, EventArgs e)
    {
        var sidebar = this.FindControl<TabControl>("Sidebar");

        if (sidebar == null)
        {
            return;
        }

        foreach (TabItem? tabItem in sidebar.Items)
        {
            if (tabItem == null)
            {
                continue;
            }

            if (ReferenceEquals(sender, tabItem.Content))
            {
                sidebar.SelectedItem = tabItem;
            }
        }
    }

    void OnDataContextChanged(object? sender, EventArgs e)
    {
        var viewModel = (MainViewModel?)DataContext;

        if (viewModel == null)
        {
            return;
        }

        viewModel.ActivatePageRequested += OnActivatePageRequested;
    }

    async void OnConnectionToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var viewModel = (MainViewModel?)DataContext;
        if (viewModel == null)
        {
            return;
        }

        await viewModel.ToggleConnectionAsync().ConfigureAwait(true);

        if (sender is ToggleButton toggleButton)
        {
            toggleButton.IsChecked = viewModel.ConnectionPage.IsConnected;
        }
    }

    void OnUpdateAvailableNotificationPressed(object? _, PointerPressedEventArgs __)
    {
        ((MainViewModel)DataContext!).InfoPage.OpenReleasesUrl();
    }
}