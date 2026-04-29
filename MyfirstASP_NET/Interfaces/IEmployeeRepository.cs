using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Dtos.Employee;
using MyfirstASP_NET.Helpers;

namespace MyfirstASP_NET.Interfaces
{
    public interface IEmployeeRepository
    {
        Task<List<Employee>> GetAllAsync(QueryObjectEmployee query);

        Task<Employee?> GetByIdAsync(int id);

        Task<Employee> CreateAsync(Employee employeeModel);

        Task<Employee?> UpdateAsync(int id, Employee employeeDto);

        Task<Employee?> DeleteAsync(int id);

        Task<bool> EmployeeExistsAsync(int id); 
    }
}
