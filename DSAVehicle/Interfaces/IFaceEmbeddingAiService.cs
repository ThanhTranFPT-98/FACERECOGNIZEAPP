using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Dtos.Embedding;

namespace DSAVehicle.Interfaces
{
    public interface IFaceEmbeddingAiService
    {
        Task<PythonEmbeddingResponseDto?> GenerateEmbeddingAsync(IFormFile imageFile);
    }
}