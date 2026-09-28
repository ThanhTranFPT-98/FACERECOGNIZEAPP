using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DSAVehicle.Dtos.FaceRecognition
{
    public class RecognizeFrameResultsDto
    {
        public DateTime CaptureTime { get; set; }
        public string CameraId { get; set; } = string.Empty;
        public int TotalFaces { get; set; }
        public List<RecognizedFaceDto> RecognizedFaces { get; set; } = new List<RecognizedFaceDto>();
        public List<UnknowFaceDto> UnknowFaces { get; set; } = new List<UnknowFaceDto>();

    }

}