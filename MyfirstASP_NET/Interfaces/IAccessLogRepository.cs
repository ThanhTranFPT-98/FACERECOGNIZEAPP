using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Dtos.AccessLog;

namespace MyfirstASP_NET.Interfaces
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