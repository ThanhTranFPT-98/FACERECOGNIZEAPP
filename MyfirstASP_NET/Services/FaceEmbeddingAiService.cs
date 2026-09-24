using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using System.Net.Http.Headers;
using System.Text.Json;
using MyfirstASP_NET.Dtos.Embedding;
using MyfirstASP_NET.Interfaces;

namespace MyfirstASP_NET.Services
{
    public class FaceEmbeddingAiService : IFaceEmbeddingAiService
    {
        private readonly HttpClient _httpClient;
        public FaceEmbeddingAiService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<PythonEmbeddingResponseDto?> GenerateEmbeddingAsync(IFormFile imageFile)
        {
            try
            {
                using var content = new MultipartFormDataContent();
                using var stream = imageFile.OpenReadStream();
                using var fileContent = new StreamContent(stream);
                // Bug fix: ContentType can be null if the client omits it; fall back to octet-stream
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                    imageFile.ContentType ?? "application/octet-stream");

                content.Add(fileContent, "file", imageFile.FileName ?? "image");

                var response = await _httpClient.PostAsync("face/embedding", content);
                var json = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Face embedding service returned {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    // Bug fix: json was already read above; reuse it instead of reading the stream again
                    return new PythonEmbeddingResponseDto
                    {
                        IsSuccess = false,
                        FaceDetected = false,
                        Embedding = null,
                        Bbox = null,
                        Message = $"Python API error: {response.StatusCode} - {json}"
                    };
                }

                var result = JsonSerializer.Deserialize<PythonEmbeddingResponseDto>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (result == null)
                {
                    return new PythonEmbeddingResponseDto
                    {
                        IsSuccess = false,
                        FaceDetected = false,
                        Message = "Failed to parse data from AI"
                    };
                }

                result.IsSuccess = true;
                result.Message = result.FaceDetected ? "Success" : "No face detected in image";

                return result;
            }
            catch (HttpRequestException ex)
            {
                return new PythonEmbeddingResponseDto
                {
                    IsSuccess = false,
                    FaceDetected = false,
                    Message = $"Cannot reach AI service: {ex.Message}"
                };
            }
        }
    }
}
