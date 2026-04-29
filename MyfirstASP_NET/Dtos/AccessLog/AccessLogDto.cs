using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using System.ComponentModel.DataAnnotations;

namespace MyfirstASP_NET.Dtos.AccessLog
{
    public class AccessLogDto
    {
        public int Id { get; set; }
        public int PeopleId { get; set; }
        public DateTime LogTime { get; set; }
        public string? AccessType { get; set; }
        public string? ImageSnapshot { get; set; }
        
        // For UI display - determined by checking if People is Employee or Stranger
        public string? PersonName { get; set; }
        public string? PersonType { get; set; }
        public string? Direction { get; set; }
    }
}