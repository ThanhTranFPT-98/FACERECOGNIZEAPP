using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Interfaces;
using MyfirstASP_NET.Data;
using Microsoft.EntityFrameworkCore;
using MyfirstASP_NET.Dtos.AccessLog;
using MyfirstASP_NET.Mapper;


namespace MyfirstASP_NET.Repository
{
    public class AccessLogRepository: IAccessLogRepository
    {
        private readonly ApplicationDBContext _context;
        public  AccessLogRepository (ApplicationDBContext context)
        {
            _context = context;
        }

        public async Task<List<AccessLog>> GetAllAsync()
        {
            return await _context.AccessLogs
                .Include(a => a.People)
                .OrderByDescending(a => a.LogTime)
                .ToListAsync();
        }

        public async Task<AccessLog?> GetByIdAsync(int id)
        {
            return await _context.AccessLogs
                .Include(a => a.People)
                .FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<AccessLog> CreateAsync(AccessLog AccessLogModel)
        {
            
            await _context.AccessLogs.AddAsync(AccessLogModel);
            await _context.SaveChangesAsync();
            return AccessLogModel;
            
        }

        public async Task<AccessLog?> DeleteAsync(int id)
        {
            var AccessLogModel = _context.AccessLogs.FirstOrDefault(x => x.Id == id);

            if (AccessLogModel == null)
            {
                return null;
            }

            _context.AccessLogs.Remove(AccessLogModel);
            await _context.SaveChangesAsync();
            return AccessLogModel;
        }

        public async Task<bool> AccessLogPeopleExistsAsync(int id)
        {
            return await _context.People.AnyAsync(a => a.Id == id);
        }
    }
}