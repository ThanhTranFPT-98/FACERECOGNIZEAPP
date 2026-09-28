using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.Stranger;
using DSAVehicle.Interfaces;
using DSAVehicle.Helpers;
using DSAVehicle.Data;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.Repository
{
    public class StrangerRepository : IStrangerRepository 
    {
        private readonly ApplicationDBContext _context;
        public  StrangerRepository (ApplicationDBContext context)
        {
            _context = context;
        }
        public async Task<List<Stranger>> GetAllAsync(QueryObjectStranger query)
        {
            var Strangers = _context.Strangers.Include(c => c.AccessLogs).AsQueryable();

            if (!string.IsNullOrWhiteSpace(query.Name))
            {
                Strangers = Strangers.Where(p => p.Name.Contains(query.Name));
            }

            if (query.BirthDay.HasValue)
            {
                Strangers = Strangers.Where(p => p.BirthDay.Date == query.BirthDay.Value.Date);
            }

            if (!string.IsNullOrWhiteSpace(query.Address))
            {
                Strangers = Strangers.Where(p => p.Address.Contains(query.Address));
            }

            if (!string.IsNullOrWhiteSpace(query.SortBy))
            {
                if (query.SortBy.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    Strangers = query.IsDescending? Strangers.OrderByDescending(p=>p.Name) : Strangers.OrderBy(p=>p.Name);
                }
            }

            var skipnumber = (query.PageNumber - 1)*query.PageSize; 
            
            return await Strangers.Skip(skipnumber).Take(query.PageSize).ToListAsync(); 
        }

        public async Task<Stranger?> GetByIdAsync(int id)
        {
            return await _context.Strangers.Include(l => l.AccessLogs).FirstOrDefaultAsync(x => x.Id == id);
        }   

        public async Task<Stranger> CreateAsync(Stranger StrangerModel)
        {
            await _context.Strangers.AddAsync(StrangerModel);
            await _context.SaveChangesAsync();
            return StrangerModel;
        }

        public async Task<Stranger?> UpdateAsync(int id, Stranger StrangerDto)
        {
            var existingStranger = await _context.Strangers.FindAsync(id);

            if (existingStranger == null)
            {
                return null;
            }

            existingStranger.Name = StrangerDto.Name;
            existingStranger.BirthDay = StrangerDto.BirthDay;
            existingStranger.Address = StrangerDto.Address;
            existingStranger.Company = StrangerDto.Company;

            await _context.SaveChangesAsync();
            return existingStranger;
        }

        public async Task<Stranger?> DeleteAsync(int id)
        {
            var strangerModel = await _context.Strangers.FirstOrDefaultAsync(x => x.Id == id);

            if (strangerModel == null)
            {
                return null;
            }

            _context.Strangers.Remove(strangerModel);
            await _context.SaveChangesAsync();
            return strangerModel;
        }

        public async Task<bool> StrangerExistsAsync(int id)
        {
            return await _context.Strangers.AnyAsync(p => p.Id == id);
        }
    
    }
}