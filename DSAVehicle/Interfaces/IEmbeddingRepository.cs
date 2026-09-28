using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.Embedding;

namespace DSAVehicle.Interfaces
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
