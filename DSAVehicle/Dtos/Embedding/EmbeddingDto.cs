using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace DSAVehicle.Dtos.Embedding
{
    public class EmbeddingDto
    {
        public int Id { get; set; }
        public int? PeopleId { get; set; }
        public string Vector { get; set; } = string.Empty;
    }
}
