using System.ComponentModel.DataAnnotations;

namespace DSAVehicle.Dtos.Embedding
{
    public class CreateEmbeddingDto
    {
        [Required]
        public IFormFile ImageFile { get; set; } = default!;
    }
}
