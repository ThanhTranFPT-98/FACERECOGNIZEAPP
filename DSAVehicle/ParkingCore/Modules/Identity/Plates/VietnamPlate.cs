using System.Text.RegularExpressions;

namespace DSAVehicle.ParkingCore;

public static class VietnamPlate
{
    // Covers common civilian layouts; special/diplomatic/military formats require review.
    public static string Normalize(string value) => Regex.Replace(value.ToUpperInvariant(), @"[\s.\-]", "");
    public static bool CommonFormat(string value) => Regex.IsMatch(value, @"^[0-9]{2}[A-Z]{1,2}[0-9]{4,6}$");
}
