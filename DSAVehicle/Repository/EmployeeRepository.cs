using Microsoft.EntityFrameworkCore;
using DSAVehicle.Data;
using DSAVehicle.Dtos.Employee;
using DSAVehicle.Interfaces;
using DSAVehicle.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Helpers;

namespace DSAVehicle.Repository
{
    public class EmployeeRepository : IEmployeeRepository
    {
        private readonly ApplicationDBContext _context;
        public  EmployeeRepository (ApplicationDBContext context)
        {
            _context = context;
        }
        public async Task<List<Employee>> GetAllAsync(QueryObjectEmployee query)
        {
            var Employees = _context.Employees.Include(c => c.AccessLogs).AsQueryable();

            if (!string.IsNullOrWhiteSpace(query.Name))
            {
                Employees = Employees.Where(p => p.Name.Contains(query.Name));
            }

            if (query.BirthDay.HasValue)
            {
                Employees = Employees.Where(p => p.BirthDay.Date == query.BirthDay.Value.Date);
            }

            if (!string.IsNullOrWhiteSpace(query.Address))
            {
                Employees = Employees.Where(p => p.Address.Contains(query.Address));
            }

            if (!string.IsNullOrWhiteSpace(query.SortBy))
            {
                if (query.SortBy.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    Employees = query.IsDescending? Employees.OrderByDescending(p=>p.Name) : Employees.OrderBy(p=>p.Name);
                }
            }

            var skipnumber = (query.PageNumber - 1)*query.PageSize; 
            
            return await Employees.Skip(skipnumber).Take(query.PageSize).ToListAsync(); 
        }

        public async Task<Employee?> GetByIdAsync(int Id)
        {
            return await _context.Employees.Include(c => c.AccessLogs).FirstOrDefaultAsync(x => x.Id == Id);
        }
        public async Task<Employee> CreateAsync(Employee EmployeeModel)
        {
            await _context.Employees.AddAsync(EmployeeModel);
            await _context.SaveChangesAsync();
            return EmployeeModel;
        }

        public async Task<Employee?> UpdateAsync(int Id, Employee EmployeeDto)
        {
            var existingEmployee = await _context.Employees.FindAsync(Id);

            if (existingEmployee == null)
            {
                return null;
            }

            existingEmployee.Name = EmployeeDto.Name;
            existingEmployee.BirthDay = EmployeeDto.BirthDay;
            existingEmployee.Address = EmployeeDto.Address;
            existingEmployee.Role = EmployeeDto.Role;
            existingEmployee.Salary = EmployeeDto.Salary;
            existingEmployee.StartDate = EmployeeDto.StartDate;

            await _context.SaveChangesAsync();

            return existingEmployee;
        }

        public async Task<Employee?> DeleteAsync(int Id)
        {
            var stockModel = await _context.Employees.FirstOrDefaultAsync(x => x.Id == Id);

            if (stockModel == null)
            {
                return null;
            }

            _context.Employees.Remove(stockModel);
            await _context.SaveChangesAsync();
            return stockModel;
        }

        public async Task<bool> EmployeeExistsAsync(int Id)
        {
            return await _context.Employees.AnyAsync(p => p.Id == Id);
        }
    }
}
