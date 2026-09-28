using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DSAVehicle.Models
{
    public class AccessLog
    {
        public int Id { get; set; }
        public int PeopleId { get; set; }
        public People People { get; set; } = null!;
        public DateTime LogTime { get; set; }
        public string AccessType { get; set; } = string.Empty;
        public string? ImageSnapshot { get; set; }
    }
}