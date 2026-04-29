using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyfirstASP_NET.Models
{
    public class Stranger : People 
    {
        public string Company { get; set; } = string.Empty;
    }
}