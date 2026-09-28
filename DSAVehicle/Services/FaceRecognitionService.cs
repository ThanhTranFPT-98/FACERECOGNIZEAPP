using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Interfaces;
using System.Net.Http;
using System.Text.Json;
using System.Net.Http.Headers;
using DSAVehicle.Dtos.FaceRecognition;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;



namespace DSAVehicle.Services
{
    public class FaceRecognitionService :IFaceRecognitionService
    {
        private readonly HttpClient _httpClientFactory;

        public FaceRecognitionService(HttpClient httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<(List<(int PeopleId, double Confidence)> RecognizedFaces, List<double> StrangerFaces)> RecognizeFacesAsync(RecognizeFrameRequestDto request)
        {
            try
            {
                IFormFile imageFile = request.ImageFile;
                Console.WriteLine($"[FaceRecognitionService] Starting recognition, file: {imageFile.FileName}");

                var content = new MultipartFormDataContent();
                var fileContent = new StreamContent(imageFile.OpenReadStream());
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(imageFile.ContentType);
                content.Add(fileContent, "Image", imageFile.FileName);
                
                Console.WriteLine("[FaceRecognitionService] Sending request to Python API...");
                var response = await _httpClientFactory.PostAsync("face/recognize", content);
                var json = await response.Content.ReadAsStringAsync();
                
                Console.WriteLine($"[FaceRecognitionService] Response status: {response.StatusCode}");
                Console.WriteLine($"[FaceRecognitionService] Response: {json.Substring(0, Math.Min(200, json.Length))}...");
                
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Python API error: {response.StatusCode} - {json}");
                }

                RecognizeFrameResultsDto? result;
                try
                {
                    result = JsonSerializer.Deserialize<RecognizeFrameResultsDto>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FaceRecognitionService] JSON Deserialization error: {ex.Message}");
                    Console.WriteLine($"[FaceRecognitionService] JSON content: {json}");
                    throw;
                }
                
                Console.WriteLine($"[FaceRecognitionService] Deserialized: Recognized={result?.RecognizedFaces?.Count ?? 0}, Unknown={result?.UnknowFaces?.Count ?? 0}");
                
                var recognizedFaces = result?.RecognizedFaces
                    ?.Where(x => x.PeopleId != 0)
                    .Select(x => ((int)x.PeopleId!, x.Confidence))
                    .ToList()
                    ?? new List<(int, double)>();

                var strangerFaces = result?.UnknowFaces
                    ?.Select(x => x.Confidence)
                    .ToList() ?? new List<double>();
                    
                Console.WriteLine($"[FaceRecognitionService] Returning: {recognizedFaces.Count} recognized, {strangerFaces.Count} strangers");
                return (recognizedFaces, strangerFaces);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FaceRecognitionService] ERROR: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[FaceRecognitionService] StackTrace: {ex.StackTrace}");
                throw;
            }
        }
    
    }
}