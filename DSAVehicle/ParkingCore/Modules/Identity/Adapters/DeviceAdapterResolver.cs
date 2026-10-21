using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// Built-in code adapters first, then enabled device profiles.
public sealed class DeviceAdapterResolver(CameraAdapters builtIn, ParkingDb db, ProfileCache cache)
{
    public async Task<ICameraAdapter?> Find(string name, CancellationToken stop = default)
    {
        if (builtIn.Find(name) is { } adapter) return adapter;
        var profile = await db.DeviceProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == name && x.Enabled, stop);
        if (profile is null) return null;
        // Profiles are validated on save; one that no longer compiles is treated as unknown so its messages are refused, not misread.
        try { return cache.Get(profile); } catch (ProfileException) { return null; }
    }
}
