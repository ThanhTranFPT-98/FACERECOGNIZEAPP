using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace MyfirstASP_NET.Dtos.Embedding
{
    public class PythonEmbeddingResponseDto
    {
        [JsonPropertyName("face_detected")]
        public bool FaceDetected { get; set; }

        [JsonPropertyName("embedding")]
        public List<float>? Embedding { get; set; }

        [JsonPropertyName("bbox")]
        public List<float>? Bbox { get; set; }
        public bool IsSuccess { get; set; }
        public string? Message { get; set; }
    }
}