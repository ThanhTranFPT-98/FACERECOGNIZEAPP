namespace DSAVehicle.ParkingCore;

public sealed record ReceiverOptions(string? Key, int? Port, string PortAdapter, string[] AllowedIps)
{
    public static ReceiverOptions From(IConfiguration c) => new(c["Receiver:Key"], c.GetValue<int?>("Receiver:Port"), c["Receiver:PortAdapter"] ?? "t1550",
        (c["Receiver:AllowedIps"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { Ports = ParsePorts(c) };
    // Port → adapter for devices that can only post to "host:port/api": Receiver:Port/PortAdapter plus Receiver:Ports = "2124=hik-anpr;2125=zk-face" (CR-015).
    public IReadOnlyDictionary<int, string> Ports { get; init; } = new Dictionary<int, string>();
    public static Dictionary<int, string> ParsePorts(IConfiguration c)
    {
        var ports = new Dictionary<int, string>();
        if (c.GetValue<int?>("Receiver:Port") is int legacy) ports[legacy] = c["Receiver:PortAdapter"] ?? "t1550";
        foreach (var entry in (c["Receiver:Ports"] ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var port) && port is > 0 and < 65536 && parts[1].Length > 0) ports[port] = parts[1];
        }
        return ports;
    }
}
