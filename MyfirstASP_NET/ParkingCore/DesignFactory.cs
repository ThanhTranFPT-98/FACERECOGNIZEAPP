using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace MyfirstASP_NET.ParkingCore;
public class ParkingDesignFactory : IDesignTimeDbContextFactory<ParkingDb>
{
    public ParkingDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ParkingDb>().UseSqlite("Data Source=parking-design.db").Options);
}
public class ParkingSqlServerDesignFactory : IDesignTimeDbContextFactory<ParkingSqlServerDb>
{
    public ParkingSqlServerDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ParkingSqlServerDb>().UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=Parking;Integrated Security=true").Options);
}
