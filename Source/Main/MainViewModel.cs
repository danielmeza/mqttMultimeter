using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using mqttMultimeter.Common;
using mqttMultimeter.Pages.Connection;
using mqttMultimeter.Pages.Inflight;
using mqttMultimeter.Pages.Info;
using mqttMultimeter.Pages.Log;
using mqttMultimeter.Pages.PacketInspector;
using mqttMultimeter.Pages.Publish;
using mqttMultimeter.Pages.Subscriptions;
using mqttMultimeter.Pages.TopicExplorer;
using mqttMultimeter.Services.Mqtt;
using MQTTnet;
using ReactiveUI;

namespace mqttMultimeter.Main;

public sealed class MainViewModel : BaseViewModel
{
    readonly MqttClientService _mqttClientService;

    DispatcherTimer? _counterTimer;
    long _receivedCounter;
    long _notifiedCounter;
    long _bufferedCounter;
    long _droppedCounter;
    bool _showDroppedCounter;
    bool _hasDroppedMessages;
    object? _overlayContent;

    public MainViewModel(ConnectionPageViewModel connectionPage,
        PublishPageViewModel publishPage,
        SubscriptionsPageViewModel subscriptionsPage,
        InflightPageViewModel inflightPage,
        TopicExplorerPageViewModel topicExplorerPage,
        PacketInspectorPageViewModel packetInspectorPage,
        InfoPageViewModel infoPage,
        LogPageViewModel logPage,
        MqttClientService mqttClientService)
    {
        _mqttClientService = mqttClientService ?? throw new ArgumentNullException(nameof(mqttClientService));

        ConnectionPage = AttachEvents(connectionPage);
        PublishPage = AttachEvents(publishPage);
        SubscriptionsPage = AttachEvents(subscriptionsPage);
        InflightPage = AttachEvents(inflightPage);
        TopicExplorerPage = AttachEvents(topicExplorerPage);
        PacketInspectorPage = AttachEvents(packetInspectorPage);
        InfoPage = AttachEvents(infoPage);
        LogPage = AttachEvents(logPage);

        InflightPage.RepeatMessageRequested += item => PublishPage.RepeatMessage(item);
        topicExplorerPage.RepeatMessageRequested += item => PublishPage.RepeatMessage(item);

        // Update counters at the same frequency as the message batch buffer.
        // The observable is created on connect and disposed on disconnect.
        _mqttClientService.MessageStreamConnected += OnCounterStreamConnected;
        _mqttClientService.MessageStreamDisconnected += OnCounterStreamDisconnected;
    }

    public event EventHandler? ActivatePageRequested;

    public ConnectionPageViewModel ConnectionPage { get; }

    public long ReceivedCounter
    {
        get => _receivedCounter;
        set => this.RaiseAndSetIfChanged(ref _receivedCounter, value);
    }

    public long NotifiedCounter
    {
        get => _notifiedCounter;
        set => this.RaiseAndSetIfChanged(ref _notifiedCounter, value);
    }

    public long BufferedCounter
    {
        get => _bufferedCounter;
        set => this.RaiseAndSetIfChanged(ref _bufferedCounter, value);
    }

    public long DroppedCounter
    {
        get => _droppedCounter;
        set => this.RaiseAndSetIfChanged(ref _droppedCounter, value);
    }

    public bool ShowDroppedCounter
    {
        get => _showDroppedCounter;
        set => this.RaiseAndSetIfChanged(ref _showDroppedCounter, value);
    }

    public bool HasDroppedMessages
    {
        get => _hasDroppedMessages;
        set => this.RaiseAndSetIfChanged(ref _hasDroppedMessages, value);
    }

    public InflightPageViewModel InflightPage { get; }

    public InfoPageViewModel InfoPage { get; }

    public LogPageViewModel LogPage { get; }

    public object? OverlayContent
    {
        get => _overlayContent;
        set => this.RaiseAndSetIfChanged(ref _overlayContent, value);
    }

    public PacketInspectorPageViewModel PacketInspectorPage { get; }

    public PublishPageViewModel PublishPage { get; }

    public SubscriptionsPageViewModel SubscriptionsPage { get; }

    public TopicExplorerPageViewModel TopicExplorerPage { get; }

    TPage AttachEvents<TPage>(TPage page) where TPage : BasePageViewModel
    {
        page.ActivationRequested += (_, __) => ActivatePageRequested?.Invoke(page, EventArgs.Empty);
        return page;
    }

    void OnCounterStreamConnected(StreamConnectedEventArgs<MqttApplicationMessageReceivedEventArgs> args)
    {
        if (_counterTimer is not null)
        {
            _counterTimer.Tick -= OnCounterTimerTick;
            _counterTimer.Stop();
        }

        _counterTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(args.CounterUpdateMs)
        };
        _counterTimer.Tick += OnCounterTimerTick;
        _counterTimer.Start();
    }

    void OnCounterStreamDisconnected()
    {
        if (_counterTimer is not null)
        {
            _counterTimer.Tick -= OnCounterTimerTick;
            _counterTimer.Stop();
            _counterTimer = null;
        }

        // Final update to flush the last counter values.
        UpdateCounter();
    }

    void OnCounterTimerTick(object? sender, EventArgs e) => UpdateCounter();

    void UpdateCounter()
    {
        ReceivedCounter = _mqttClientService.ReceivedMessagesCount;
        NotifiedCounter = _mqttClientService.NotifiedMessagesCount;
        BufferedCounter = _mqttClientService.BufferedMessagesCount;
        DroppedCounter = _mqttClientService.DroppedMessagesCount;
        HasDroppedMessages = DroppedCounter > 0;

        var selectedItem = ConnectionPage.Items.SelectedItem;
        if (selectedItem == null)
        {
            ShowDroppedCounter = false;
        }
        else
        {
            var options = selectedItem.MessageProcessingOptions;
            ShowDroppedCounter = options.UseBoundedChannel && options.SelectedFullMode.Value != System.Threading.Channels.BoundedChannelFullMode.Wait;
        }
    }

    public async Task ToggleConnectionAsync()
    {
        var selectedItem = ConnectionPage.Items.SelectedItem;
        if (selectedItem == null || ConnectionPage.IsConnecting)
        {
            return;
        }

        if (ConnectionPage.IsConnected)
        {
            await selectedItem.Disconnect().ConfigureAwait(true);
        }
        else
        {
            await selectedItem.Connect().ConfigureAwait(true);
        }
    }
}