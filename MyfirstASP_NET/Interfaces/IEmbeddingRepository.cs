using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Dtos.Embedding;

namespace MyfirstASP_NET.Interfaces
{
    public interface IEmbeddingRepository
    {
        Task<List<Embedding>> GetAllAsync();
        Task<Embedding?> GetByIdAsync(int id);

        Task<Embedding> CreateAsync(Embedding embeddingModel);

        Task<Embedding?> UpdateAsync(int id, Embedding embeddingModel);

        Task<Embedding?> DeleteAsync(int id);
    }
}
