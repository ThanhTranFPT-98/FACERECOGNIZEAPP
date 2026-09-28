using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DSAVehicle.Interfaces;
using DSAVehicle.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DSAVehicle.Data
{
    public class ApplicationDBContext : DbContext 
    {
        private readonly IDataProtector templateProtector;
        public ApplicationDBContext(DbContextOptions<ApplicationDBContext> dbContextOptions, IDataProtectionProvider protection)
        : base(dbContextOptions)
        {
            templateProtector = protection.CreateProtector("Parking.FaceTemplates.v1");
        
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Embedding>().Property(x => x.Vector).HasConversion(
                vector => "protected:v1:" + templateProtector.Protect(vector),
                stored => stored.StartsWith("protected:v1:") ? templateProtector.Unprotect(stored.Substring(13)) : stored);
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
