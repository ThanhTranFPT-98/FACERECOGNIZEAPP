using System.ComponentModel.DataAnnotations;

namespace MyfirstASP_NET.Dtos.Embedding
{
    public class CreateEmbeddingDto
    {
        [Required]
        public IFormFile ImageFile { get; set; } = default!;
    }
}
