using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// CR-030: an AI box has no screen and no settings of its own at the site. The operator draws each lane's
// region on the station; the box asks for them here and keeps asking, so a region redrawn on the station
// takes effect without anyone logging into the box. Authenticated with the receiver key, like the messages
// that come back the other way.

public static class CameraRoi
{
    public const int MinPoints = 3;
    public const int MaxPoints = 20;

    /// <summary>Stored polygon → points, or null when nothing usable is stored (which means "the whole image").</summary>
    public static double[][]? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            var points = JsonSerializer.Deserialize<double[][]>(stored);
            return points is { Length: >= MinPoints } && points.All(Valid) ? points : null;
        }
        catch (JsonException) { return null; }
    }

    public static bool Valid(double[] point) => point.Length == 2 && point.All(v => v is >= 0 and <= 1);

    public static string Serialize(double[][] points) =>
        JsonSerializer.Serialize(points.Select(p => new[] { Math.Round(p[0], 4), Math.Round(p[1], 4) }).ToArray());
}

[ApiController, Route("api/v1/parking"), AllowAnonymous]
public class AiConfigController(ParkingDb db, IConfiguration config) : ControllerBase
{
    [HttpGet("ai-config")]
    public async Task<IActionResult> Regions([FromQuery] string? device, [FromQuery] string? channel, [FromQuery] string? key)
    {
        if (!ReceiverOptions.From(config).KeyMatches(key)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(device)) return BadRequest(new { message = "device is required." });
        var id = T1550Adapter.Normalize(device);
        var cameras = await db.Cameras.AsNoTracking().Where(x => x.ExternalDevice == id).OrderBy(x => x.Id).ToListAsync();
        // A box that names its channel only gets that channel's rows; a camera row that names none answers for any.
        if (!string.IsNullOrWhiteSpace(channel))
            cameras = cameras.Where(x => string.IsNullOrEmpty(x.ExternalChannel) || x.ExternalChannel == channel.Trim()).ToList();
        return Ok(new
        {
            deviceId = id,
            channel,
            rois = cameras.Select(c => new
            {
                roiId = c.ExternalRoi ?? "",
                label = c.Name,
                laneId = c.LaneId,
                polygon = CameraRoi.Parse(c.RoiPolygon) ?? [],
            })
        });
    }
}
