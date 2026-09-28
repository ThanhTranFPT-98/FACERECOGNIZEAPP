using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Interfaces;
using DSAVehicle.Dtos.FaceRecognition;
using DSAVehicle.Models;
using DSAVehicle.Data;
using Microsoft.EntityFrameworkCore;
using DSAVehicle.Repository;
using DSAVehicle.Mapper;
using DSAVehicle.Dtos.AccessLog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using DSAVehicle.Hubs;

namespace DSAVehicle.Services
{
    public class AccessLogProcessingService : IAccessLogProcessingService
    {
        private readonly ApplicationDBContext _context;
        private readonly AccessLogRepository _accessLogRepository;
        private readonly IHubContext<NotificationHub> _hubContext;
        public AccessLogProcessingService(ApplicationDBContext context, IHubContext<NotificationHub> hubContext)
        {
            _context = context;
            _accessLogRepository = new AccessLogRepository(_context);
            _hubContext = hubContext;

        }

        public async Task<List<AccessLogDto>> ProcessAccessLogsAsync(RecognizeFrameResultsDto resultsDto, IFormFile imageFile, byte[]? imageBytes = null)
        {
            Console.WriteLine($"[ProcessAccessLogs] Called with {resultsDto.TotalFaces} faces, imageBytes: {(imageBytes != null ? $"{imageBytes.Length} bytes" : "null")}");
            
            var createdLogs = new List<AccessLogDto>();
            // nếu không có người → bỏ qua
            if (resultsDto.TotalFaces == 0)
            {
                return createdLogs;
            }
            // Xử lý người đã nhận diện (RecognizedFaces - đã có trong DB)
            foreach (var recognizedFace in resultsDto.RecognizedFaces)
            {
                if (await ShouldSkipDuplicateAsync(recognizedFace.PeopleId, resultsDto.CaptureTime, resultsDto.CameraId))
                {
                    continue; // bỏ qua log trùng lặp
                }

                var accessType = await GetNextAccessTypeAsync(recognizedFace.PeopleId, resultsDto.CaptureTime, resultsDto.CameraId);
                var ImageUrl = await SaveSnapshotAsync(imageBytes ?? new byte[0], resultsDto.CaptureTime, resultsDto.CameraId);
                var _accessLogModel = recognizedFace.ToAccessLogFromRecognizedFace (resultsDto.CaptureTime, accessType, ImageUrl);
                var createdLog = await _accessLogRepository.CreateAsync(_accessLogModel);

                // map sang DTO để trả UI
                createdLogs.Add(createdLog.ToAccessLogDto());
            }

            // Xử lý ngườ lạ chưa đăng ký (UnknownFaces)
            // Unknown = Chưa có trong DB, chưa có embedding
            // Cần đăng ký thủ công qua UI (tạo Employee hoặc Stranger mới)
            if (resultsDto.UnknowFaces.Count > 0)
            {
                Console.WriteLine($"[ProcessAccessLogs] {resultsDto.UnknowFaces.Count} unknown face(s) detected - NOT in database!");
                
                // Lưu ảnh để có thể đăng ký sau
                var imageUrl = await SaveSnapshotAsync(imageBytes ?? new byte[0], resultsDto.CaptureTime, resultsDto.CameraId);
                
                // Thông báo có ngườ lạ cho tất cả clients
                await _hubContext.Clients.All.SendAsync("UnknownDetected", new
                {
                    CameraId = resultsDto.CameraId,
                    Timestamp = resultsDto.CaptureTime,
                    UnknownCount = resultsDto.UnknowFaces.Count,
                    Message = $"Phát hiện {resultsDto.UnknowFaces.Count} ngườ lạ chưa đăng ký!",
                    SnapshotPath = imageUrl,
                    RequiresRegistration = true
                });

                // KHÔNG tự động tạo Stranger - để ngườ dùng đăng ký thủ công
                // Qua UI: Employees (Add) hoặc Strangers (Add)
                
                Console.WriteLine($"[ProcessAccessLogs] Unknown faces require manual registration via UI");
            }

            return createdLogs;

        }

        public async Task<bool> ShouldSkipDuplicateAsync(int peopleId, DateTime captureTime, string cameraId)
        {
                var lastLog = await _context.AccessLogs
                .Where(log => log.PeopleId == peopleId)
                .OrderByDescending(log => log.LogTime)
                .FirstOrDefaultAsync();
                
                double secondsDiff = lastLog != null ? (captureTime - lastLog.LogTime).TotalSeconds : double.MaxValue;
                Console.WriteLine($"Last log time: {lastLog?.LogTime}, Capture time: {captureTime}, Seconds difference: {secondsDiff}");
                if (lastLog != null && secondsDiff < 20)
                {
                    return true ; // bỏ qua, chưa ghi log mới
                }

                return false; // không bỏ qua, ghi log mới
        }
        public async Task<string> GetNextAccessTypeAsync(int peopleId, DateTime captureTime, string cameraId)
        {
            var lastLog = await _context.AccessLogs
                .Where(log => log.PeopleId == peopleId)
                .OrderByDescending(log => log.LogTime)
                .FirstOrDefaultAsync();

            string nextAccessType;

            if (lastLog == null)
            {
                nextAccessType = "CheckIn";
            }
            else if (lastLog.AccessType == "CheckIn")
            {
                nextAccessType = "CheckOut";
            }
            else
            {
                nextAccessType = "CheckIn";
            }
            return nextAccessType;
        }    

        private async Task<string> SaveSnapshotAsync(byte[] imageBytes, DateTime captureTime, string cameraId)
        {
            Console.WriteLine($"[SaveSnapshotAsync] Called with {imageBytes?.Length ?? 0} bytes");
            
            try
            {
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    Console.WriteLine("[AccessLogProcessingService] ERROR: imageBytes is null or empty");
                    return null;
                }

                // 1. Đường dẫn thư mục lưu
                var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "snapshots");

                // 2. Tạo thư mục nếu chưa có
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                // 3. Tạo tên file unique
                var guidStr = Guid.NewGuid().ToString("N").Substring(0, 8);
                var timeStr = captureTime.ToString("yyyyMMdd_HHmmssfff");
                var fileName = $"{cameraId}_{timeStr}_{guidStr}.jpg";
                Console.WriteLine($"[SaveSnapshotAsync] Generated filename: {fileName}");

                // 4. Full path vật lý
                var filePath = Path.Combine(folderPath, fileName);
                Console.WriteLine($"[SaveSnapshotAsync] Full path: {filePath}");

                // 5. Lưu file
                Console.WriteLine($"[SaveSnapshotAsync] Writing {imageBytes.Length} bytes to file...");
                await File.WriteAllBytesAsync(filePath, imageBytes);
                Console.WriteLine($"[SaveSnapshotAsync] File written successfully");

                Console.WriteLine($"[AccessLogProcessingService] Snapshot saved: {filePath}");

                // 6. Trả về URL cho frontend / DB
                return $"/snapshots/{fileName}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AccessLogProcessingService] ERROR saving snapshot: {ex.Message}");
                return null;
            }
        }
    }
}