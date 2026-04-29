using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Dtos.FaceRecognition;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Dtos.AccessLog;
namespace MyfirstASP_NET.Interfaces
{
    public interface IAccessLogProcessingService
    {
        Task<List<AccessLogDto>> ProcessAccessLogsAsync(RecognizeFrameResultsDto resultsDto, IFormFile imageFile, byte[]? imageBytes = null);
        Task<bool> ShouldSkipDuplicateAsync(int peopleId, DateTime captureTime, string cameraId);
        Task<string> GetNextAccessTypeAsync(int peopleId, DateTime captureTime, string cameraId);
    }
}