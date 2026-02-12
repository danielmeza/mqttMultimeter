using System.Collections.Generic;
using System.Threading.Channels;

namespace mqttMultimeter.Pages.Connection;

/// <summary>
/// A throughput profile carrying all recommended settings as data.
/// When selected the ViewModel copies the values into the UI properties.
/// The <see cref="IsCustom"/> profile leaves the values untouched.
/// </summary>
public sealed record MessageProcessingProfile(
    string DisplayName,
    bool IsCustom,
    bool UseBoundedChannel,
    int Capacity,
    BoundedChannelFullMode FullMode,
    int MessageProcessingDelayMs,
    int PacketInspectionDelayMs,
    int MessageBufferMs,
    int PacketBufferMs,
    int MaxUiItems,
    int TrimBatchSize,
    int CounterUpdateMs)
{
    /// <summary>All built-in profiles (from docs/high-throughput-guide.md).</summary>
    public static IReadOnlyList<MessageProcessingProfile> BuiltIn { get; } =
    [
        new("\u2264 1K msg/s  (Low)",
            IsCustom: false,
            UseBoundedChannel: false,
            Capacity: 5_000,
            FullMode: BoundedChannelFullMode.DropNewest,
            MessageProcessingDelayMs: 0,
            PacketInspectionDelayMs: 0,
            MessageBufferMs: 50,
            PacketBufferMs: 50,
            MaxUiItems: 150_000,
            TrimBatchSize: 500,
            CounterUpdateMs: 200),

        new("\u2264 5K msg/s  (Medium)",
            IsCustom: false,
            UseBoundedChannel: true,
            Capacity: 20_000,
            FullMode: BoundedChannelFullMode.DropNewest,
            MessageProcessingDelayMs: 3,
            PacketInspectionDelayMs: 3,
            MessageBufferMs: 150,
            PacketBufferMs: 150,
            MaxUiItems: 150_000,
            TrimBatchSize: 500,
            CounterUpdateMs: 200),

        new("8K+ msg/s  (High)",
            IsCustom: false,
            UseBoundedChannel: true,
            Capacity: 50_000,
            FullMode: BoundedChannelFullMode.DropNewest,
            MessageProcessingDelayMs: 5,
            PacketInspectionDelayMs: 5,
            MessageBufferMs: 300,
            PacketBufferMs: 300,
            MaxUiItems: 150_000,
            TrimBatchSize: 500,
            CounterUpdateMs: 200),

        new("Custom",
            IsCustom: true,
            UseBoundedChannel: true,
            Capacity: 50_000,
            FullMode: BoundedChannelFullMode.DropNewest,
            MessageProcessingDelayMs: 5,
            PacketInspectionDelayMs: 5,
            MessageBufferMs: 200,
            PacketBufferMs: 500,
            MaxUiItems: 150_000,
            TrimBatchSize: 500,
            CounterUpdateMs: 200)
    ];

    public override string ToString() => DisplayName;
}
