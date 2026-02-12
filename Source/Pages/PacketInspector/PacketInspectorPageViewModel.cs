using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using mqttMultimeter.Common;
using mqttMultimeter.Extensions;
using mqttMultimeter.Services.Mqtt;
using MQTTnet.Diagnostics.PacketInspection;
using ReactiveUI;

namespace mqttMultimeter.Pages.PacketInspector;

public sealed class PacketInspectorPageViewModel : BasePageViewModel
{
    readonly MqttClientService _mqttClientService;
    readonly SourceList<PacketViewModel> _packetsSource = new();
    readonly ReadOnlyObservableCollection<PacketViewModel> _packets;

    CompositeDisposable _streamCleanup = new();
    bool _isRecordingEnabled;
    int _number;
    PacketViewModel? _selectedPacket;

    public PacketInspectorPageViewModel(MqttClientService mqttClientService)
    {
        _mqttClientService = mqttClientService;
        ArgumentNullException.ThrowIfNull(mqttClientService);
        
        // Use DynamicData to batch updates before hitting the UI thread.
        // This prevents flooding the dispatcher queue during high-throughput scenarios.
        _packetsSource.Connect()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _packets)
            .Subscribe();

        // Subscribe to session events for reactive stream lifecycle
        _mqttClientService.PacketStreamConnected += OnPacketStreamConnected;
        _mqttClientService.PacketStreamDisconnected += OnPacketStreamDisconnected;
    }

    public bool IsRecordingEnabled
    {
        get => _isRecordingEnabled;
        private set => this.RaiseAndSetIfChanged(ref _isRecordingEnabled, value);
    }

    public ReadOnlyObservableCollection<PacketViewModel> Packets => _packets;

    public PacketViewModel? SelectedPacket
    {
        get => _selectedPacket;
        set => this.RaiseAndSetIfChanged(ref _selectedPacket, value);
    }

    public void StartStopRecording()
    {
        try
        {
            if (IsRecordingEnabled)
            {
                _mqttClientService.StopInspectingPackets();
            }
            else
            {
                _mqttClientService.StartInspectingPackets();
            }
            IsRecordingEnabled = !IsRecordingEnabled;
        }
        catch (Exception e)
        {
            App.ShowException(e);
        }
    }

    public void ClearItems()
    {
        _number = 0;
        _packetsSource.Clear();
    }

    static readonly string[] ControlPacketTypes =
    [
        "UNKNOWN", // 0
        "CONNECT (01)",
        "CONNACK (02)",
        "PUBLISH (03)",
        "PUBACK (04)",
        "PUBREC (05)",
        "PUBREL (06)",
        "PUBCOMP (07)",
        "SUBSCRIBE (08)",
        "SUBACK (09)",
        "UNSUBSCRIBE (10)",
        "UNSUBACK (11)",
        "PINGREQ (12)",
        "PINGRESP (13)",
        "DISCONNECT (14)",
        "AUTH (15)"
    ];
    
    static string GetControlPacketType(byte data)
    {
        int controlType = data >> 4;
        return (controlType >= 0 && controlType < ControlPacketTypes.Length)
            ? ControlPacketTypes[controlType]
            : "UNKNOWN";
    }

    PacketViewModel MapPacket(InspectMqttPacketEventArgs args) => new()
    {
        Number = _number++,
        Type = GetControlPacketType(args.Buffer[0]),
        Data = args.Buffer,
        Length = args.Buffer.Length,
        IsInbound = args.Direction == MqttPacketFlowDirection.Inbound
    };

    void InsertPacketBatch(IList<PacketViewModel> batch, int maxUiItems, int trimBatchSize)
    {
        if (!_isRecordingEnabled)
        {
            return;
        }

        _packetsSource.AddRangeAndTrim(batch, maxUiItems, trimBatchSize);
    }

    void OnPacketStreamConnected(StreamConnectedEventArgs<InspectMqttPacketEventArgs> args)
    {
        _streamCleanup.Dispose();
        _streamCleanup = new CompositeDisposable();

        var subscription = args.Stream
            .Select(MapPacket)                          // transform on stream thread
            .Buffer(TimeSpan.FromMilliseconds(args.BufferMs))
            .Where(batch => batch.Count > 0)
            .ObserveOn(RxApp.MainThreadScheduler)       // only the insert hits the UI thread
            .Subscribe(batch => InsertPacketBatch(batch, args.MaxUiItems, args.TrimBatchSize));

        _streamCleanup.Add(subscription);
    }

    void OnPacketStreamDisconnected()
    {
        _streamCleanup.Dispose();
        _streamCleanup = new CompositeDisposable();
    }
}