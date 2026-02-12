using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace mqttMultimeter.Controls;

public sealed class AutoGrid : Grid
{
    public static readonly AttachedProperty<bool>
        IsNextRowProperty = AvaloniaProperty.RegisterAttached<AutoGrid, Interactive, bool>("IsNextRow", false, false, BindingMode.Default);

    bool _cellArrangementPending;

    public static bool GetIsNextRow(AvaloniaObject element)
    {
        return element.GetValue(IsNextRowProperty);
    }

    public static void SetIsNextRow(AvaloniaObject element, bool value)
    {
        element.SetValue(IsNextRowProperty, value);
    }

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);

        if (_cellArrangementPending)
        {
            return;
        }

        _cellArrangementPending = true;

        Dispatcher.UIThread.Post(
            state =>
            {
                var grid = (AutoGrid)state!;
                grid._cellArrangementPending = false;

                var currentRow = 0;
                var currentColumn = 0;

                foreach (var child in grid.Children)
                {
                    if (child is Control control)
                    {
                        if (GetIsNextRow(control))
                        {
                            currentRow++;
                            currentColumn = 0;
                        }

                        var childColumn = GetColumn(control);
                        if (childColumn > 0)
                        {
                            // This happens when there is an empty column and the next column is
                            // annotated with the target column.
                            currentColumn = childColumn;
                        }

                        SetRow(control, currentRow);
                        SetColumn(control, currentColumn);
                    }
                }

                if (grid.RowDefinitions.Count - 1 != currentRow)
                {
                    grid.RowDefinitions.Clear();
                    for (var i = 0; i <= currentRow; i++)
                    {
                        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    }
                }
            },
            this,
            DispatcherPriority.Render);
    }
}