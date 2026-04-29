using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Dtos.AccessLog;
using System.ComponentModel.DataAnnotations;

namespace MyfirstASP_NET.Dtos.Stranger
{
    public class StrangerDto
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
        public string? Company { get; set; } = string.Empty;

        public ICollection<AccessLogDto> AccessLogs { get; set; } = new List<AccessLogDto>();
    }
}