using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Interfaces;
using MyfirstASP_NET.Data;
using Microsoft.EntityFrameworkCore;

namespace MyfirstASP_NET.Repository
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