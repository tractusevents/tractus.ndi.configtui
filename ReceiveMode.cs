namespace Tractus.Ndi.ConfigTui;

internal enum ReceiveMode
{
    SingleTcp,
    ReliableUdp,
    Udp,
    MultiTcp,
    Custom
}

internal static class ReceiveModeExtensions
{
    public static string ToDisplayName(this ReceiveMode mode) =>
        mode switch
        {
            ReceiveMode.SingleTcp => "Single-TCP",
            ReceiveMode.ReliableUdp => "Reliable UDP",
            ReceiveMode.Udp => "UDP",
            ReceiveMode.MultiTcp => "Multi-TCP",
            _ => "Custom"
        };

    public static ReceiveMode NextPreset(this ReceiveMode mode) =>
        mode switch
        {
            ReceiveMode.SingleTcp => ReceiveMode.ReliableUdp,
            ReceiveMode.ReliableUdp => ReceiveMode.Udp,
            ReceiveMode.Udp => ReceiveMode.MultiTcp,
            _ => ReceiveMode.SingleTcp
        };
}
