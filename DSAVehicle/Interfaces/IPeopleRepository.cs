using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Dtos.Embedding;
using DSAVehicle.Models;

namespace DSAVehicle.Interfaces
{
    public interface IPeopleRepository
    {

        Task<bool> PeopleExistsAsync(int id);
        Task<People?> GetByIdAsync(int id);

    }
}