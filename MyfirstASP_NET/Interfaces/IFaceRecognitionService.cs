using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MyfirstASP_NET.Dtos.FaceRecognition;

namespace MyfirstASP_NET.Interfaces
{
    public interface IFaceRecognitionService
    {
        Task <(List<(int PeopleId, double Confidence)> RecognizedFaces, List<double> StrangerFaces)> RecognizeFacesAsync(RecognizeFrameRequestDto request);
    }
}