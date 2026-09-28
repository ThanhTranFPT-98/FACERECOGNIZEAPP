using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.Embedding;
using System.Text.Json;


namespace DSAVehicle.Mapper
{
    public static class EmbeddingMapper
    {
        public static EmbeddingDto ToEmbeddingDto(this Embedding  embeddingModel)
        {
            return new EmbeddingDto
            {
                Id = embeddingModel.Id,
                PeopleId = embeddingModel.PeopleId,
                Vector = embeddingModel.Vector
            };
        }
        public static Embedding ToEmbeddingFromAiResult(this PythonEmbeddingResponseDto aiResult, int peopleId)
        {
            return new Embedding
            {
                PeopleId = peopleId,
                Vector = JsonSerializer.Serialize(aiResult.Embedding)
            };
        }
        public static Embedding ToEmbeddingFromUpdate(this UpdateEmbeddingRequestDto UpdateEmbeddingDto)
        {
            return new Embedding
            {
                Vector = UpdateEmbeddingDto.Vector,
            };
        }
    }
}
