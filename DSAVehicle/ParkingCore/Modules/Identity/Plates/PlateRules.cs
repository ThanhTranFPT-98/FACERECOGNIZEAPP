using System.Text.RegularExpressions;

namespace DSAVehicle.ParkingCore;

// Site plate formats (GC-43). Patterns are anchored and time-limited, so an admin's regex can't hang a decision.
public sealed class PlateRules
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(50);
    private readonly Regex[] patterns;
    public static readonly PlateRules Default = new("");
    public PlateRules(string patterns) => this.patterns = Lines(patterns).Select(p => new Regex("^(?:" + p + ")$", RegexOptions.CultureInvariant, Limit)).ToArray();
    public bool Custom => patterns.Length > 0;
    public bool Valid(string normalizedPlate)
    {
        if (!Custom) return VietnamPlate.CommonFormat(normalizedPlate);
        try { return patterns.Any(p => p.IsMatch(normalizedPlate)); }
        catch (RegexMatchTimeoutException) { return false; }
    }
    // Returns an error message, or null when every line is a usable pattern.
    public static string? Check(string patterns)
    {
        foreach (var line in Lines(patterns))
        {
            try { _ = new Regex("^(?:" + line + ")$", RegexOptions.CultureInvariant, Limit); }
            catch (ArgumentException ex) { return "Invalid pattern '" + line + "': " + ex.Message; }
        }
        return null;
    }
    private static IEnumerable<string> Lines(string patterns) => patterns.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0);
}
