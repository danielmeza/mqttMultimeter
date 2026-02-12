using mqttMultimeter.Common;

namespace mqttMultimeter.Pages.PacketInspector;

public sealed class PacketViewModel : BaseViewModel
{
    public byte[]? Data { get; init; }

    public bool IsInbound { get; init; }

    public long Length { get; init; }
    
    public int Number { get; set; }

    public string? Type { get; init; }
}