using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace mqttMultimeter.Pages.TopicExplorer;

public sealed partial class TopicExplorerTreeNodeView : UserControl
{
    TopicExplorerTreeNodeViewModel? _previousViewModel;

    public TopicExplorerTreeNodeView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from the previous view model to avoid leaks during
        // virtualization recycling.
        if (_previousViewModel is not null)
        {
            _previousViewModel.MessagesChanged -= OnMessagesChanged;
        }

        if (DataContext is TopicExplorerTreeNodeViewModel viewModel)
        {
            _previousViewModel = viewModel;
            viewModel.MessagesChanged += OnMessagesChanged;
        }
        else
        {
            _previousViewModel = null;
        }
    }

    void OnMessagesChanged(object? sender, EventArgs eventArgs)
    {
        if (DataContext is not TopicExplorerTreeNodeViewModel viewModel)
        {
            return;
        }

        if (!viewModel.OwnerPage.HighlightChanges)
        {
            return;
        }

        // Walk up the visual tree to find the TreeDataGridRow ancestor
        // (replaces the previous TreeViewItem lookup after the TreeDataGrid migration).
        Control? row = this;
        while (row is not TreeDataGridRow)
        {
            row = row.GetVisualParent<Control>();
            if (row == null)
            {
                break;
            }
        }

        if (row != null)
        {
            row.Classes.Add("highlight");

            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5));

                row.Classes.Remove("highlight");
            });
        }
    }
}