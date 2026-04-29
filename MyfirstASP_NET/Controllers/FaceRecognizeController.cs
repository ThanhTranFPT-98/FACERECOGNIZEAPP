using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Data;
using Microsoft.AspNetCore.Mvc;
using MyfirstASP_NET.Helpers;
using MyfirstASP_NET.Dtos.FaceRecognition;
using MyfirstASP_NET.Mapper;
using MyfirstASP_NET.Interfaces;
using Microsoft.EntityFrameworkCore;
using MyfirstASP_NET.Services;



namespace MyfirstASP_NET.Controllers
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