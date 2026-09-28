using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.Stranger;
using DSAVehicle.Helpers;


namespace DSAVehicle.Interfaces
{
    public interface IStrangerRepository
    {
        Task<List<Stranger>> GetAllAsync(QueryObjectStranger query);

        Task<Stranger?> GetByIdAsync(int id);

        Task<Stranger> CreateAsync(Stranger StrangerModel);

        Task<Stranger?> UpdateAsync(int id, Stranger StrangerDto);

        Task<Stranger?> DeleteAsync(int id);

        Task<bool> StrangerExistsAsync(int id); 
    }
}