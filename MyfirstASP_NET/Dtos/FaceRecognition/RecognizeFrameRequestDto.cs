using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyfirstASP_NET.Dtos.FaceRecognition
{
    public class RecognizeFrameRequestDto
    {
        public IFormFile ImageFile { get; set; } = null!;
        public DateTime CaptureTime { get; set; }
        public string CameraId { get; set; } = string.Empty;
    }
}