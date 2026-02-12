using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using mqttMultimeter.Common;
using mqttMultimeter.Controls;
using mqttMultimeter.Extensions;
using mqttMultimeter.Pages.Inflight.Export;
using mqttMultimeter.Services.Mqtt;
using MQTTnet;
using ReactiveUI;

namespace mqttMultimeter.Pages.Inflight;

public sealed class InflightPageViewModel : BasePageViewModel
{
    readonly InflightPageItemExportService _exportService;
    readonly ILogger<InflightPageViewModel> _logger;
    readonly ReadOnlyObservableCollection<InflightPageItemViewModel> _items;
    readonly SourceList<InflightPageItemViewModel> _itemsSource = new();
    readonly MqttClientService _mqttClientService;

    CompositeDisposable _streamCleanup = new();
    long _counter;

    string? _filterText;
    bool _isRecordingEnabled;

    public InflightPageViewModel(
        MqttClientService mqttClientService, 
        InflightPageItemExportService exportService,
        ILogger<InflightPageViewModel> logger)
    {
        _mqttClientService = mqttClientService ?? throw new ArgumentNullException(nameof(mqttClientService));
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _logger = logger;

        var filter = this
            .WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(800))
            .Select(BuildFilter);

        _itemsSource.Connect() 
            .Filter(filter)
            .Sort(SortExpressionComparer<InflightPageItemViewModel>.Ascending(t => t.Number))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _items)
            .Subscribe();

        // Subscribe to session events for reactive stream lifecycle
        _mqttClientService.MessageStreamConnected += OnMessageStreamConnected;
        _mqttClientService.MessageStreamDisconnected += OnMessageStreamDisconnected;

        // Start recording by default
        IsRecordingEnabled = true;
    }

    public event Action<InflightPageItemViewModel>? RepeatMessageRequested;

    public string? FilterText
    {
        get => _filterText;
        set => this.RaiseAndSetIfChanged(ref _filterText, value);
    }

    public bool IsRecordingEnabled
    {
        get => _isRecordingEnabled;
        set => this.RaiseAndSetIfChanged(ref _isRecordingEnabled, value);
    }

    public ReadOnlyObservableCollection<InflightPageItemViewModel> Items => _items;

    public void AppendMessage(MqttApplicationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var newItem = CreateItemViewModel(message);
 
        _itemsSource.Add(newItem);
        if (_itemsSource.Count > _mqttClientService.MaxUiItems)
        {
            _itemsSource.RemoveRange(0, Math.Min(_mqttClientService.TrimBatchSize, _itemsSource.Count));
        }
    }

    public void ClearItems()
    {
        _itemsSource.Clear();
    }

    public Task ExportItems(string path)
    {
        return _exportService.Export(this, path);
    }

    public Task ImportItems(string path)
    {
        if (!File.Exists(path))
        {
            return Task.CompletedTask;
        }

        return _exportService.Import(this, path);
    }

    Func<InflightPageItemViewModel, bool> BuildFilter(string? searchText)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return t => true;
        }

        return t => t.Topic.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    InflightPageItemViewModel CreateItemViewModel(MqttApplicationMessage applicationMessage)
    {
        var counter = Interlocked.Increment(ref _counter);

        var itemViewModel = InflightPageItemViewModelFactory.Create(applicationMessage, counter);
        // TODO: Consider using weak event handlers or another approach to avoid potential memory leaks when subscribing to events on the item view models.
        // Be careful with event handlers on the item view models.
        // They can easily capture the whole list item and prevent it from being garbage collected, which can lead to memory leaks if not handled properly.
        // Always ensure that event handlers are unsubscribed when the item is removed from the list or when the page is disposed.
        itemViewModel.RepeatMessageRequested += (_, _) =>
        {
            RepeatApplicationMessage(itemViewModel);
        };

        itemViewModel.DeleteRetainedMessageRequested += OnDeleteRetainedMessageRequested;

        return itemViewModel;
    }

    void InsertItemBatch(IList<InflightPageItemViewModel> batch, int maxUiItems, int trimBatchSize)
    {
        if (!_isRecordingEnabled)
        {
            return;
        }

        try
        {
            _itemsSource.AddRangeAndTrim(batch, maxUiItems, trimBatchSize);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while inserting received MQTT message batch.");
        }
    }

    void OnMessageStreamConnected(StreamConnectedEventArgs<MqttApplicationMessageReceivedEventArgs> args)
    {
        _streamCleanup.Dispose();
        _streamCleanup = new CompositeDisposable();

        var subscription = args.Stream
            .Where(e => IsRecordingEnabled)
            .Select(e => CreateItemViewModel(e.ApplicationMessage))  // transform on stream thread
            .Buffer(TimeSpan.FromMilliseconds(args.BufferMs))
            .Where(batch => batch.Count > 0)
            .ObserveOn(RxApp.MainThreadScheduler)                   // only the insert hits the UI thread
            .Subscribe(
                batch => InsertItemBatch(batch, args.MaxUiItems, args.TrimBatchSize),
                ex => _logger.LogError(ex, "Error in message stream."));

        _streamCleanup.Add(subscription);
    }

    void OnMessageStreamDisconnected()
    {
        _streamCleanup.Dispose();
        _streamCleanup = new CompositeDisposable();
    }

    void OnDeleteRetainedMessageRequested(object? sender, EventArgs e)
    {
        var item = (InflightPageItemViewModel)sender!;
        OverlayContent = ProgressIndicatorViewModel.Create($"Deleting retained message...\r\n\r\n{item.Topic}");

        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                // To delete a retained message it is important to set the body to an empty
                // one and setting the retain flag to _true_ as well!
                var message = new MqttApplicationMessageBuilder().WithTopic(item.Topic)
                    .WithQualityOfServiceLevel(item.QualityOfServiceLevel)
                    .WithPayload(ArraySegment<byte>.Empty)
                    .WithRetainFlag()
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

    void RepeatApplicationMessage(InflightPageItemViewModel item)
    {
        RepeatMessageRequested?.Invoke(item);
    }
    
    public void StartStopRecording()
    {
        IsRecordingEnabled = !IsRecordingEnabled;
    }
}