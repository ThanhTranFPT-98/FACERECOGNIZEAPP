using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MyfirstASP_NET.ParkingCore;

public class CameraInput
{
    [Required, RegularExpression("^[a-zA-Z0-9_-]{1,40}$")] public string Id { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(40)] public string? LaneId { get; set; }
    // Empty on edit keeps the stored source, so operators never need to see the camera password again.
    [StringLength(500)] public string? Source { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Record24x7 { get; set; } = true;
    [StringLength(40)] public string? ExternalDevice { get; set; }
    [StringLength(20)] public string? ExternalChannel { get; set; }
    [Range(0.01, 1)] public double? TrustedConfidence { get; set; }
    [StringLength(500)] public string? WallSource { get; set; }
    [Range(30, 86400)] public int? SilentAlertSeconds { get; set; }
}

[ApiController, Route("api/v1/parking"), Authorize(Policy = Perm.ViewEvidence)]
public class VideoController(ParkingDb db, IDataProtectionProvider protection, IConfiguration config, IWebHostEnvironment environment, IServiceProvider services, AreaScope scope) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";
    private string VideoRoot => VideoOptions.From(config).ResolveDirectory(environment);

    [HttpGet("cameras")]
    public async Task<object> Cameras()
    {
        var protector = protection.CreateProtector(CameraProtection.Purpose);
        var pending = await db.VideoSegments.Where(x => x.Status == "Recorded" || x.Status == "Failed").GroupBy(x => x.CameraId)
            .Select(g => new { CameraId = g.Key, Count = g.Count(), Oldest = g.Min(x => x.StartUtc) }).ToListAsync();
        var allowed = await scope.Lanes();
        // A camera without a lane belongs to no area, so only accounts without area limits see it.
        var cameras = (await db.Cameras.AsNoTracking().OrderBy(x => x.Id).ToListAsync()).Where(c => allowed is null || (c.LaneId is not null && allowed.Contains(c.LaneId))).ToList();
        return new {
            videoEnabled = VideoOptions.From(config).Enabled, cloudEnabled = CloudOptions.From(config).Enabled,
            outboxPending = await db.Outbox.CountAsync(x => x.Status != "Sent"),
            cameras = cameras.Select(c => new { c.Id, c.Name, c.LaneId, c.Enabled, c.Record24x7, c.LastSegmentUtc, source = c.SourceProtected.Length == 0 ? "" : Mask(protector, c.SourceProtected),
                c.ExternalDevice, c.ExternalChannel, c.TrustedConfidence, c.LastMessageUtc, c.SilentAlertSeconds, wallSource = c.WallSourceProtected is null ? "" : Mask(protector, c.WallSourceProtected),
                pendingUploads = pending.FirstOrDefault(p => p.CameraId == c.Id)?.Count ?? 0, oldestPendingUtc = pending.FirstOrDefault(p => p.CameraId == c.Id)?.Oldest })
        };
    }

    [HttpPut("cameras"), Authorize(Policy = Perm.ManageSite)]
    public async Task<IActionResult> SaveCamera(CameraInput input)
    {
        var camera = await db.Cameras.FindAsync(input.Id);
        if (camera is null && string.IsNullOrWhiteSpace(input.Source) && string.IsNullOrWhiteSpace(input.ExternalDevice)) return Conflict(new { message = "A new camera needs a stream source (rtsp://…) or an AI device ID." });
        if (input.LaneId is not null && !await db.Lanes.AnyAsync(x => x.Id == input.LaneId)) return Conflict(new { message = "Unknown lane." });
        if (!string.IsNullOrWhiteSpace(input.Source))
        {
            var source = input.Source.Trim();
            var allowed = source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase)
                || (environment.IsDevelopment() && source.StartsWith("file:", StringComparison.OrdinalIgnoreCase));
            if (!allowed) return Conflict(new { message = "Use an rtsp:// or rtsps:// stream. Simulated file: sources are only allowed in Development." });
            if (camera is null) { camera = new Camera { Id = input.Id }; db.Cameras.Add(camera); }
            camera.SourceProtected = protection.CreateProtector(CameraProtection.Purpose).Protect(source);
        }
        if (camera is null) { camera = new Camera { Id = input.Id, Record24x7 = false }; db.Cameras.Add(camera); }
        camera.ExternalDevice = string.IsNullOrWhiteSpace(input.ExternalDevice) ? null : T1550Adapter.Normalize(input.ExternalDevice);
        camera.ExternalChannel = string.IsNullOrWhiteSpace(input.ExternalChannel) ? null : input.ExternalChannel.Trim();
        camera.TrustedConfidence = input.TrustedConfidence; camera.SilentAlertSeconds = input.SilentAlertSeconds;
        if (!string.IsNullOrWhiteSpace(input.WallSource))
        {
            if (!input.WallSource.Trim().StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) return Conflict(new { message = "The wall stream must be rtsp:// or rtsps://." });
            camera.WallSourceProtected = protection.CreateProtector(CameraProtection.Purpose).Protect(input.WallSource.Trim());
        }
        camera.Name = input.Name.Trim(); camera.LaneId = string.IsNullOrWhiteSpace(input.LaneId) ? null : input.LaneId;
        camera.Enabled = input.Enabled; camera.Record24x7 = input.Record24x7;
        db.Log(Actor, "SaveCamera", camera.Id);
        await db.SaveChangesAsync();
        return Ok(new { camera.Id });
    }

    // Segments from the event's lane cameras covering ten seconds either side of the decision.
    [HttpGet("events/{id:guid}/video")]
    public async Task<IActionResult> EventVideo(Guid id)
    {
        var entry = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (entry is null) return NotFound();
        if (!await scope.Allows(entry.LaneId)) return StatusCode(403, new { message = new AreaForbiddenException().Message });
        var from = entry.CreatedUtc.AddSeconds(-10); var until = entry.CreatedUtc.AddSeconds(10);
        var cameras = await db.Cameras.AsNoTracking().Where(x => x.LaneId == entry.LaneId).Select(x => x.Id).ToListAsync();
        var segments = await db.VideoSegments.AsNoTracking()
            .Where(x => cameras.Contains(x.CameraId) && x.StartUtc <= until && x.EndUtc >= from && x.Status != "Missing" && x.Status != "Discarded")
            .OrderBy(x => x.CameraId).ThenBy(x => x.StartUtc).ToListAsync();
        return Ok(segments.Select(x => new { x.Id, x.CameraId, x.StartUtc, x.EndUtc, x.Status,
            offsetSeconds = Math.Max(0, (entry.CreatedUtc - x.StartUtc).TotalSeconds - 5), local = x.Status != "LocalDeleted" }));
    }

    [HttpGet("video/segments/{id:guid}/play")]
    public async Task<IActionResult> Play(Guid id)
    {
        var segment = await db.VideoSegments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (segment is null) return NotFound();
        var cameraLane = await db.Cameras.Where(x => x.Id == segment.CameraId).Select(x => x.LaneId).SingleOrDefaultAsync();
        if (!await scope.Allows(cameraLane)) return StatusCode(403, new { message = new AreaForbiddenException().Message });
        db.Log(Actor, "ViewVideo", id); await db.SaveChangesAsync();
        Response.Headers.CacheControl = "no-store";
        var path = Path.Combine(VideoRoot, segment.CameraId, segment.FileName);
        if (System.IO.File.Exists(path))
        {
            // Browsers cannot play MPEG-TS; remux (no re-encode) to fragmented MP4 while streaming.
            var info = new ProcessStartInfo(VideoOptions.From(config).FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-c", "copy", "-f", "mp4", "-movflags", "frag_keyframe+empty_moov", "pipe:1" }) info.ArgumentList.Add(arg);
            Process? ffmpeg = null;
            try { ffmpeg = Process.Start(info); }
            catch (System.ComponentModel.Win32Exception) { }
            if (ffmpeg is null) return PhysicalFile(path, "video/mp2t", segment.FileName);
            using (ffmpeg)
            {
                Response.ContentType = "video/mp4";
                try { await ffmpeg.StandardOutput.BaseStream.CopyToAsync(Response.Body, HttpContext.RequestAborted); }
                catch (OperationCanceledException) { }
                finally { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); }
            }
            return new EmptyResult();
        }
        var url = segment.ObjectKey is null ? null : services.GetService<IObjectStorage>()?.ReadUrl(segment.ObjectKey, TimeSpan.FromMinutes(10));
        return url is null ? NotFound(new { message = "Video is no longer stored locally and no cloud copy is reachable." }) : Redirect(url);
    }

    private static string Mask(IDataProtector protector, string protectedSource)
    {
        try
        {
            var source = protector.Unprotect(protectedSource);
            return Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("rtsp") ? $"{uri.Scheme}://{uri.Host}:{uri.Port}/…" : "simulated file";
        }
        catch (System.Security.Cryptography.CryptographicException) { return "unreadable — re-enter source"; }
    }
}
