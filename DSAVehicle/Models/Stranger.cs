using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DSAVehicle.Models
{
    public class Stranger : People 
    {
        public string Company { get; set; } = string.Empty;
    }
}