using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DSAVehicle.Dtos.FaceRecognition
{
    public class RecognizedFaceDto
    {   
        public int PeopleId { get; set; }
        public double Confidence { get; set; }
    }
}