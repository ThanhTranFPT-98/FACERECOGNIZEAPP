using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MyfirstASP_NET.Interfaces;
using MyfirstASP_NET.Models;

namespace MyfirstASP_NET.Data
{
    public class ApplicationDBContext : DbContext 
    {
        public ApplicationDBContext(DbContextOptions dbContextOptions)
        : base(dbContextOptions)
        {
        
        }

        public DbSet<Employee> Employees { get;set; }

        public DbSet<Stranger> Strangers { get;set; }

        public DbSet<People> People { get;set; }

        public DbSet<AccessLog> AccessLogs { get;set; }
        
        public DbSet<Embedding> Embeddings { get;set; }


        //public static implicit operator ApplicationDBContext(IPeopleRepository v)
        //{
        //    throw new NotImplementedException();
        //}
    }
}
