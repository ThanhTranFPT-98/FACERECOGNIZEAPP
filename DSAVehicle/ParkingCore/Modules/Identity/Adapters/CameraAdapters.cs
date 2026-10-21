namespace DSAVehicle.ParkingCore;

public sealed class CameraAdapters(IEnumerable<ICameraAdapter> adapters)
{
    public ICameraAdapter? Find(string name) => adapters.FirstOrDefault(x => x.Name == name);
}
