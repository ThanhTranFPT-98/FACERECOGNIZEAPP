using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.AccessLog;

namespace DSAVehicle.Interfaces
{
    public interface IAccessLogRepository
    {
        Task<List<AccessLog>> GetAllAsync();
        Task<AccessLog?> GetByIdAsync(int id);

        Task<AccessLog> CreateAsync(AccessLog AccessLogModel);

        Task<AccessLog?> DeleteAsync(int id);

        Task<bool> AccessLogPeopleExistsAsync(int id);
        
    }
}