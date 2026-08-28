using System.Collections.Generic;
using System.Threading.Channels;

namespace mqttMultimeter.Pages.Connection;

/// <summary>
/// Data model for a bounded-channel full-mode option.
/// Replaces <c>EnumViewModel&lt;BoundedChannelFullMode&gt;</c> so the display
/// name lives next to the value without a generic wrapper.
/// </summary>
public sealed record BoundedChannelFullModeOption(string DisplayName, BoundedChannelFullMode Value)
{
    /// <summary>All available full-mode options.</summary>
    public static IReadOnlyList<BoundedChannelFullModeOption> All { get; } =
    [
        new("Wait", BoundedChannelFullMode.Wait),
        new("Drop newest", BoundedChannelFullMode.DropNewest),
        new("Drop oldest", BoundedChannelFullMode.DropOldest),
        new("Drop write", BoundedChannelFullMode.DropWrite)
    ];

    public override string ToString() => DisplayName;
}
