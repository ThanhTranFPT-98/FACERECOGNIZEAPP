using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Data;
using Microsoft.AspNetCore.Mvc;
using MyfirstASP_NET.Helpers;
using MyfirstASP_NET.Dtos.Stranger;
using MyfirstASP_NET.Mapper;
using MyfirstASP_NET.Interfaces;
using Microsoft.EntityFrameworkCore;
using MyfirstASP_NET.Dtos.Embedding;


namespace MyfirstASP_NET.Controllers
{
    [Route("api/Stranger")]
    [ApiController]
    public class StrangerController : ControllerBase
    {
        private readonly ApplicationDBContext _context;
        private readonly IStrangerRepository _StrangerRepo;
        private readonly IFaceEmbeddingAiService _faceEmbeddingService;
        private readonly IEmbeddingRepository _embeddingRepo;

        public StrangerController(ApplicationDBContext context, IStrangerRepository StrangerRepo,
            IFaceEmbeddingAiService faceEmbeddingService, IEmbeddingRepository embeddingRepo)
        {
            _StrangerRepo = StrangerRepo;
            _context = context;
            _faceEmbeddingService = faceEmbeddingService;
            _embeddingRepo = embeddingRepo;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] QueryObjectStranger query)
        {
            var Strangers = await _StrangerRepo.GetAllAsync(query);
            var StrangerDto = Strangers.Select(s => s.ToStrangerDto());

            return Ok(StrangerDto);
        }

        [HttpGet("{Id:int}")]
        public async Task<IActionResult> GetById([FromRoute] int Id)
        {
            var Stranger = await _StrangerRepo.GetByIdAsync(Id);
            if (Stranger == null)
            {
                return NotFound($"Không tìm thấy Stranger có Id = {Id}");
            }
            return Ok(Stranger.ToStrangerDto());
        
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateStrangerRequestDto StrangerDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            var StrangerModel = StrangerDto.ToStrangerFromCreateDTO();
            await _StrangerRepo.CreateAsync(StrangerModel);
            return CreatedAtAction(nameof(GetById), new { id = StrangerModel.Id }, StrangerModel.ToStrangerDto());
        }

        [HttpPut]
        [Route("{id:int}")]
        public async Task<IActionResult> Update([FromRoute] int id, [FromBody] UpdateStrangerRequestDto updateDto)

        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var StrangerModel = await _StrangerRepo.UpdateAsync(id, updateDto.ToStrangerFromUpdate());
            if (StrangerModel == null)
            {
                return NotFound();
            }

            return Ok(StrangerModel.ToStrangerDto());

        }

        [HttpDelete]
        [Route("{id:int}")]

        public async Task<IActionResult> Delete([FromRoute] int id)
        {
            var StrangerModel = await _StrangerRepo.DeleteAsync(id);

            if (StrangerModel == null)
            {
                return NotFound();
            }

            return NoContent();
        }

        /// <summary>
        /// Upload ảnh và tạo embedding cho Stranger
        /// </summary>
        [HttpPost("{id:int}/upload-image")]
        public async Task<IActionResult> UploadImage([FromRoute] int id, IFormFile imageFile)
        {
            if (imageFile == null || imageFile.Length == 0)
            {
                return BadRequest("Please select an image file");
            }

            // Check if stranger exists
            var stranger = await _StrangerRepo.GetByIdAsync(id);
            if (stranger == null)
            {
                return NotFound($"Stranger with Id = {id} not found");
            }

            // Generate embedding from image
            var embeddingResult = await _faceEmbeddingService.GenerateEmbeddingAsync(imageFile);
            
            if (embeddingResult == null || !embeddingResult.IsSuccess)
            {
                return BadRequest(new { 
                    Success = false, 
                    Message = embeddingResult?.Message ?? "Failed to generate embedding" 
                });
            }

            if (!embeddingResult.FaceDetected)
            {
                return BadRequest(new { 
                    Success = false, 
                    Message = "No face detected in the image" 
                });
            }

            if (embeddingResult?.Embedding == null)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = "Generate embedding failed"
                });
            }

            var embedding = new Embedding
            {
                PeopleId = id,
                Vector = System.Text.Json.JsonSerializer.Serialize(embeddingResult.Embedding),
            };


            await _embeddingRepo.CreateAsync(embedding);

            return Ok(new {
                Success = true,
                Message = "Image uploaded and embedding created successfully",
                EmbeddingId = embedding.Id,
                FaceDetected = true
            });
        }
    }
}