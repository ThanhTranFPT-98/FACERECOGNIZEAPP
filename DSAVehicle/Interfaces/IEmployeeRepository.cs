using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.Employee;
using DSAVehicle.Helpers;

namespace DSAVehicle.Interfaces
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
