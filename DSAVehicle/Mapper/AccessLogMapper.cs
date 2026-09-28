using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Dtos.AccessLog;
using DSAVehicle.Dtos.FaceRecognition;

namespace DSAVehicle.Mapper
{
    public static class AccessLogMapper
    {
        public static AccessLogDto ToAccessLogDto(this AccessLog accessLogModel)
        {
            // Determine person type based on People actual type (Employee or Stranger)
            string? personType = null;
            string? personName = null;
            
            if (accessLogModel.People != null)
            {
                personName = accessLogModel.People.Name;
                // Check actual type of People (Employee inherits from People, Stranger inherits from People)
                if (accessLogModel.People is Employee)
                    personType = "Employee";
                else if (accessLogModel.People is Stranger)
                    personType = "Stranger";
                else
                    personType = "Unknown";
            }
            
            // Determine direction from AccessType (CheckIn = In, CheckOut = Out)


            string direction = accessLogModel.AccessType.ToLower() switch
            {
                var s when s.Contains("in") => "In",
                var s when s.Contains("out") => "Out",
                _ => "Unknown"
            };
            
            return new AccessLogDto
            {
                Id = accessLogModel.Id,
                PeopleId = accessLogModel.PeopleId,
                LogTime = accessLogModel.LogTime,
                AccessType = accessLogModel.AccessType,
                ImageSnapshot = accessLogModel.ImageSnapshot,
                PersonName = personName,
                PersonType = personType,
                Direction = direction
            };
        }

        public static AccessLog ToAccessLogFromCreateDto(this CreateAccessLogRequestDto accessLogDto, int peopleId)
        {
            return new AccessLog
            {
                PeopleId = peopleId,
                LogTime = accessLogDto.LogTime,
                AccessType = accessLogDto.AccessType,
                ImageSnapshot = accessLogDto.ImageSnapshot
            };
        }

        public static AccessLog ToAccessLogFromRecognizedFace (this RecognizedFaceDto recognizedFace, DateTime captureTime, string accessType, string imageUrl)
        {
            return new AccessLog
            {
                PeopleId = recognizedFace.PeopleId,
                LogTime = captureTime,
                AccessType = accessType,
                ImageSnapshot = imageUrl
            };
        }
    }
}