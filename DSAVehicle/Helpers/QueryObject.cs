using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DSAVehicle.Helpers
{
    public class QueryObjectEmployee
    {
        public string? Name { get; set; } = null;
        public DateTime? BirthDay { get; set; } = null;
        public string? Address { get; set; } = null;

        public string? SortBy { get; set; } = null;
        public bool IsDescending { get; set; } = false;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }

        public class QueryObjectStranger
    {
        public string? Name { get; set; } = null;
        public DateTime? BirthDay { get; set; } = null;
        public string? Address { get; set; } = null;

        public string? Company { get; set; } = null;

        public string? SortBy { get; set; } = null;
        public bool IsDescending { get; set; } = false;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }
}