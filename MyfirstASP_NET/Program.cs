using Microsoft.EntityFrameworkCore;
using MyfirstASP_NET.Data;
using MyfirstASP_NET.Interfaces;
using MyfirstASP_NET.Repository;
using System;  
using System.Collections.Generic;  
using MyfirstASP_NET.Services;
using MyfirstASP_NET.Hubs;

namespace MyfirstASP_NET

{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.AddControllers().AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
            });

            builder.Services.AddDbContext<ApplicationDBContext>(options =>
            {
                options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
            });

            builder.Services.AddScoped<IEmployeeRepository, EmployeeRepository>();
            builder.Services.AddScoped<IEmbeddingRepository, EmbeddingRepository>();
            builder.Services.AddScoped<IStrangerRepository, StrangerRepository>();
            builder.Services.AddScoped<IAccessLogRepository, AccessLogRepository>();
            builder.Services.AddScoped<IPeopleRepository, PeopleRepository>();
            builder.Services.AddHttpClient<IFaceEmbeddingAiService, FaceEmbeddingAiService>(client =>
                {
                    client.BaseAddress = new Uri("http://127.0.0.1:8000/");
                });

            builder.Services.AddHttpClient<IFaceRecognitionService, FaceRecognitionService>(client =>
                {
                    client.BaseAddress = new Uri("http://127.0.0.1:8000/");
                });
            builder.Services.AddScoped<IAccessLogProcessingService, AccessLogProcessingService>();
            builder.Services.AddSignalR(options =>
            {
                // Increase maximum message size to handle large base64 images
                options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
            });
            builder.Services.AddScoped<NotificationHub, NotificationHub>();
            builder.Services.AddControllersWithViews();

            // Add CORS for React app
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("ReactApp", policy =>
                {
                    policy.WithOrigins("http://localhost:3000", "http://localhost:5173")
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
            });

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            // Enable CORS before auth
            app.UseCors("ReactApp");

            app.UseAuthorization();
            
            // Enable static files for snapshots
            app.UseStaticFiles();
            
            // Ensure snapshots directory exists
            var snapshotsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "snapshots");
            if (!Directory.Exists(snapshotsPath))
            {
                Directory.CreateDirectory(snapshotsPath);
                Console.WriteLine($"[Program] Created snapshots directory: {snapshotsPath}");
            }

            app.MapControllers();

            app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Dashboard}/{action=Index}/{id?}");

            // Map SignalR hub
            app.MapHub<NotificationHub>("/notificationHub");

            app.Run();
        }
    }
}
