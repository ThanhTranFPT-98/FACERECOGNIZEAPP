using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Dtos.Embedding;

namespace MyfirstASP_NET.Interfaces
{
    public interface IFaceEmbeddingAiService
    {
        Task<PythonEmbeddingResponseDto?> GenerateEmbeddingAsync(IFormFile imageFile);
    }
}