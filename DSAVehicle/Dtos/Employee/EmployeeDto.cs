using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.AccessLog;
using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.Dtos.Employee
{
    public class EmployeeDto
    {
        public int PeopleId { get; set; }
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public DateTime BirthDay { get; set; }

        [Required]
        [StringLength(10)]
        public string Sex { get; set; } = string.Empty;

        [StringLength(200)]
        public string Address { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Role { get; set; } = string.Empty;

        [Range(0, 1000000000)] 
        public decimal Salary { get; set; }

        [Required]
        public DateTime StartDate { get; set; }

        public ICollection<AccessLogDto> AccessLogs { get; set; } = new List<AccessLogDto>();
    }
}
