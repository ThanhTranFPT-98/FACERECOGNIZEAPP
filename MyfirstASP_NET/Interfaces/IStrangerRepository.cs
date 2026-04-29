using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Dtos.Stranger;
using MyfirstASP_NET.Helpers;


namespace MyfirstASP_NET.Interfaces
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