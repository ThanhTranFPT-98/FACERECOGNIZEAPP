using System;
using System.Collections.Generic;

namespace MyfirstASP_NET.Models
{
    public class Embedding 
    {       
        public int Id { get; set; }
        public int? PeopleId { get; set; }
        public People? People { get; set; }
        public string Vector { get; set; } = string.Empty;
    }
}
