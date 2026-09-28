using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Dtos.FaceRecognition;
using DSAVehicle.Models;
using DSAVehicle.Dtos.AccessLog;
namespace DSAVehicle.Interfaces
{
    public interface IAccessLogProcessingService
    {
        Task<List<AccessLogDto>> ProcessAccessLogsAsync(RecognizeFrameResultsDto resultsDto, IFormFile imageFile, byte[]? imageBytes = null);
        Task<bool> ShouldSkipDuplicateAsync(int peopleId, DateTime captureTime, string cameraId);
        Task<string> GetNextAccessTypeAsync(int peopleId, DateTime captureTime, string cameraId);
    }
}