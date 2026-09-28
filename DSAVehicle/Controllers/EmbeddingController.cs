using DSAVehicle.Data;
using Microsoft.AspNetCore.Mvc;
using DSAVehicle.Dtos.Embedding;
using DSAVehicle.Mapper;
using DSAVehicle.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using DSAVehicle.Services;


namespace DSAVehicle.Controllers
{
    [Route("api/embeddings")]
    [ApiController]
    public class EmbeddingController : ControllerBase
    {
        public readonly IEmbeddingRepository _embeddingRepo;
        public readonly IPeopleRepository _peopleRepo;

        public readonly IFaceEmbeddingAiService _faceEmbeddingAiService;
        public EmbeddingController( IEmbeddingRepository embeddingRepo, IPeopleRepository peopleRepo, IFaceEmbeddingAiService faceEmbeddingAiService)
        {
            _embeddingRepo = embeddingRepo;
            _peopleRepo = peopleRepo;
            _faceEmbeddingAiService = faceEmbeddingAiService;
        }

        [HttpGet]

        public async Task<IActionResult> GetAll()
        {

            var embeddings = await _embeddingRepo.GetAllAsync();

            var embeddingDto = embeddings.Select(s => s.ToEmbeddingDto());

            return Ok(embeddingDto);
        }

        [HttpGet("{id:int}")]

        public async Task<IActionResult> GetById([FromRoute] int id)
        {
            var embedding = await _embeddingRepo.GetByIdAsync(id);

            if (embedding == null)
            {
                return NotFound();
            }

            return Ok(embedding.ToEmbeddingDto());
        }

        [HttpPost]
        [Route("{peopleId:int}")]

        public async Task<IActionResult> Create(
            [FromRoute] int peopleId,
            [FromForm] CreateEmbeddingDto embeddingDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (!await _peopleRepo.PeopleExistsAsync(peopleId))
            {
                return BadRequest("people does not exist");
            }

            if (embeddingDto.ImageFile == null || embeddingDto.ImageFile.Length == 0)
            {
                return BadRequest("Ảnh không hợp lệ");
            }

            var aiResult = await _faceEmbeddingAiService.GenerateEmbeddingAsync(embeddingDto.ImageFile);
            

            if (aiResult == null || !aiResult.IsSuccess || aiResult.Embedding == null || !aiResult.Embedding.Any())
            {
                return BadRequest("Không tạo được embedding từ ảnh. Vui lòng thử lại với ảnh khác hoặc liên hệ hỗ trợ.");
            }

            var embeddingModel = aiResult.ToEmbeddingFromAiResult(peopleId);

            await _embeddingRepo.CreateAsync(embeddingModel);

            return CreatedAtAction(
                nameof(GetById),
                new { id = embeddingModel.Id }, 
                embeddingModel.ToEmbeddingDto()
            );
        }

        [HttpPut]
        [Route("{id:int}")]

        public async Task<IActionResult> Update([FromRoute] int id,[FromBody] UpdateEmbeddingRequestDto UpdateEmbeddingDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }


            var embeddingModel = await _embeddingRepo.UpdateAsync(id, UpdateEmbeddingDto.ToEmbeddingFromUpdate());

            if (embeddingModel == null)
            {
                return NotFound();
            }
            return Ok(embeddingModel.ToEmbeddingDto());
        }


        [HttpDelete]
        [Route("{id:int}")]
        public async Task<IActionResult> Delete([FromRoute] int id)
        {
            var embeddingModel = await _embeddingRepo.DeleteAsync(id);
            if (embeddingModel == null)
            {
                return NotFound();
            }
            return NoContent();
        }
        }
}
