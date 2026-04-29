using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyfirstASP_NET.Models
{
    public class Employee : People 
    {
        public string Role { get; set; } = string.Empty;

        public decimal Salary { get; set; }

        public DateTime StartDate { get; set; }

    }
}