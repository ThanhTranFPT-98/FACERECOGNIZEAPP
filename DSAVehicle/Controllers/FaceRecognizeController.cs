using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Data;
using Microsoft.AspNetCore.Mvc;
using DSAVehicle.Helpers;
using DSAVehicle.Dtos.FaceRecognition;
using DSAVehicle.Mapper;
using DSAVehicle.Interfaces;
using Microsoft.EntityFrameworkCore;
using DSAVehicle.Services;



namespace DSAVehicle.Controllers
{
    [Route("api/FaceRecognize")]
    [ApiController]

    public class FaceRecognitionController : ControllerBase
    {
        private readonly IFaceRecognitionService _faceRecognitionService;

        private readonly IAccessLogProcessingService _accessLogProcessingService;
        public FaceRecognitionController(IFaceRecognitionService faceRecognitionService, IAccessLogProcessingService accessLogProcessingService)
        {
            _faceRecognitionService = faceRecognitionService;
            _accessLogProcessingService = accessLogProcessingService;
        }

        [HttpPost("RecognizeFrame")]
        public async Task<ActionResult<RecognizeFrameResultsDto>> RecognizeFrame([FromForm] RecognizeFrameRequestDto request)
        {
            request.CaptureTime = DateTime.Now;

            var (recognizedFaces, unknowFaces) = await _faceRecognitionService.RecognizeFacesAsync(request);

            var resultsDto = new RecognizeFrameResultsDto
            {
                TotalFaces = recognizedFaces.Count + unknowFaces.Count,
                CaptureTime = request.CaptureTime,
                CameraId = request.CameraId,

                RecognizedFaces = recognizedFaces
                .Where(f => f.PeopleId != 0)
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

            var createdLogs = await _accessLogProcessingService.ProcessAccessLogsAsync(resultsDto, request.ImageFile);
            Console.WriteLine($"Created {createdLogs.Count} access logs from this frame.");
            return Ok(createdLogs);
        }
    }
}