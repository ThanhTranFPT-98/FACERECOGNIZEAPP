using MyfirstASP_NET.Data;
using Microsoft.AspNetCore.Mvc;
using MyfirstASP_NET.Dtos.Employee;
using MyfirstASP_NET.Dtos.Embedding;
using MyfirstASP_NET.Mapper;
using MyfirstASP_NET.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Dtos.AccessLog;



namespace MyfirstASP_NET.Controllers
{

    [Route("api/AccessLog")]
    [ApiController]
    public class AccessLogController: ControllerBase
    {
        private readonly IAccessLogRepository _accessLogRepo;
        public AccessLogController(IAccessLogRepository accessLogRepo)
        {
            _accessLogRepo = accessLogRepo;
        }

        [HttpGet]

        public async Task<IActionResult> GetAll()
        {

            var accessLogs = await _accessLogRepo.GetAllAsync();

            var accessDto = accessLogs.Select(s => s.ToAccessLogDto());

            return Ok(accessDto);

        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById([FromRoute] int id)
        {
            var accessLog = await _accessLogRepo.GetByIdAsync(id);

            if (accessLog ==null)
            {
                return NotFound();
            }

            return Ok(accessLog.ToAccessLogDto());
        
        }

        [HttpPost]
        [Route("{peopleID:int}")]

        public async Task<IActionResult> Create([FromBody] CreateAccessLogRequestDto CreateAccessDto, [FromRoute] int peopleID)
        {
        
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            if (!await _accessLogRepo.AccessLogPeopleExistsAsync(peopleID))
       
            {
                return BadRequest(" people does not exist");
            }
            var accessLogModel = CreateAccessDto.ToAccessLogFromCreateDto(peopleID);
            await _accessLogRepo.CreateAsync(accessLogModel);
            
            return CreatedAtAction(nameof(GetById), new { id = accessLogModel.Id }, accessLogModel.ToAccessLogDto());
        }

        [HttpDelete]

        public async Task<IActionResult> Delete([FromRoute] int id)
        {
            var accessLogModel = await _accessLogRepo.DeleteAsync(id);

            if (accessLogModel == null)
            {
                return NotFound();
            }

            return NoContent();
        }


    }
}