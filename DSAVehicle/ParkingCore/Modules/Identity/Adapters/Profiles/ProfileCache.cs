using System.Collections.Concurrent;

namespace DSAVehicle.ParkingCore;

// Compiled profiles, reused until the profile's Version changes (one API process per site, so no cross-process invalidation).
public sealed class ProfileCache
{
    private readonly ConcurrentDictionary<string, (int Version, ProfileAdapter Adapter)> compiled = new();
    public ProfileAdapter Get(DeviceProfile profile)
    {
        if (compiled.TryGetValue(profile.Id, out var entry) && entry.Version == profile.Version) return entry.Adapter;
        var adapter = ProfileAdapter.Compile(profile);
        compiled[profile.Id] = (profile.Version, adapter);
        return adapter;
    }
}
