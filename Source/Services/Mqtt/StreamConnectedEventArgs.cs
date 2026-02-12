using System;

namespace mqttMultimeter.Services.Mqtt;

/// <summary>
/// Carries a hot observable stream together with the pipeline configuration
/// that subscribers need. Passed through session events on Connect so each
/// subscriber can build its reactive pipeline without reading back from the service.
/// </summary>
public sealed record StreamConnectedEventArgs<T>(
    IObservable<T> Stream,
    int BufferMs,
    int MaxUiItems,
    int CounterUpdateMs);
