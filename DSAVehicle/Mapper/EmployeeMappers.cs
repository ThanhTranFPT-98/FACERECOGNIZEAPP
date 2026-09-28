using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.Employee;

namespace DSAVehicle.Mapper
{
    public static class EmployeeMappers
    {
        public static EmployeeDto ToEmployeeDto(this Employee EmployeeModel)
        {
            return new EmployeeDto
            {
                PeopleId = EmployeeModel.Id,
                Name = EmployeeModel.Name,
                BirthDay = EmployeeModel.BirthDay,
                Address = EmployeeModel.Address,
                Role = EmployeeModel.Role,
                Salary = EmployeeModel.Salary ,
                StartDate = EmployeeModel.StartDate,
                AccessLogs = EmployeeModel.AccessLogs.Select(a => a.ToAccessLogDto()).ToList(),
            };
        }

        public static Employee ToEmployeeFromCreateDTO(this CreateEmployeeRequestDto EmployeeDto)
        {
            return new Employee
            {
                Name = EmployeeDto.Name,
                BirthDay = EmployeeDto.BirthDay,
                Address = EmployeeDto.Address,
                Role = EmployeeDto.Role,
                Salary = EmployeeDto.Salary,
                StartDate = EmployeeDto.StartDate,
            };
        }

        public static Employee ToEmployeeFromUpdate(this UpdateEmployeeRequestDto EmployeeDto)
        {
            return new Employee
            {
                Name = EmployeeDto.Name,
                BirthDay = EmployeeDto.BirthDay,
                Address = EmployeeDto.Address,
                Role = EmployeeDto.Role,
                Salary = EmployeeDto.Salary,
                StartDate = EmployeeDto.StartDate,
            };
        }
        

    }
}
