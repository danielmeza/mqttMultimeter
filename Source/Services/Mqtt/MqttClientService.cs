using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using mqttMultimeter.Controls;
using mqttMultimeter.Pages.Connection;
using mqttMultimeter.Pages.Publish;
using mqttMultimeter.Pages.Subscriptions;
using MQTTnet;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Diagnostics.PacketInspection;
using MQTTnet.Exceptions;

namespace mqttMultimeter.Services.Mqtt;

public sealed class MqttClientService
{
    readonly ILogger<MqttClientService> _logger;
    readonly MqttNetEventLogger _mqttNetEventLogger = new();

    Channel<MqttApplicationMessageReceivedEventArgs>? _receivedMessagesChannel;
    Channel<InspectMqttPacketEventArgs>? _packetInspectionChannel;
    CancellationTokenSource? _channelCancellationTokenSource;
    Task? _receivedMessagesTask;
    Task? _packetInspectionTask;
    bool _waitForMessageChannel;
    bool _isInspectingPackets;
    int _messageProcessingDelayMs;
    int _packetInspectionDelayMs;

    // Reactive subjects for hot observable streams (one per session)
    Subject<MqttApplicationMessageReceivedEventArgs>? _messageSubject;
    Subject<InspectMqttPacketEventArgs>? _packetSubject;
    Subject<MqttNetLogMessagePublishedEventArgs>? _logSubject;
    IObservable<MqttApplicationMessageReceivedEventArgs>? _messageStream;
    IObservable<InspectMqttPacketEventArgs>? _packetStream;
    IObservable<MqttNetLogMessagePublishedEventArgs>? _logStream;

    IMqttClient? _mqttClient;
    long _receivedMessagesCount;
    long _enqueuedMessagesCount;
    long _notifiedMessagesCount;
    long _droppedMessagesCount;

    public MqttClientService(ILogger<MqttClientService> logger)
    {
        _logger = logger;
        _mqttNetEventLogger.LogMessagePublished += OnLogMessagePublished;
    }

    /// <summary>
    /// Raised when a new message stream becomes available (on Connect).
    /// Subscribers should use this to subscribe to the observable for message data.
    /// </summary>
    public event Action<StreamConnectedEventArgs<MqttApplicationMessageReceivedEventArgs>>? MessageStreamConnected;

    /// <summary>
    /// Raised when the message stream ends (on Disconnect).
    /// Subscribers should dispose their subscriptions.
    /// </summary>
    public event Action? MessageStreamDisconnected;

    /// <summary>
    /// Raised when a new packet inspection stream becomes available (on Connect).
    /// </summary>
    public event Action<StreamConnectedEventArgs<InspectMqttPacketEventArgs>>? PacketStreamConnected;

    /// <summary>
    /// Raised when the packet inspection stream ends (on Disconnect).
    /// </summary>
    public event Action? PacketStreamDisconnected;

    /// <summary>
    /// Raised when a new log stream becomes available (on Connect).
    /// </summary>
    public event Action<StreamConnectedEventArgs<MqttNetLogMessagePublishedEventArgs>>? LogStreamConnected;

    /// <summary>
    /// Raised when the log stream ends (on Disconnect).
    /// </summary>
    public event Action? LogStreamDisconnected;

    public event EventHandler<MqttClientDisconnectedEventArgs>? Disconnected;

    public bool IsConnected => _mqttClient?.IsConnected == true;

    // These counters are display-only. Plain reads are sufficient because:
    // - Each counter has a single writer thread (no torn reads on 64-bit)
    // - The UI polls periodically, so brief staleness is acceptable
    // - Cache coherence ensures eventual visibility across cores
    public long ReceivedMessagesCount => _receivedMessagesCount;

    public long NotifiedMessagesCount => _notifiedMessagesCount;

    public long BufferedMessagesCount => Math.Max(0, _enqueuedMessagesCount - _notifiedMessagesCount - _droppedMessagesCount);

    public long DroppedMessagesCount => _droppedMessagesCount;

    /// <summary>
    /// Buffer window (ms) for message-based reactive subscribers (Inflight, TopicExplorer, Log).
    /// Set from <see cref="MessageProcessingOptionsViewModel"/> at connect time.
    /// </summary>
    public int MessageBufferMs { get; private set; } = 200;

    /// <summary>
    /// Buffer window (ms) for the packet-inspection reactive subscriber.
    /// Set from <see cref="MessageProcessingOptionsViewModel"/> at connect time.
    /// </summary>
    public int PacketBufferMs { get; private set; } = 500;

    /// <summary>
    /// Maximum items kept per UI-bound list. Subscribers trim oldest items
    /// in batches of <see cref="TrimBatchSize"/> when this limit is exceeded.
    /// </summary>
    public int MaxUiItems { get; private set; } = 150_000;

    /// <summary>
    /// Number of oldest items removed at once when a UI list exceeds <see cref="MaxUiItems"/>.
    /// </summary>
    public int TrimBatchSize { get; private set; } = 500;

    /// <summary>
    /// Interval (ms) at which the status-bar counters are refreshed.
    /// Set from <see cref="MessageProcessingOptionsViewModel"/> at connect time.
    /// </summary>
    public int CounterUpdateMs { get; private set; } = 200;

    public async Task<MqttClientConnectResult> Connect(ConnectionItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_mqttClient != null)
        {
            _mqttClient.ApplicationMessageReceivedAsync -= OnApplicationMessageReceived;
            _mqttClient.InspectPacketAsync -= OnInspectPacket;
            _mqttClient.DisconnectedAsync -= OnDisconnected;

            await StopChannelProcessingAsync().ConfigureAwait(false);

            await _mqttClient.DisconnectAsync();
            _mqttClient.Dispose();
        }

        _mqttClient = new MqttClientFactory(_mqttNetEventLogger).CreateMqttClient();
        _mqttClient.DisconnectedAsync += OnDisconnected;

        StartChannelProcessing(item.MessageProcessingOptions);
        StartReactiveStreams();

        var clientOptionsBuilder = new MqttClientOptionsBuilder().WithTimeout(TimeSpan.FromSeconds(item.ServerOptions.CommunicationTimeout))
            .WithProtocolVersion(item.ServerOptions.SelectedProtocolVersion.Value)
            .WithClientId(item.SessionOptions.ClientId)
            .WithCleanSession(item.SessionOptions.CleanSession)
            .WithCredentials(item.SessionOptions.UserName, item.SessionOptions.Password)
            .WithRequestProblemInformation(item.SessionOptions.RequestProblemInformation)
            .WithRequestResponseInformation(item.SessionOptions.RequestResponseInformation)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(item.SessionOptions.KeepAliveInterval))
            .WithReceiveMaximum(item.ServerOptions.ReceiveMaximum)
            .WithoutPacketFragmentation(); // We do not need this optimization is this type of client. It will also increase compatibility.

        if (item.SessionOptions.SessionExpiryInterval > 0)
        {
            clientOptionsBuilder.WithSessionExpiryInterval((uint)item.SessionOptions.SessionExpiryInterval);
        }

        if (!string.IsNullOrEmpty(item.SessionOptions.AuthenticationMethod))
        {
            clientOptionsBuilder.WithEnhancedAuthentication(item.SessionOptions.AuthenticationMethod, Convert.FromBase64String(item.SessionOptions.AuthenticationData));
        }

        if (item.ServerOptions.SelectedTransport.Value == Transport.Tcp)
        {
            clientOptionsBuilder.WithTcpServer(item.ServerOptions.Host, item.ServerOptions.Port);
        }
        else
        {
            clientOptionsBuilder.WithWebSocketServer(o =>
            {
                o.WithUri(item.ServerOptions.Host);
            });
        }

        if (item.ServerOptions.SelectedTlsVersion.Value != SslProtocols.None)
        {
            clientOptionsBuilder.WithTlsOptions(o =>
            {
                o.UseTls();
                o.WithSslProtocols(item.ServerOptions.SelectedTlsVersion.Value);
                o.WithIgnoreCertificateChainErrors(item.ServerOptions.IgnoreCertificateErrors);
                o.WithIgnoreCertificateRevocationErrors(item.ServerOptions.IgnoreCertificateErrors);

                if (item.ServerOptions.IgnoreCertificateErrors)
                {
                    o.WithCertificateValidationHandler(context => true);
                    o.WithAllowUntrustedCertificates(true);
                    o.WithIgnoreCertificateChainErrors(true);
                    o.WithIgnoreCertificateRevocationErrors(true);
                }

                if (!string.IsNullOrEmpty(item.SessionOptions.CertificatePath))
                {
                    X509Certificate2Collection certificates = [];

                    if (string.IsNullOrEmpty(item.SessionOptions.CertificatePassword))
                    {
                        certificates.Add(X509CertificateLoader.LoadCertificateFromFile(item.SessionOptions.CertificatePath));
                    }
                    else
                    {
                        certificates.Add(X509CertificateLoader.LoadPkcs12FromFile(item.SessionOptions.CertificatePath, item.SessionOptions.CertificatePassword));
                    }

                    o.WithClientCertificates(certificates);
                    o.WithApplicationProtocols([new SslApplicationProtocol("mqtt")]);
                }
            });
        }

        _mqttClient.ApplicationMessageReceivedAsync += OnApplicationMessageReceived;
        
        // Always register the packet inspector BEFORE connecting.
        // 
        // IMPORTANT: MQTTnet's InspectPacketAsync event is not safe to subscribe/unsubscribe
        // while the client is connected and processing packets. Doing so causes disconnections
        // due to race conditions in the event invocation during the receive loop.
        // 
        // ROOT CAUSE (MQTTnet source code references):
        // 
        // 1. AsyncEvent<T>.AddHandler() in MQTTnet/Internal/AsyncEvent.cs:
        //    - Creates a NEW list copy: _handlersForInvoke = new List<>(_handlers)
        //    - This invalidates any existing iteration over _handlersForInvoke
        // 
        // 2. MqttPacketInspector.InspectPacket() in MQTTnet/Diagnostics/PacketInspection/MqttPacketInspector.cs:
        //    - Calls: await _asyncEvent.InvokeAsync(eventArgs).ConfigureAwait(false)
        //    - This is called on the receive loop thread for EVERY packet (including keep-alive)
        // 
        // 3. AsyncEvent<T>.InvokeAsync() in MQTTnet/Internal/AsyncEvent.cs:
        //    - Gets reference: var handlers = _handlersForInvoke
        //    - Iterates: foreach (var handler in handlers)
        //    - If AddHandler() replaces _handlersForInvoke mid-iteration, behavior is undefined
        // 
        // 4. The race window:
        //    - Receive loop calls InvokeAsync(), gets _handlersForInvoke reference
        //    - User calls AddHandler() on another thread, replaces _handlersForInvoke
        //    - Iteration may fail or handler list becomes inconsistent
        //    - Socket operations fail, keep-alive times out, connection drops
        // 
        // WORKAROUND: Register handler BEFORE ConnectAsync() when no packets are being processed.
        // The handler checks _isInspectingPackets flag internally to decide whether to process.
        // This has minimal overhead (just a boolean check) when inspection is disabled.
        _mqttClient.InspectPacketAsync += OnInspectPacket;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(item.ServerOptions.CommunicationTimeout));
        try
        {
            return await _mqttClient.ConnectAsync(clientOptionsBuilder.Build(), timeout.Token);
        }
        catch (OperationCanceledException ex)
        {
            if (timeout.IsCancellationRequested)
            {
                throw new MqttCommunicationTimedOutException(ex);
            }

            throw;
        }
    }

    public async Task Disconnect()
    {
        ThrowIfNotConnected();

        await _mqttClient!.DisconnectAsync().ConfigureAwait(false);
        StopReactiveStreams();
        await StopChannelProcessingAsync().ConfigureAwait(false);
    }

    public async Task<MqttClientPublishResult> Publish(MqttApplicationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        ThrowIfNotConnected();

        return await _mqttClient!.PublishAsync(message, cancellationToken);
    }

    public async Task<MqttClientPublishResult> Publish(PublishItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        ThrowIfNotConnected();

        byte[] payload = item.PayloadFormat switch
        {
            BufferFormat.Plain => Encoding.UTF8.GetBytes(item.Payload),
            BufferFormat.Base64 => Convert.FromBase64String(item.Payload),
            BufferFormat.Path => await File.ReadAllBytesAsync(item.Payload),
            _ => throw new NotSupportedException("Unsupported buffer format.")
        };

        var applicationMessageBuilder = new MqttApplicationMessageBuilder().WithTopic(item.Topic)
            .WithQualityOfServiceLevel(item.QualityOfServiceLevel.Value)
            .WithRetainFlag(item.Retain)
            .WithMessageExpiryInterval(item.MessageExpiryInterval)
            .WithContentType(item.ContentType)
            .WithPayloadFormatIndicator(item.PayloadFormatIndicator.Value)
            .WithPayload(payload)
            .WithResponseTopic(item.ResponseTopic);

        if (item.SubscriptionIdentifier > 0)
        {
            applicationMessageBuilder.WithSubscriptionIdentifier(item.SubscriptionIdentifier);
        }

        if (item.TopicAlias > 0)
        {
            applicationMessageBuilder.WithTopicAlias(item.TopicAlias);
        }

        foreach (var userProperty in item.UserProperties.Items)
        {
            if (!string.IsNullOrEmpty(userProperty.Name))
            {
                applicationMessageBuilder.WithUserProperty(userProperty.Name, userProperty.Value);
            }
        }

        return await _mqttClient!.PublishAsync(applicationMessageBuilder.Build());
    }



    public async Task<MqttClientSubscribeResult> Subscribe(SubscriptionItemViewModel subscriptionItem)
    {
        ArgumentNullException.ThrowIfNull(subscriptionItem);

        ThrowIfNotConnected();

        var topicFilter = new MqttTopicFilterBuilder().WithTopic(subscriptionItem.Topic)
            .WithQualityOfServiceLevel(subscriptionItem.QualityOfServiceLevel.Value)
            .WithNoLocal(subscriptionItem.NoLocal)
            .WithRetainHandling(subscriptionItem.RetainHandling.Value)
            .WithRetainAsPublished(subscriptionItem.RetainAsPublished)
            .Build();

        var subscribeOptionsBuilder = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(topicFilter);

        foreach (var userProperty in subscriptionItem.UserProperties.Items)
        {
            if (!string.IsNullOrEmpty(userProperty.Name))
            {
                subscribeOptionsBuilder.WithUserProperty(userProperty.Name, userProperty.Value);
            }
        }

        var subscribeOptions = subscribeOptionsBuilder.Build();

        return await _mqttClient!.SubscribeAsync(subscribeOptions).ConfigureAwait(false);
    }

    public async Task<MqttClientUnsubscribeResult> Unsubscribe(SubscriptionItemViewModel subscriptionItem)
    {
        ArgumentNullException.ThrowIfNull(subscriptionItem);

        ThrowIfNotConnected();

        return await _mqttClient.UnsubscribeAsync(subscriptionItem.Topic).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts inspecting MQTT packets. The handler is always registered at connect time,
    /// this just enables actual processing.
    /// </summary>
    public void StartInspectingPackets()
    {
        ThrowIfNotConnected();
        _isInspectingPackets = true;
    }

    /// <summary>
    /// Stops inspecting MQTT packets. The handler remains registered,
    /// this just disables processing.
    /// </summary>
    public void StopInspectingPackets()
    {
        _isInspectingPackets = false;
    }

    async Task OnApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs eventArgs)
    {
        // Single writer thread - plain increment is safe
        _receivedMessagesCount++;

        var channel = _receivedMessagesChannel;
        if (channel == null)
        {
            return;
        }

        try
        {
            if (_waitForMessageChannel)
            {
                var cancellationToken = _channelCancellationTokenSource?.Token ?? CancellationToken.None;
                await channel.Writer.WriteAsync(eventArgs, cancellationToken).ConfigureAwait(false);
                _enqueuedMessagesCount++;
            }
            else
            {
                if (channel.Writer.TryWrite(eventArgs))
                {
                    _enqueuedMessagesCount++;
                }
            }
        }
        catch (OperationCanceledException e)
        {
            // This can happen when the channel is completed while we are trying to write to it.
            _logger.LogWarning(e, "Failed to write received message to channel because the operation was canceled.");
        }
    }

    Task OnInspectPacket(InspectMqttPacketEventArgs eventArgs)
    {
        // IMPORTANT: This method is called on the MQTT client's internal packet processing thread.
        // Return immediately if inspection is not enabled.
        if (!_isInspectingPackets)
        {
            return Task.CompletedTask;
        }

        var channel = _packetInspectionChannel;
        if (channel == null)
        {
            return Task.CompletedTask;
        }
        
        // IMPORTANT: Never await/block here! This method is called on the MQTT client's
        // internal packet processing thread. Blocking here will prevent keep-alive packets
        // from being processed, causing connection timeouts and disconnections.
        // Always use TryWrite for packet inspection to avoid blocking.
        channel.Writer.TryWrite(eventArgs);
        
        return Task.CompletedTask;
    }

    async Task ConsumeReceivedMessagesAsync(ChannelReader<MqttApplicationMessageReceivedEventArgs> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var eventArgs))
                {
                    _notifiedMessagesCount++;

                    // Push into the hot observable stream for all reactive subscribers
                    _messageSubject?.OnNext(eventArgs);

                    var delayMs = _messageProcessingDelayMs;
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException e)
        {
            // This is expected when the channel is completed.
            _logger.LogWarning(e, "Received messages channel processing was canceled.");
        }
    }

    async Task ConsumePacketInspectionsAsync(ChannelReader<InspectMqttPacketEventArgs> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var eventArgs))
                {
                    // Push into the hot observable stream for all reactive subscribers
                    _packetSubject?.OnNext(eventArgs);
                    
                    // Wait here to avoid flooding the message inspectors with packets when they are not able to keep up.
                    // This will also increase the responsiveness of the UI when inspecting packets.
                    var delayMs = _packetInspectionDelayMs;
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Received messages channel processing was canceled.");
        }
    }

    void StartChannelProcessing(MessageProcessingOptionsViewModel options)
    {
        _channelCancellationTokenSource?.Dispose();
        _channelCancellationTokenSource = new CancellationTokenSource();

        _messageProcessingDelayMs = Math.Max(0, options.MessageProcessingDelayMs);
        _packetInspectionDelayMs = Math.Max(0, options.PacketInspectionDelayMs);

        // Store buffer times for reactive stream subscribers
        MessageBufferMs = Math.Max(50, options.MessageBufferMs);
        PacketBufferMs = Math.Max(50, options.PacketBufferMs);

        // Store UI list limits
        MaxUiItems = Math.Max(1_000, options.MaxUiItems);
        TrimBatchSize = Math.Max(1, options.TrimBatchSize);
        CounterUpdateMs = Math.Max(50, options.CounterUpdateMs);

        // Reset counters for new session
        _receivedMessagesCount = 0;
        _enqueuedMessagesCount = 0;
        _notifiedMessagesCount = 0;
        _droppedMessagesCount = 0;

        _waitForMessageChannel = options is
        {
            UseBoundedChannel: true
        } && options.SelectedFullMode.Value == BoundedChannelFullMode.Wait;

        if (options.UseBoundedChannel)
        {
            var boundedMessageOptions = new BoundedChannelOptions(Math.Max(1, options.Capacity))
            {
                FullMode = options.SelectedFullMode.Value,
                SingleReader = true,
                SingleWriter = true
            };
            
            // Packet inspection channel uses thread pool offloading, so multiple threads may write
            var boundedInspectionOptions = new BoundedChannelOptions(Math.Max(1, options.Capacity))
            {
                FullMode = options.SelectedFullMode.Value,
                SingleReader = true,
                SingleWriter = false  // Multiple thread pool threads may write
            };

            _receivedMessagesChannel = Channel.CreateBounded<MqttApplicationMessageReceivedEventArgs>(boundedMessageOptions, OnMessageDropped);
            _packetInspectionChannel = Channel.CreateBounded<InspectMqttPacketEventArgs>(boundedInspectionOptions, OnPacketInspectionDropped);
        }
        else
        {
            var unboundedMessageOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            };
            
            // Packet inspection channel uses thread pool offloading, so multiple threads may write
            var unboundedInspectionOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false  // Multiple thread pool threads may write
            };

            _receivedMessagesChannel = Channel.CreateUnbounded<MqttApplicationMessageReceivedEventArgs>(unboundedMessageOptions);
            _packetInspectionChannel = Channel.CreateUnbounded<InspectMqttPacketEventArgs>(unboundedInspectionOptions);
        }

        var cancellationToken = _channelCancellationTokenSource.Token;
        
        _receivedMessagesTask = Task.Factory.StartNew(() => ConsumeReceivedMessagesAsync(_receivedMessagesChannel.Reader, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        
        _packetInspectionTask = Task.Factory.StartNew(() => ConsumePacketInspectionsAsync(_packetInspectionChannel.Reader, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    async Task StopChannelProcessingAsync()
    {
        var cancellationTokenSource = _channelCancellationTokenSource;
        if (cancellationTokenSource == null)
        {
            return;
        }

         await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        _receivedMessagesChannel?.Writer.TryComplete();
        _packetInspectionChannel?.Writer.TryComplete();
        var receivedMessagesTask = _receivedMessagesTask;
        var packetInspectionTask = _packetInspectionTask;
        var tasks = new List<Task>(2);
        if (receivedMessagesTask is not null)
        {
            tasks.Add(receivedMessagesTask);
        }

        if (packetInspectionTask is not null)
        {
            tasks.Add(packetInspectionTask);
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException e)
            {
                _logger.LogCritical(e, "Channel processing tasks were canceled.");
            }
        }

        _receivedMessagesTask = null;
        _packetInspectionTask = null;
        _receivedMessagesChannel = null;
        _packetInspectionChannel = null;

        _channelCancellationTokenSource = null;
        cancellationTokenSource.Dispose();
    }
    
    
    Task OnDisconnected(MqttClientDisconnectedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post((state) =>
        {   
            var args = (MqttClientDisconnectedEventArgs)state!;
            Disconnected?.Invoke(this, args);
        }, eventArgs);
        return Task.CompletedTask;
    }

    void OnMessageDropped(MqttApplicationMessageReceivedEventArgs e)
    {
        _droppedMessagesCount++;
        _logger.LogReceivedMessageDropped(e.PacketIdentifier);
    }

    void OnPacketInspectionDropped(InspectMqttPacketEventArgs e)
    {
        _logger.LogPacketInspectionDropped();
    }

    void OnLogMessagePublished(object? sender, MqttNetLogMessagePublishedEventArgs e)
    {
        // Push into the hot observable stream for all reactive subscribers
        _logSubject?.OnNext(e);
    }

    /// <summary>
    /// Creates new reactive subjects and multicasted observable streams for the current session.
    /// Notifies subscribers via session events so they can subscribe to the new streams.
    /// </summary>
    void StartReactiveStreams()
    {
        // Message stream
        _messageSubject = new Subject<MqttApplicationMessageReceivedEventArgs>();
        _messageStream = _messageSubject.Publish().RefCount();

        // Packet inspection stream
        _packetSubject = new Subject<InspectMqttPacketEventArgs>();
        _packetStream = _packetSubject.Publish().RefCount();

        // Log stream
        _logSubject = new Subject<MqttNetLogMessagePublishedEventArgs>();
        _logStream = _logSubject.Publish().RefCount();

        // Notify subscribers that new streams are available
        MessageStreamConnected?.Invoke(new StreamConnectedEventArgs<MqttApplicationMessageReceivedEventArgs>(
            _messageStream, MessageBufferMs, MaxUiItems, TrimBatchSize, CounterUpdateMs));
        PacketStreamConnected?.Invoke(new StreamConnectedEventArgs<InspectMqttPacketEventArgs>(
            _packetStream, PacketBufferMs, MaxUiItems, TrimBatchSize, CounterUpdateMs));
        LogStreamConnected?.Invoke(new StreamConnectedEventArgs<MqttNetLogMessagePublishedEventArgs>(
            _logStream, MessageBufferMs, MaxUiItems, TrimBatchSize, CounterUpdateMs));
    }

    /// <summary>
    /// Completes and disposes the reactive subjects, ending all observable streams.
    /// Notifies subscribers via session events.
    /// </summary>
    void StopReactiveStreams()
    {
        _messageSubject?.OnCompleted();
        _messageSubject?.Dispose();
        _messageSubject = null;
        _messageStream = null;

        _packetSubject?.OnCompleted();
        _packetSubject?.Dispose();
        _packetSubject = null;
        _packetStream = null;

        _logSubject?.OnCompleted();
        _logSubject?.Dispose();
        _logSubject = null;
        _logStream = null;

        // Notify subscribers that streams have ended
        MessageStreamDisconnected?.Invoke();
        PacketStreamDisconnected?.Invoke();
        LogStreamDisconnected?.Invoke();
    }

    void ThrowIfNotConnected()
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            throw new InvalidOperationException("The MQTT client is not connected.");
        }
    }
}

internal static partial class MqttClientServiceExtensions
{
    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "Error in packet inspector.")]
    public static partial void LogPackageInspectorError(this ILogger<MqttClientService> logger, Exception? exception);

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Message {PacketIdentifier} was dropped in the received messages channel. This can happen when the channel is full and messages are being produced faster than they are being consumed.")]
    public static partial void LogReceivedMessageDropped(this ILogger<MqttClientService> logger, ushort packetIdentifier);
    
    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Error in application message received event handler.")]
    public static partial void LogApplicationMessageReceivedError(this ILogger<MqttClientService> logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Packet inspection was dropped in the channel. This can happen when the channel is full and packets are being produced faster than they are being consumed.")]
    public static partial void LogPacketInspectionDropped(this ILogger<MqttClientService> logger);
}

