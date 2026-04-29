using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Dtos.Embedding;
using MyfirstASP_NET.Models;

namespace MyfirstASP_NET.Interfaces
{
    public interface IPeopleRepository
    {

        Task<bool> PeopleExistsAsync(int id);
        Task<People?> GetByIdAsync(int id);

    }
}