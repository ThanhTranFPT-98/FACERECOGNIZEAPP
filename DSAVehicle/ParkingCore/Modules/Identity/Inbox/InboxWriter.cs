using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// CR-015 flexible device input (docs/DEVICE_INPUT.md). Layers:
//   1 transport: HTTP receiver, receiver ports, or an in-process SDK connector → InboxWriter (store before ACK)
//   2 decoding:  built-in code adapters, or a DeviceProfile (declarative mapping of a JSON/XML message)
//   3 contract:  ExternalObservation; external person IDs resolved through PersonIdentity
//   4 business:  ParkingService.Ingest (unchanged). A profile only produces evidence, never a command.

public enum InboxOutcome { Stored, Duplicate, UnknownAdapter, BadBody }

// The only way a device message enters the system: HTTP receiver or an in-process connector (vendor SDK).
// The message is durable when this returns Stored/Duplicate, so a device (or SDK) may be acknowledged after it, not before.
public sealed class InboxWriter(ParkingDb db, DeviceAdapterResolver adapters, InboxSignal signal)
{
    public async Task<InboxOutcome> Store(string adapterName, string body, string source, CancellationToken stop = default)
    {
        var adapter = await adapters.Find(adapterName, stop);
        if (adapter is null) return InboxOutcome.UnknownAdapter;
        if (!PayloadFormat.Valid(adapter.Format, body)) return InboxOutcome.BadBody;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        // A retry of the same message is acknowledged again but stored once. Connectors must put a device ID/time in the body.
        var since = DateTime.UtcNow.AddHours(-1);
        if (await db.CameraInbox.AnyAsync(x => x.PayloadSha256 == hash && x.ReceivedUtc > since, stop)) return InboxOutcome.Duplicate;
        db.CameraInbox.Add(new CameraInbox { Adapter = adapterName, Payload = body, PayloadSha256 = hash, RemoteIp = source.Length > 64 ? source[..64] : source });
        await db.SaveChangesAsync(stop);   // durable before ACK
        signal.Notify();
        return InboxOutcome.Stored;
    }
}
