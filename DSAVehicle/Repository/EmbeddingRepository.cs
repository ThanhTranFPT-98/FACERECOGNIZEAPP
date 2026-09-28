using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Interfaces;
using DSAVehicle.Data;
using Microsoft.EntityFrameworkCore;
using DSAVehicle.Dtos.Embedding;

namespace DSAVehicle.Repository
{
    public class EmbeddingRepository : IEmbeddingRepository 
    {
        private readonly ApplicationDBContext _context;
        public EmbeddingRepository(ApplicationDBContext context)
        {
            _context = context;
        }

        public async Task<List<Embedding>> GetAllAsync()
        {
            return await _context.Embeddings.ToListAsync();
        }

        public async Task<Embedding?> GetByIdAsync(int Id)
        {
            return await _context.Embeddings.FirstOrDefaultAsync(x => x.Id == Id);
        }

        public async Task<Embedding> CreateAsync(Embedding embeddingModel)
        {
            await _context.Embeddings.AddAsync(embeddingModel);
            await _context.SaveChangesAsync();
            return embeddingModel;
        }

        public async Task<Embedding?> UpdateAsync(int id, Embedding UpdateEmbeddingDto)
        {
            var existingEmbedding = await _context.Embeddings.FindAsync(id);

            if (existingEmbedding == null)
            {
                return null;
            }

            existingEmbedding.Vector = UpdateEmbeddingDto.Vector;
            await _context.SaveChangesAsync();
            return existingEmbedding;
        }

        public async Task<Embedding?> DeleteAsync(int Id)
        {
            var embeddingModel = await _context.Embeddings.FirstOrDefaultAsync(x => x.Id == Id);

            if (embeddingModel == null)
            {
                return null;
            }

            _context.Remove(embeddingModel);
            await _context.SaveChangesAsync();
            return embeddingModel;
        }

        public async Task<bool> EmbeddingExistsAsync( int Id)
        {
              return await _context.Embeddings.AnyAsync(e => e.Id == Id);
        }
    }
}
