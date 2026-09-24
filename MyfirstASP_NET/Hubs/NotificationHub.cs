using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using MyfirstASP_NET.Dtos.FaceRecognition;
using MyfirstASP_NET.Interfaces;
using MyfirstASP_NET.Services;
using Microsoft.AspNetCore.Authorization;

namespace MyfirstASP_NET.Hubs
{
    [Authorize(Roles = "Admin")]
    public class NotificationHub : Hub
    {
        private readonly IFaceRecognitionService _faceRecognitionService;
        private readonly IAccessLogProcessingService _accessLogProcessingService;

        public NotificationHub(IFaceRecognitionService faceRecognitionService, IAccessLogProcessingService accessLogProcessingService)
        {
            _faceRecognitionService = faceRecognitionService;
            _accessLogProcessingService = accessLogProcessingService;
        }

        /// <summary>
        /// Client gọi method này để gửi frame ảnh base64 để nhận diện
        /// </summary>
        public async Task ProcessFrame(string imageBase64, string cameraId = "default")
        {
            // Send ACK immediately so client knows server received the frame
            await Clients.Caller.SendAsync("FrameReceived", new { CameraId = cameraId, Timestamp = DateTime.Now });
            
            try
            {
                Console.WriteLine($"[NotificationHub] ProcessFrame called for camera: {cameraId}");
                
                // Validate base64
                if (string.IsNullOrEmpty(imageBase64))
                {
                    Console.WriteLine("[NotificationHub] ERROR: Image base64 is empty");
                    await Clients.Caller.SendAsync("RecognitionError", new { Error = "Image base64 is empty" });
                    return;
                }
                
                // Convert base64 to IFormFile
                string base64Data;
                if (imageBase64.Contains(','))
                {
                    base64Data = imageBase64.Split(',')[1]; // Remove data:image/jpeg;base64, prefix
                }
                else
                {
                    base64Data = imageBase64;
                }
                
                Console.WriteLine($"[NotificationHub] Base64 length: {base64Data.Length}");
                
                var bytes = Convert.FromBase64String(base64Data);
                Console.WriteLine($"[NotificationHub] Decoded bytes: {bytes.Length}");
                
                var stream = new MemoryStream(bytes);
                var formFile = new FormFile(stream, 0, bytes.Length, "Image", "frame.jpg")
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "image/jpeg"
                };

                var request = new RecognizeFrameRequestDto
                {
                    ImageFile = formFile,
                    CameraId = cameraId,
                    CaptureTime = DateTime.Now
                };

                // Call recognition service
                Console.WriteLine("[NotificationHub] Calling FaceRecognitionService...");
                List<(int PeopleId, double Confidence)> recognizedFaces;
                List<double> unknowFaces;
                
                try
                {
                    (recognizedFaces, unknowFaces) = await _faceRecognitionService.RecognizeFacesAsync(request);
                    Console.WriteLine($"[NotificationHub] Recognition complete: {recognizedFaces.Count} recognized, {unknowFaces.Count} unknown");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NotificationHub] ERROR in FaceRecognitionService: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"[NotificationHub] StackTrace: {ex.StackTrace}");
                    throw;
                }

                var result = new RecognizeFrameResultsDto
                {
                    TotalFaces = recognizedFaces.Count + unknowFaces.Count,
                    CaptureTime = request.CaptureTime,
                    CameraId = cameraId,
                    RecognizedFaces = recognizedFaces
                        .Select(f => new RecognizedFaceDto
                        {
                            PeopleId = f.PeopleId,
                            Confidence = f.Confidence
                        }).ToList(),
                    UnknowFaces = unknowFaces
                        .Select(f => new UnknowFaceDto
                        {
                            Confidence = f
                        }).ToList()
                };

                // Process access logs - pass bytes array to avoid stream disposal issues
                var createdLogs = await _accessLogProcessingService.ProcessAccessLogsAsync(result, formFile, bytes);

                // Send result back to caller
                await Clients.Caller.SendAsync("RecognitionResult", new
                {
                    Success = true,
                    Data = result,
                    Logs = createdLogs
                });

                // Notify all clients if stranger detected
                if (unknowFaces.Count > 0)
                {
                    await Clients.All.SendAsync("StrangerDetected", new
                    {
                        CameraId = cameraId,
                        Timestamp = DateTime.Now,
                        StrangerCount = unknowFaces.Count
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationHub] ERROR: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[NotificationHub] StackTrace: {ex.StackTrace}");
                
                // Send error to client but DON'T rethrow - that would disconnect SignalR
                try
                {
                    await Clients.Caller.SendAsync("RecognitionError", new
                    {
                        Success = false,
                        Error = $"{ex.GetType().Name}: {ex.Message}"
                    });
                }
                catch (Exception sendEx)
                {
                    Console.WriteLine($"[NotificationHub] Failed to send error to client: {sendEx.Message}");
                }
            }
        }

        /// <summary>
        /// Client gọi khi bắt đầu monitoring
        /// </summary>
        public async Task StartMonitoring(string cameraId)
        {
            try
            {
                Console.WriteLine($"[NotificationHub] StartMonitoring called for camera: {cameraId}");
                await Groups.AddToGroupAsync(Context.ConnectionId, $"Camera_{cameraId}");
                await Clients.Caller.SendAsync("MonitoringStarted", new { CameraId = cameraId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationHub] ERROR in StartMonitoring: {ex.Message}");
            }
        }

        /// <summary>
        /// Client gọi khi dừng monitoring
        /// </summary>
        public async Task StopMonitoring(string cameraId)
        {
            try
            {
                Console.WriteLine($"[NotificationHub] StopMonitoring called for camera: {cameraId}");
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Camera_{cameraId}");
                await Clients.Caller.SendAsync("MonitoringStopped", new { CameraId = cameraId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationHub] ERROR in StopMonitoring: {ex.Message}");
            }
        }

        public override async Task OnConnectedAsync()
        {
            try
            {
                Console.WriteLine($"[NotificationHub] Client connected: {Context.ConnectionId}");
                await Clients.Caller.SendAsync("Connected", new { ConnectionId = Context.ConnectionId });
                await base.OnConnectedAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationHub] ERROR in OnConnectedAsync: {ex.Message}");
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            try
            {
                if (exception != null)
                {
                    Console.WriteLine($"[NotificationHub] Client disconnected with error: {exception.Message}");
                }
                else
                {
                    Console.WriteLine($"[NotificationHub] Client disconnected normally: {Context.ConnectionId}");
                }
                await base.OnDisconnectedAsync(exception);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationHub] ERROR in OnDisconnectedAsync: {ex.Message}");
            }
        }
    }
}
