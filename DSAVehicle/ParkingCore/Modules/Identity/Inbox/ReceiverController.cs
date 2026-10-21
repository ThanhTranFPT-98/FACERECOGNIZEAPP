using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DSAVehicle.ParkingCore;

[ApiController, AllowAnonymous]
public class ReceiverController(InboxWriter inbox, IConfiguration config) : ControllerBase
{
    // Preferred: http://station/api/v1/parking/receiver/{adapter}?key=<Receiver:Key> (key ≥ 32 characters).
    [HttpPost("api/v1/parking/receiver/{adapter}"), RequestSizeLimit(16_000_000)]
    public async Task<IActionResult> Receive(string adapter, [FromQuery] string? key)
    {
        var options = ReceiverOptions.From(config);
        if (string.IsNullOrEmpty(options.Key) || options.Key.Length < 32 || key is null
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(options.Key)), SHA256.HashData(Encoding.UTF8.GetBytes(key)))) return Unauthorized();
        return await Store(adapter);
    }

    // For cameras that can only post to "host:port/api" (T1550 capture). Served only on a receiver port and only to Receiver:AllowedIps.
    [HttpPost("api"), RequestSizeLimit(16_000_000)]
    public async Task<IActionResult> ReceiveOnPort()
    {
        var options = ReceiverOptions.From(config);
        if (!options.Ports.TryGetValue(HttpContext.Connection.LocalPort, out var adapter)) return NotFound();
        if (!IpAllowed(HttpContext.Connection.RemoteIpAddress, options.AllowedIps)) return StatusCode(403);
        return await Store(adapter);
    }

    public static bool IpAllowed(IPAddress? remote, string[] allowed)
    {
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        foreach (var entry in allowed)
        {
            var parts = entry.Split('/');
            if (!IPAddress.TryParse(parts[0], out var network)) continue;
            if (parts.Length == 1) { if (network.Equals(remote)) return true; continue; }
            if (!int.TryParse(parts[1], out var bits) || network.AddressFamily != remote.AddressFamily) continue;
            var a = network.GetAddressBytes(); var b = remote.GetAddressBytes();
            var match = true;
            for (var i = 0; i < a.Length && match; i++)
            {
                var take = Math.Clamp(bits - i * 8, 0, 8);
                var mask = (byte)(0xFF << (8 - take));
                match = (a[i] & mask) == (b[i] & mask);
            }
            if (match) return true;
        }
        return false;
    }

    private async Task<IActionResult> Store(string adapterName)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        // Stored (or already stored: a camera retry) before the ACK; see InboxWriter.
        return await inbox.Store(adapterName, body, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "") switch
        {
            InboxOutcome.UnknownAdapter => NotFound(new { message = "Unknown adapter." }),
            InboxOutcome.BadBody => BadRequest(new { message = "Body must be JSON (or XML for an XML device profile)." }),
            _ => Ok(new { result = "ok" })
        };
    }
}
