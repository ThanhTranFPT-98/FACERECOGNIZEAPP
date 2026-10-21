namespace DSAVehicle.ParkingCore;

// Internal contract M1 → M3: who/which vehicle was seen on a lane. Module 3 (access) only sees identity through this record.
public record Recognition(string Plate, double PlateConfidence, int? PersonId, double FaceConfidence, string ModelVersion);
