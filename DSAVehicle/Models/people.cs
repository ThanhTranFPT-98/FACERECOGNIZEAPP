using System;
using System.Collections.Generic;
using DSAVehicle.Models;

namespace DSAVehicle.Models
{
    public class People

    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime BirthDay { get; set; }
        public string Address { get; set; } = string.Empty;
        public ICollection<Embedding> Embeddings { get; set; } = new List<Embedding>();
        public ICollection<AccessLog> AccessLogs { get; set; } = new List<AccessLog>();
    }
}
