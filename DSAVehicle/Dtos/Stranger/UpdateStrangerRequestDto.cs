using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.AccessLog;
using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.Dtos.Stranger
{
    public class UpdateStrangerRequestDto
    {
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
        public string Company { get; set; } = string.Empty;
        
    }
}