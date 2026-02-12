using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using mqttMultimeter.Common;
using mqttMultimeter.Extensions;
using mqttMultimeter.Services.Mqtt;
using MQTTnet.Diagnostics.Logger;
using ReactiveUI;

namespace mqttMultimeter.Pages.Log;

public sealed class LogPageViewModel : BasePageViewModel
{
    bool _isRecordingEnabled;
    readonly MqttClientService _mqttClientService;
    readonly ReadOnlyObservableCollection<LogItemViewModel> _items;
    readonly SourceList<LogItemViewModel> _itemsSource = new();
    CompositeDisposable _streamCleanup = new();

    public LogPageViewModel(MqttClientService mqttClientService)
    {
        _mqttClientService = mqttClientService ?? throw new ArgumentNullException(nameof(mqttClientService));

        _itemsSource.Connect()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _items)
            .Subscribe();

        // Subscribe to session events for reactive stream lifecycle
        _mqttClientService.LogStreamConnected += OnLogStreamConnected;
        _mqttClientService.LogStreamDisconnected += OnLogStreamDisconnected;
    }

    public bool IsRecordingEnabled
    {
        get => _isRecordingEnabled;
        private set => this.RaiseAndSetIfChanged(ref _isRecordingEnabled, value);
    }

    public ReadOnlyObservableCollection<LogItemViewModel> Items => _items;
    
    public void StartStopRecording()
    {
        IsRecordingEnabled = !IsRecordingEnabled;
    }
    
    public void ClearItems()
    {
        _itemsSource.Clear();
    }

    static LogItemViewModel MapLogEvent(MqttNetLogMessagePublishedEventArgs eventArgs) => new()
    {
        IsVerbose = eventArgs.LogMessage.Level == MqttNetLogLevel.Verbose,
        IsInformation = eventArgs.LogMessage.Level == MqttNetLogLevel.Info,
        IsWarning = eventArgs.LogMessage.Level == MqttNetLogLevel.Warning,
        IsError = eventArgs.LogMessage.Level == MqttNetLogLevel.Error,
        Timestamp = eventArgs.LogMessage.Timestamp.ToString("HH:mm:ss.fff"),
        Source = eventArgs.LogMessage.Source,
        Message = eventArgs.LogMessage.Message,
        Level = eventArgs.LogMessage.Level.ToString(),
        Exception = eventArgs.LogMessage.Exception?.ToString()
    };

    void InsertLogBatch(IList<LogItemViewModel> batch, int maxUiItems)
    {
        _itemsSource.AddRangeAndTrim(batch, maxUiItems);
    }

    void OnLogStreamConnected(StreamConnectedEventArgs<MqttNetLogMessagePublishedEventArgs> args)
    {
        _streamCleanup.Dispose();
        _streamCleanup = new CompositeDisposable();

        var subscription = args.Stream
            .Where(_ => IsRecordingEnabled)
            .Select(MapLogEvent)                        // transform on stream thread
            .Buffer(TimeSpan.FromMilliseconds(args.BufferMs))
            .Where(batch => batch.Count > 0)
            .ObserveOn(RxApp.MainThreadScheduler)       // only the insert hits the UI thread
            .Subscribe(batch => InsertLogBatch(batch, args.MaxUiItems));

        _streamCleanup.Add(subscription);
    }

    void OnLogStreamDisconnected()
    {
        _streamCleanup.Dispose();
        _streamCleanup = new CompositeDisposable();
    }
}