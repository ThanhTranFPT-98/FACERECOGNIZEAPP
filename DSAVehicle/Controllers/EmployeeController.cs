using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Data;
using Microsoft.AspNetCore.Mvc;
using DSAVehicle.Dtos.Employee;
using DSAVehicle.Mapper;
using DSAVehicle.Interfaces;
using Microsoft.EntityFrameworkCore;
using DSAVehicle.Helpers;
using DSAVehicle.Dtos.Embedding;


namespace DSAVehicle.Controllers
{
    [Route("api/Employee")]
    [ApiController]
    public class EmployeeController : ControllerBase
    {
        private readonly ApplicationDBContext _context;
        private readonly IEmployeeRepository _EmployeeRepo;
        private readonly IFaceEmbeddingAiService _faceEmbeddingService;
        private readonly IEmbeddingRepository _embeddingRepo;

        public EmployeeController(ApplicationDBContext context, IEmployeeRepository EmployeeRepo, 
            IFaceEmbeddingAiService faceEmbeddingService, IEmbeddingRepository embeddingRepo)
        {
            _EmployeeRepo = EmployeeRepo;
            _context = context;
            _faceEmbeddingService = faceEmbeddingService;
            _embeddingRepo = embeddingRepo;
        }

        [HttpGet]

        public async Task<IActionResult> GetAll([FromQuery] QueryObjectEmployee query)
        {
            var Employees = await _EmployeeRepo.GetAllAsync(query);
            var EmployeeDto = Employees.Select(s => s.ToEmployeeDto());

            return Ok(Employees);
        }

        [HttpGet("{id:int}")]

        public async Task<IActionResult> GetById([FromRoute] int id)
        {
            var Employee = await _EmployeeRepo.GetByIdAsync(id);

            if (Employee == null)
            {
                return NotFound();
            }
            return Ok(Employee.ToEmployeeDto());
        }

        [HttpPost]

        public async Task<IActionResult> Create([FromBody] CreateEmployeeRequestDto EmployeeDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            var EmployeeModel = EmployeeDto.ToEmployeeFromCreateDTO();
            await _EmployeeRepo.CreateAsync(EmployeeModel);
            return CreatedAtAction(nameof(GetById), new { id = EmployeeModel.Id }, EmployeeModel.ToEmployeeDto());
        }

        [HttpPut]
        [Route("{id:int}")]

        public async Task<IActionResult> Update([FromRoute] int id, [FromBody] UpdateEmployeeRequestDto updateDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var EmployeeModel = await _EmployeeRepo.UpdateAsync(id, updateDto.ToEmployeeFromUpdate());
            if (EmployeeModel == null)
            {
                return NotFound();
            }

            return Ok(EmployeeModel.ToEmployeeDto());

        }

        [HttpDelete]
        [Route("{id:int}")]

        public async Task<IActionResult> Delete([FromRoute] int id)
        {
            var EmployeeModel = await _EmployeeRepo.DeleteAsync(id);

            if (EmployeeModel == null)
            {
                return NotFound();
            }

            return NoContent();
        }

        /// <summary>
        /// Upload ảnh và tạo embedding cho Employee
        /// </summary>
        [HttpPost("{id:int}/upload-image")]
        public async Task<IActionResult> UploadImage([FromRoute] int id, IFormFile imageFile)
        {
            if (imageFile == null || imageFile.Length == 0)
            {
                return BadRequest("Please select an image file");
            }

            // Check if employee exists
            var employee = await _EmployeeRepo.GetByIdAsync(id);
            if (employee == null)
            {
                return NotFound($"Employee with Id = {id} not found");
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

            // Save embedding to database

            if(embeddingResult.Embedding == null)
            {
                return BadRequest(new { 
                    Success = false, 
                    Message = "Embedding result is null" 
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
