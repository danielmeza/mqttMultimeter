using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using mqttMultimeter.Common;
using mqttMultimeter.Controls;
using mqttMultimeter.Pages.Inflight;
using mqttMultimeter.Services.Mqtt;
using MQTTnet;
using ReactiveUI;

namespace mqttMultimeter.Pages.TopicExplorer;

public sealed partial class TopicExplorerPageViewModel : BasePageViewModel
{
    readonly MqttClientService _mqttClientService;
    readonly ILogger<TopicExplorerPageViewModel> _logger;

    /// <summary>
    /// O(1) root-level segment lookup.  Thread-safe for stream-thread reads.
    /// Each child node carries its own <see cref="TopicExplorerTreeNodeViewModel.ChildLookup"/>
    /// forming a trie so we never need a global flat dictionary.
    /// </summary>
    readonly ConcurrentDictionary<string, TopicExplorerTreeNodeViewModel> _rootLookup = new();
    readonly SourceList<TopicExplorerTreeNodeViewModel> _rootNodes = new();

    CompositeDisposable _streamCleanup = new();
    bool _highlightChanges = true;
    bool _isRecordingEnabled;

    TopicExplorerItemViewModel? _selectedItem;
    TopicExplorerTreeNodeViewModel? _selectedNode;
    bool _trackLatestMessageOnly;
    HierarchicalTreeDataGridSource<TopicExplorerTreeNodeViewModel> _treeSource;

    public TopicExplorerPageViewModel(
        MqttClientService mqttClientService,
        ILogger<TopicExplorerPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(mqttClientService);

        _mqttClientService = mqttClientService;
        _logger = logger;

        _rootNodes.Connect()
            .Sort(SortExpressionComparer<TopicExplorerTreeNodeViewModel>.Ascending(t => t.Name))
            .Bind(out var bindingData)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasNodes)));

        Nodes = bindingData;
        _treeSource = BuildTreeSource();

        // Subscribe to session events for reactive stream lifecycle
        _mqttClientService.MessageStreamConnected += OnMessageStreamConnected;
        _mqttClientService.MessageStreamDisconnected += OnMessageStreamDisconnected;

        // Start recording by default
        IsRecordingEnabled = true;
    }

    public event Action<InflightPageItemViewModel>? RepeatMessageRequested;

    public bool HasNodes => Nodes.Count > 0;

    public bool HighlightChanges
    {
        get => _highlightChanges;
        set => this.RaiseAndSetIfChanged(ref _highlightChanges, value);
    }

    public bool IsRecordingEnabled
    {
        get => _isRecordingEnabled;
        private set => this.RaiseAndSetIfChanged(ref _isRecordingEnabled, value);
    }

    public ReadOnlyObservableCollection<TopicExplorerTreeNodeViewModel> Nodes { get; }

    /// <summary>
    /// The currently selected item's view model, or null when nothing is selected.
    /// Updated whenever <see cref="SelectedNode"/> changes.
    /// </summary>
    public TopicExplorerItemViewModel? SelectedItem
    {
        get => _selectedItem;
        private set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    public TopicExplorerTreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNode, value);
            SelectedItem = _selectedNode?.Item;
        }
    }

    /// <summary>
    /// The HierarchicalTreeDataGridSource backing the TreeDataGrid control.
    /// Rebuilt when root nodes change so the virtualized tree stays in sync.
    /// </summary>
    public HierarchicalTreeDataGridSource<TopicExplorerTreeNodeViewModel> TreeSource
    {
        get => _treeSource;
        private set => this.RaiseAndSetIfChanged(ref _treeSource, value);
    }

    public bool TrackLatestMessageOnly
    {
        get => _trackLatestMessageOnly;
        set => this.RaiseAndSetIfChanged(ref _trackLatestMessageOnly, value);
    }

    public void Clear()
    {
        _rootNodes.Clear();
        _rootLookup.Clear();

        SelectedNode = null;
    }

    public void CollapseAll()
    {
        foreach (var node in Nodes)
        {
            SetExpandedState(node, false);
        }
    }

    public void DeleteRetainedMessage(InflightPageItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        OverlayContent = ProgressIndicatorViewModel.Create($"Deleting retained message...\r\n\r\n{item.Topic}");

        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var message = new MqttApplicationMessageBuilder().WithTopic(item.Topic)
                    .WithQualityOfServiceLevel(item.QualityOfServiceLevel)
                    .WithPayload(ArraySegment<byte>.Empty)
                    .Build();

                await _mqttClientService.Publish(message, CancellationToken.None);
            }
            catch (Exception exception)
            {
                App.ShowException(exception);
            }
            finally
            {
                OverlayContent = null;
            }
        });
    }

    public void ExpandSelectedTree()
    {
        if (_selectedNode == null)
        {
            return;
        }

        SetExpandedState(_selectedNode, true);
    }

    public void RepeatMessage(InflightPageItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        RepeatMessageRequested?.Invoke(item);
    }

    /// <summary>
    /// Walks the topic path through the trie (root lookup → child lookups)
    /// creating missing nodes on the fly.  New nodes are staged in
    /// <paramref name="pendingAdds"/> for deferred <c>Edit()</c> on the UI thread.
    /// Each node's <see cref="TopicExplorerTreeNodeViewModel.ChildLookup"/> is a
    /// small ConcurrentDictionary containing only its direct children, so every
    /// lookup is O(1) against a tiny dictionary.
    /// </summary>
    TopicExplorerTreeNodeViewModel ResolveOrCreateLeaf(
        string[] segments,
        Dictionary<SourceList<TopicExplorerTreeNodeViewModel>, List<TopicExplorerTreeNodeViewModel>> pendingAdds)
    {
        TopicExplorerTreeNodeViewModel? current = null;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            // Pick the lookup for this depth: root-level or parent's children.
            var lookup = current?.ChildLookup ?? _rootLookup;

            if (!lookup.TryGetValue(segment, out var child))
            {
                child = new TopicExplorerTreeNodeViewModel(segment, current, this);

                // Register in the trie.
                lookup[segment] = child;

                // Stage for deferred bulk-add to the SourceList.
                var target = current?.NodesSource ?? _rootNodes;
                if (!pendingAdds.TryGetValue(target, out var list))
                {
                    list = [];
                    pendingAdds[target] = list;
                }

                list.Add(child);
            }

            current = child;
        }

        return current ?? throw new InvalidOperationException("Empty topic path.");
    }

    /// <summary>
    /// Phase 1 — runs on the stream thread.
    /// Groups messages by first topic segment for cache-friendly trie traversal,
    /// then resolves / creates nodes via
    /// <see cref="ResolveOrCreateLeaf"/>.
    /// </summary>
    (Dictionary<SourceList<TopicExplorerTreeNodeViewModel>, List<TopicExplorerTreeNodeViewModel>> PendingAdds,
     List<(TopicExplorerTreeNodeViewModel Node, MqttApplicationMessage Message)> MessageUpdates)
    PrepareBatchInserts(IList<MqttApplicationMessage> batch)
    {
        var pendingAdds = new Dictionary<SourceList<TopicExplorerTreeNodeViewModel>, List<TopicExplorerTreeNodeViewModel>>();
        var messageUpdates = new List<(TopicExplorerTreeNodeViewModel, MqttApplicationMessage)>(batch.Count);

        if (!_isRecordingEnabled)
        {
            return (pendingAdds, messageUpdates);
        }

        // Group by first segment so all topics under the same root are
        // processed together, maximising trie-walk cache locality.
        var grouped = batch
            .GroupBy(m =>
            {
                var sep = m.Topic.IndexOf('/');
                return sep < 0 ? m.Topic : m.Topic[..sep];
            });

        foreach (var group in grouped)
        {
            foreach (var message in group)
            {
                try
                {
                    var segments = message.Topic.Split('/');
                    var leaf = ResolveOrCreateLeaf(segments, pendingAdds);
                    messageUpdates.Add((leaf, message));
                }
                catch (Exception exception)
                {
                    _logger.LogInsertMessageException(exception);
                }
            }
        }

        return (pendingAdds, messageUpdates);
    }

    /// <summary>
    /// Phase 2 — runs on the UI thread.
    /// Flushes deferred SourceList adds via <c>Edit()</c> and applies
    /// message data to target nodes.
    /// </summary>
    void FlushBatchInserts(
        (Dictionary<SourceList<TopicExplorerTreeNodeViewModel>, List<TopicExplorerTreeNodeViewModel>> PendingAdds,
         List<(TopicExplorerTreeNodeViewModel Node, MqttApplicationMessage Message)> MessageUpdates) result)
    {
        // Flush all pending adds with a single changeset per SourceList.
        foreach (var (sourceList, newNodes) in result.PendingAdds)
        {
            sourceList.Edit(inner => inner.AddRange(newNodes));
        }

        // Apply message data to nodes.
        var trackLatest = _trackLatestMessageOnly;
        foreach (var (node, message) in result.MessageUpdates)
        {
            if (trackLatest)
            {
                node.Clear();
            }

            node.AddMessage(message);
        }
    }

    HierarchicalTreeDataGridSource<TopicExplorerTreeNodeViewModel> BuildTreeSource()
    {
        var source = new HierarchicalTreeDataGridSource<TopicExplorerTreeNodeViewModel>(Nodes)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<TopicExplorerTreeNodeViewModel>(
                    new TemplateColumn<TopicExplorerTreeNodeViewModel>("Topic", "TopicExplorerNodeCell"),
                    node => node.Nodes),
            },
        };

        source.RowSelection!.SelectionChanged += OnTreeSelectionChanged;
        return source;
    }

    void RebuildTreeSource()
    {
        if (_treeSource is not null)
        {
            _treeSource.RowSelection!.SelectionChanged -= OnTreeSelectionChanged;
            _treeSource.Dispose();
        }

        TreeSource = BuildTreeSource();
    }

    void OnTreeSelectionChanged(object? sender, TreeSelectionModelSelectionChangedEventArgs<TopicExplorerTreeNodeViewModel> e)
    {
        SelectedNode = _treeSource.RowSelection?.SelectedItem;
    }

    void OnMessageStreamConnected(StreamConnectedEventArgs<MqttApplicationMessageReceivedEventArgs> args)
    {
        _streamCleanup.Dispose();
        _streamCleanup = new CompositeDisposable();

        // Buffer messages on the stream thread, run Phase 1 (tree walk + node
        // creation) on the stream thread, then Phase 2 (SourceList commits +
        // message application) on the UI thread.
        var subscription = args.Stream
            .Select(e => e.ApplicationMessage)
            .Buffer(TimeSpan.FromMilliseconds(args.BufferMs))
            .Where(batch => batch.Count > 0)
            .Select(PrepareBatchInserts)                // Phase 1: stream thread
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(
                FlushBatchInserts,                      // Phase 2: UI thread
                ex => _logger.LogInsertMessageException(ex));

        _streamCleanup.Add(subscription);
    }

    void OnMessageStreamDisconnected()
    {
        _streamCleanup.Dispose();
        _streamCleanup = new CompositeDisposable();
    }

    static void SetExpandedState(TopicExplorerTreeNodeViewModel node, bool value)
    {
        node.IsExpanded = value;

        foreach (var childNode in node.Nodes)
        {
            SetExpandedState(childNode, value);
        }
    }
    
    public void StartStopRecording()
    {
        IsRecordingEnabled = !IsRecordingEnabled;
    }
}

internal static partial class TopicExplorerPageViewModelLogExpressions
{
    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "Error while inserting MQTT message into topic explorer tree.")]
    public static partial void LogInsertMessageException(this ILogger<TopicExplorerPageViewModel> logger, Exception exception);
}