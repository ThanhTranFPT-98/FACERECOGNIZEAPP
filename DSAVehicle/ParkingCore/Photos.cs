using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

// Guard photos (GC-17) and entry/exit comparison (GC-12). Photos are evidence with a SHA-256 taken at capture (GC-31).
[ApiController, Route("api/v1/parking"), Authorize(Policy = Perm.ViewEvidence)]
public class PhotoController(ParkingDb db, ParkingService service, AreaScope scope, IWebHostEnvironment environment, IConfiguration config,
    IDataProtectionProvider protection, ParkingLock mutex) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";

    // Upload from the guard's device (browser camera, phone, or file).
    [HttpPost("events/{id:guid}/photos"), Authorize(Policy = Perm.OperateLane), RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> Upload(Guid id, IFormFile file, [FromForm, StringLength(500)] string? note)
    {
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        return await Store(id, stream.ToArray(), note);
    }

    // Grab one frame from the lane's camera stream (needs ffmpeg and an RTSP source).
    [HttpPost("events/{id:guid}/photos/camera"), Authorize(Policy = Perm.OperateLane)]
    public async Task<IActionResult> FromCamera(Guid id, [FromQuery] string? cameraId, [FromQuery, StringLength(500)] string? note)
    {
        var entry = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (entry is null) return NotFound();
        var camera = await db.Cameras.AsNoTracking().Where(x => x.LaneId == entry.LaneId && x.SourceProtected != "" && (cameraId == null || x.Id == cameraId)).FirstOrDefaultAsync();
        if (camera is null) return Conflict(new { message = "No camera with a stream is linked to this lane. Upload a photo instead." });
        var source = protection.CreateProtector(CameraProtection.Purpose).Unprotect(camera.SourceProtected);
        var info = new ProcessStartInfo(VideoOptions.From(config).FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-rtsp_transport", "tcp", "-i", source, "-frames:v", "1", "-f", "image2", "-vcodec", "mjpeg", "pipe:1" }) info.ArgumentList.Add(arg);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var ffmpeg = Process.Start(info)!;
            await using var frame = new MemoryStream();
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(frame, timeout.Token);
            await ffmpeg.WaitForExitAsync(timeout.Token);
            if (frame.Length == 0) return StatusCode(503, new { message = "The camera did not return a frame." });
            return await Store(id, frame.ToArray(), note ?? "from camera " + camera.Id);
        }
        catch (System.ComponentModel.Win32Exception) { return StatusCode(503, new { message = "ffmpeg is not installed on this station." }); }
        catch (OperationCanceledException) { return StatusCode(504, new { message = "The camera did not answer within 10 seconds." }); }
    }

    [HttpGet("events/{id:guid}/photos")]
    public async Task<IActionResult> List(Guid id)
    {
        var entry = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (entry is null) return NotFound();
        if (!await scope.Allows(entry.LaneId)) return StatusCode(403, new { message = new AreaForbiddenException().Message });
        return Ok(await db.EvidenceImages.AsNoTracking().Where(x => x.EventId == id).OrderBy(x => x.CreatedUtc).ToListAsync());
    }

    [HttpGet("photos/{photoId:guid}")]
    public async Task<IActionResult> Get(Guid photoId)
    {
        var photo = await db.EvidenceImages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == photoId);
        if (photo is null) return NotFound();
        var lane = await db.Events.Where(x => x.Id == photo.EventId).Select(x => x.LaneId).SingleOrDefaultAsync();
        if (!await scope.Allows(lane)) return StatusCode(403, new { message = new AreaForbiddenException().Message });
        var path = Path.Combine(EvidenceStorage.Directory(environment, config), Path.GetFileName(photo.FileId));
        if (!System.IO.File.Exists(path)) return NotFound();
        db.Log(Actor, "ViewPhoto", photoId); await db.SaveChangesAsync();
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, path.EndsWith(".png") ? "image/png" : "image/jpeg");
    }

    // GC-12: for an exit event, the entry event of the same plate (latest session that started before the exit), to compare side by side.
    [HttpGet("events/{id:guid}/entry")]
    public async Task<IActionResult> Entry(Guid id)
    {
        var exit = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (exit is null) return NotFound();
        if (!await scope.Allows(exit.LaneId)) return StatusCode(403, new { message = new AreaForbiddenException().Message });
        var session = await db.Sessions.AsNoTracking().Where(x => x.Plate == exit.Plate && exit.Plate != "" && x.EntryUtc <= exit.CreatedUtc)
            .OrderByDescending(x => x.EntryUtc).FirstOrDefaultAsync();
        if (session is null) return Ok(new { found = false });
        var entryEvent = await db.Commands.AsNoTracking().Where(c => c.Id == session.EntryCommandId).Join(db.Events.AsNoTracking(), c => c.EventId, e => e.Id, (c, e) => e).SingleOrDefaultAsync();
        return Ok(new { found = entryEvent is not null, session, entry = entryEvent,
            entryAllowed = entryEvent is not null && await scope.Allows(entryEvent.LaneId),
            photos = entryEvent is null ? [] : await db.EvidenceImages.AsNoTracking().Where(x => x.EventId == entryEvent.Id).ToListAsync() });
    }

    private async Task<IActionResult> Store(Guid id, byte[] data, string? note)
    {
        await mutex.Gate.WaitAsync();
        try
        {
            var entry = await db.Events.SingleOrDefaultAsync(x => x.Id == id);
            if (entry is null) return NotFound();
            if (!await scope.Allows(entry.LaneId)) return StatusCode(403, new { message = new AreaForbiddenException().Message });
            var fileId = service.SaveEvidenceBytes(data);
            if (fileId is null) return Conflict(new { message = "Upload a JPEG or PNG photo up to 5 MB." });
            var photo = new EvidenceImage { EventId = id, FileId = fileId, Kind = "Inspection", Sha256 = EvidenceImage.Hash(data), TakenBy = Actor, Note = note?.Trim() };
            db.EvidenceImages.Add(photo); db.Log(Actor, "InspectionPhoto", id);
            await db.SaveChangesAsync();
            return Ok(photo);
        }
        finally { mutex.Gate.Release(); }
    }
}
