using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace mqttMultimeter.Pages.TopicExplorer;

public sealed partial class TopicExplorerPageView : UserControl
{
    Grid? _detailOverlay;
    ContentPresenter? _detailContent;
    TopicExplorerPageViewModel? _currentVm;

    public TopicExplorerPageView()
    {
        InitializeComponent();
    }

    void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _detailOverlay = this.FindControl<Grid>("PART_DetailOverlay");
        _detailContent = this.FindControl<ContentPresenter>("PART_DetailContent");

        WireViewModel(DataContext as TopicExplorerPageViewModel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        WireViewModel(DataContext as TopicExplorerPageViewModel);
    }

    void WireViewModel(TopicExplorerPageViewModel? vm)
    {
        if (_currentVm != null)
        {
            _currentVm.PropertyChanged -= OnVmPropertyChanged;
        }

        _currentVm = vm;

        if (_currentVm != null)
        {
            _currentVm.PropertyChanged += OnVmPropertyChanged;
        }

        UpdateDetailPanel(_currentVm?.SelectedItem);
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TopicExplorerPageViewModel.SelectedItem))
        {
            UpdateDetailPanel(_currentVm?.SelectedItem);
        }
    }

    void UpdateDetailPanel(TopicExplorerItemViewModel? item)
    {
        if (_detailOverlay != null)
        {
            _detailOverlay.IsVisible = item == null;
        }

        if (_detailContent != null)
        {
            _detailContent.Content = item;
        }
    }
}