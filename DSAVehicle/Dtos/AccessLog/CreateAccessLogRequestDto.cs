using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DSAVehicle.Dtos.AccessLog
{
    public class CreateAccessLogRequestDto
    {
        public DateTime LogTime { get; set; }
        public string AccessType { get; set; } = string.Empty;
        public string? ImageSnapshot { get; set; }
        
    }
}