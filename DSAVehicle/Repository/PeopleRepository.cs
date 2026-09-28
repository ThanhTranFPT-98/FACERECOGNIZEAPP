using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Interfaces;
using DSAVehicle.Data;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.Repository
{
    public class PeopleRepository : IPeopleRepository
    {
        private readonly ApplicationDBContext _context;

        public PeopleRepository(ApplicationDBContext context)
        {
            _context = context;
        }

        public async Task<bool> PeopleExistsAsync(int id)
        {
            return await _context.People.AnyAsync(p => p.Id == id);
        }

        public async Task<People?> GetByIdAsync(int id)
        {
            return await _context.People.FindAsync(id);
        }
    }
}