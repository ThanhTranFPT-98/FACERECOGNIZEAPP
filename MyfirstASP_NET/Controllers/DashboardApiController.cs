using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyfirstASP_NET.Data;
using MyfirstASP_NET.Interfaces;

namespace MyfirstASP_NET.Controllers
{
    [Route("api/Dashboard")]
    [ApiController]
    public class DashboardApiController : ControllerBase
    {
        private readonly ApplicationDBContext _context;
        private readonly IEmployeeRepository _employeeRepo;
        private readonly IStrangerRepository _strangerRepo;
        private readonly IAccessLogRepository _accessLogRepo;

        public DashboardApiController(
            ApplicationDBContext context,
            IEmployeeRepository employeeRepo,
            IStrangerRepository strangerRepo,
            IAccessLogRepository accessLogRepo)
        {
            _context = context;
            _employeeRepo = employeeRepo;
            _strangerRepo = strangerRepo;
            _accessLogRepo = accessLogRepo;
        }

        /// <summary>
        /// Get dashboard statistics
        /// </summary>
        [HttpGet("Stats")]
        public async Task<IActionResult> GetStats()
        {
            var today = DateTime.Today;
            var yesterday = today.AddDays(-1);

            // Total employees
            var totalEmployees = await _context.Employees.CountAsync();

            // New strangers today (based on first access log)
            var strangersToday = await _context.Strangers
                .Where(s => s.AccessLogs.Any(a => a.LogTime >= today))
                .CountAsync();

            // Access logs today
            var accessLogsToday = await _context.AccessLogs
                .Where(a => a.LogTime >= today)
                .CountAsync();

            // Access logs yesterday for comparison
            var accessLogsYesterday = await _context.AccessLogs
                .Where(a => a.LogTime >= yesterday && a.LogTime < today)
                .CountAsync();

            // Recent alerts (strangers with access logs in last 24 hours)
            var recentAlerts = await _context.Strangers
                .Where(s => s.AccessLogs.Any(a => a.LogTime >= DateTime.Now.AddHours(-24)))
                .OrderByDescending(s => s.AccessLogs.Max(a => a.LogTime))
                .Take(5)
                .Select(s => new
                {
                    Id = s.Id,
                    Type = "StrangerDetected",
                    Message = $"Stranger detected at {s.AccessLogs.OrderByDescending(a => a.LogTime).First().LogTime:HH:mm}",
                    Timestamp = s.AccessLogs.OrderByDescending(a => a.LogTime).First().LogTime,
                    IsResolved = false
                })
                .ToListAsync();

            // Access logs by hour for chart
            var hourlyStats = await _context.AccessLogs
                .Where(a => a.LogTime >= today)
                .GroupBy(a => a.LogTime.Hour)
                .Select(g => new
                {
                    Hour = g.Key,
                    Count = g.Count()
                })
                .OrderBy(x => x.Hour)
                .ToListAsync();

            // Fill missing hours with 0
            var completeHourlyStats = Enumerable.Range(0, 24)
                .Select(h => new
                {
                    Hour = h,
                    Count = hourlyStats.FirstOrDefault(x => x.Hour == h)?.Count ?? 0
                })
                .ToList();

            // Recent access logs
            var recentAccessLogs = await _context.AccessLogs
                .Include(a => a.People)
                .OrderByDescending(a => a.LogTime)
                .Take(10)
                .Select(a => new
                {
                    Id = a.Id,
                    PersonName = a.People.Name,
                    PersonType = a.People is Models.Employee ? "Employee" : "Stranger",
                    Direction = a.AccessType,
                    Timestamp = a.LogTime,
                    SnapshotPath = a.ImageSnapshot
                })
                .ToListAsync();

            return Ok(new
            {
                TotalEmployees = totalEmployees,
                StrangersToday = strangersToday,
                AccessLogsToday = accessLogsToday,
                AccessLogsYesterday = accessLogsYesterday,
                RecentAlerts = recentAlerts,
                HourlyStats = completeHourlyStats,
                RecentAccessLogs = recentAccessLogs
            });
        }

        /// <summary>
        /// Get hourly access stats for chart
        /// </summary>
        [HttpGet("HourlyStats")]
        public async Task<IActionResult> GetHourlyStats([FromQuery] DateTime? date)
        {
            var targetDate = date ?? DateTime.Today;
            var nextDate = targetDate.AddDays(1);

            var hourlyStats = await _context.AccessLogs
                .Where(a => a.LogTime >= targetDate && a.LogTime < nextDate)
                .GroupBy(a => a.LogTime.Hour)
                .Select(g => new
                {
                    Hour = g.Key,
                    Count = g.Count()
                })
                .OrderBy(x => x.Hour)
                .ToListAsync();

            // Fill missing hours
            var completeStats = Enumerable.Range(0, 24)
                .Select(h => new
                {
                    Hour = h,
                    Label = $"{h:D2}:00",
                    Count = hourlyStats.FirstOrDefault(x => x.Hour == h)?.Count ?? 0
                })
                .ToList();

            return Ok(completeStats);
        }

        /// <summary>
        /// Get daily stats for last 7 days
        /// </summary>
        [HttpGet("WeeklyStats")]
        public async Task<IActionResult> GetWeeklyStats()
        {
            var endDate = DateTime.Today.AddDays(1);
            var startDate = endDate.AddDays(-7);

            var dailyStats = await _context.AccessLogs
                .Where(a => a.LogTime >= startDate && a.LogTime < endDate)
                .GroupBy(a => a.LogTime.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    Count = g.Count()
                })
                .OrderBy(x => x.Date)
                .ToListAsync();

            // Fill missing dates
            var completeStats = Enumerable.Range(0, 7)
                .Select(i =>
                {
                    var date = startDate.AddDays(i).Date;
                    return new
                    {
                        Date = date,
                        Label = date.ToString("ddd"),
                        Count = dailyStats.FirstOrDefault(x => x.Date == date)?.Count ?? 0
                    };
                })
                .ToList();

            return Ok(completeStats);
        }
    }
}
