using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Dtos.Stranger;
namespace MyfirstASP_NET.Mapper
{
    public static class StrangerMappers
    {
        public static StrangerDto ToStrangerDto(this Stranger strangerModel)
        {
            return new StrangerDto
            {
                PeopleId = strangerModel.Id,
                Name = strangerModel.Name,
                BirthDay = strangerModel.BirthDay,
                Address = strangerModel.Address,
                Company = strangerModel.Company,
                AccessLogs = strangerModel.AccessLogs.Select(a => a.ToAccessLogDto()).ToList(),
            };
        }

        public static Stranger ToStrangerFromCreateDTO(this CreateStrangerRequestDto strangerDto)
        {
            return new Stranger
            {
                Name = strangerDto.Name,
                BirthDay = strangerDto.BirthDay,
                Address = strangerDto.Address,
                Company = strangerDto.Company,
            };
        }

        public static Stranger ToStrangerFromUpdate(this UpdateStrangerRequestDto strangerDto)
        {
            return new Stranger
            {
                Name = strangerDto.Name,
                BirthDay = strangerDto.BirthDay,
                Address = strangerDto.Address,
                Company = strangerDto.Company,
            };
        }
    }

}
