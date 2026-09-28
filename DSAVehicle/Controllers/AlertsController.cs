using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DSAVehicle.Data;
using DSAVehicle.Models;

namespace DSAVehicle.Controllers
{
    [Route("api/Alerts")]
    [ApiController]
    public class AlertsController : ControllerBase
    {
        private readonly ApplicationDBContext _context;

        public AlertsController(ApplicationDBContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Get all alerts (strangers detected)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] bool? unresolvedOnly = false)
        {
            try
            {
                // For now, alerts are based on strangers with their first access log
                var strangers = await _context.Strangers
                    .Include(s => s.AccessLogs)
                    .Where(s => s.AccessLogs.Any()) // Only strangers with access logs
                    .OrderByDescending(s => s.AccessLogs.Max(a => a.LogTime))
                    .Select(s => new
                    {
                        Id = s.Id,
                        Type = "StrangerDetected",
                        Title = "Stranger Detected",
                        Message = $"Unknown person detected",
                        PersonName = s.Name,
                        PersonId = s.Id,
                        Timestamp = s.AccessLogs.OrderByDescending(a => a.LogTime).First().LogTime,
                        ImagePath = s.AccessLogs.OrderByDescending(a => a.LogTime).First().ImageSnapshot,
                        IsResolved = false
                    })
                    .ToListAsync();

                return Ok(strangers);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AlertsController] Error in GetAll: {ex.Message}");
                Console.WriteLine($"[AlertsController] StackTrace: {ex.StackTrace}");
                return StatusCode(500, new { Error = "Failed to fetch alerts", Message = ex.Message });
            }
        }

        /// <summary>
        /// Get recent alerts (last 24 hours)
        /// </summary>
        [HttpGet("Recent")]
        public async Task<IActionResult> GetRecent([FromQuery] int hours = 24)
        {
            try
            {
                var since = DateTime.Now.AddHours(-hours);

                var recentStrangers = await _context.Strangers
                    .Include(s => s.AccessLogs)
                    .Where(s => s.AccessLogs.Any(a => a.LogTime >= since))
                    .OrderByDescending(s => s.AccessLogs.Max(a => a.LogTime))
                    .Select(s => new
                    {
                        Id = s.Id,
                        Type = "StrangerDetected",
                        Title = "Stranger Detected",
                        Message = $"Unknown person detected",
                        PersonName = s.Name,
                        PersonId = s.Id,
                        Timestamp = s.AccessLogs.OrderByDescending(a => a.LogTime).First().LogTime,
                        ImagePath = s.AccessLogs.OrderByDescending(a => a.LogTime).First().ImageSnapshot,
                        IsResolved = false
                    })
                    .Take(20)
                    .ToListAsync();

                return Ok(recentStrangers);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AlertsController] Error in GetRecent: {ex.Message}");
                return StatusCode(500, new { Error = "Failed to fetch recent alerts", Message = ex.Message });
            }
        }

        /// <summary>
        /// Get alert statistics
        /// </summary>
        [HttpGet("Stats")]
        public async Task<IActionResult> GetStats()
        {
            var today = DateTime.Today;
            var yesterday = today.AddDays(-1);

            var alertsToday = await _context.Strangers
                .Include(s => s.AccessLogs)
                .Where(s => s.AccessLogs.Any(a => a.LogTime >= today))
                .CountAsync();

            var alertsYesterday = await _context.Strangers
                .Include(s => s.AccessLogs)
                .Where(s => s.AccessLogs.Any(a => a.LogTime >= yesterday && a.LogTime < today))
                .CountAsync();

            var totalUnresolved = await _context.Strangers
                .CountAsync();

            return Ok(new
            {
                AlertsToday = alertsToday,
                AlertsYesterday = alertsYesterday,
                TotalUnresolved = totalUnresolved
            });
        }

        /// <summary>
        /// Mark alert as resolved (convert stranger to employee)
        /// </summary>
        [HttpPost("{id}/Resolve")]
        public async Task<IActionResult> ResolveAlert(int id, [FromBody] ResolveAlertRequest request)
        {
            var stranger = await _context.Strangers
                .Include(s => s.AccessLogs)
                .FirstOrDefaultAsync(s => s.Id == id);
                
            if (stranger == null)
            {
                return NotFound("Stranger not found");
            }

            // Create new employee from stranger
            var employee = new Employee
            {
                Name = request.Name ?? stranger.Name,
                BirthDay = stranger.BirthDay,
                Address = stranger.Address,
                Role = request.Role ?? "Staff",
                Salary = request.Salary ?? 0,
                StartDate = DateTime.Now
            };

            // Remove stranger and add employee
            _context.Strangers.Remove(stranger);
            _context.Employees.Add(employee);
            await _context.SaveChangesAsync();

            // TODO: Tạo embedding cho employee từ ảnh snapshot
            // Lấy ảnh gần nhất từ AccessLog
            var latestLog = stranger.AccessLogs
                .OrderByDescending(a => a.LogTime)
                .FirstOrDefault();

            if (latestLog != null && !string.IsNullOrEmpty(latestLog.ImageSnapshot))
            {
                // Lưu ý: Cần gọi Python API để tạo embedding từ ảnh
                // Đây là placeholder - cần implement thêm
                Console.WriteLine($"[ResolveAlert] Employee {employee.Id} created. Snapshot: {latestLog.ImageSnapshot}");
                Console.WriteLine($"[ResolveAlert] Please manually create embedding for this employee.");
            }

            return Ok(new
            {
                Message = "Alert resolved. Stranger converted to employee. Please create embedding for face recognition.",
                EmployeeId = employee.Id,
                EmployeeName = employee.Name,
                SnapshotPath = latestLog?.ImageSnapshot
            });
        }
    }

    public class ResolveAlertRequest
    {
        public string? Name { get; set; }
        public string? Role { get; set; }
        public decimal? Salary { get; set; }
    }
}
