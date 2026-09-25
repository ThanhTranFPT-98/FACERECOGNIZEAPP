using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MyfirstASP_NET.ParkingCore;

// CR-001: 24/7 recording on the site station, local ring buffer, upload to S3-compatible cloud storage.
// Local-first: nothing here is on the barrier decision path, and nothing waits for the cloud.

public class Camera
{
    [MaxLength(40)] public string Id { get; set; } = "";
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(40)] public string? LaneId { get; set; }
    // Stream URLs usually embed the camera password, so they are stored with Data Protection.
    public string SourceProtected { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Record24x7 { get; set; } = true;
    public DateTime? LastSegmentUtc { get; set; }
    // Third-party AI camera mapping (CR-007): device ID (MAC without separators) + channel → this camera → its lane.
    [MaxLength(40)] public string? ExternalDevice { get; set; }
    [MaxLength(20)] public string? ExternalChannel { get; set; }
    // Confidence given to this device's results after field evaluation; null = results always go to guard review.
    public double? TrustedConfidence { get; set; }
    public DateTime? LastMessageUtc { get; set; }
    // Video wall: a lighter sub-stream, if the camera has one (encrypted like the main source).
    public string? WallSourceProtected { get; set; }
    // GC-27: alert when an AI device sends nothing (not even its periodic alarm/heartbeat message) for this long. Null = off.
    public int? SilentAlertSeconds { get; set; }
}
public class VideoSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public string CameraId { get; set; } = "";
    [MaxLength(64)] public string FileName { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public long SizeBytes { get; set; }
    [MaxLength(64)] public string Sha256 { get; set; } = "";
    // Recorded → Uploaded → LocalDeleted; Failed retries; Missing = local file vanished before upload; Discarded = sacrificed when the disk was full.
    [MaxLength(20)] public string Status { get; set; } = "Recorded";
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? UploadedUtc { get; set; }
    public string? ObjectKey { get; set; }
    public string? LastError { get; set; }
}
public class SyncOutboxItem
{
    public long Id { get; set; }
    [MaxLength(40)] public string EntityType { get; set; } = "";
    [MaxLength(64)] public string EntityId { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? EvidenceId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(20)] public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class VideoOptions
{
    public bool Enabled { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string? Directory { get; set; }
    public int SegmentSeconds { get; set; } = 60;
    public double LocalQuotaGb { get; set; } = 800;
    // Below this free-space fraction the disk is protected even at the cost of un-uploaded video (audited).
    public double MinFreeDiskFraction { get; set; } = .05;
    public string? NtpServer { get; set; }
    public double MaxClockDriftSeconds { get; set; } = 2;
    public static VideoOptions From(IConfiguration config) => config.GetSection("Video").Get<VideoOptions>() ?? new();
    public string ResolveDirectory(IWebHostEnvironment environment) => Directory ?? Path.Combine(environment.ContentRootPath, "App_Data", "video");
}
public sealed class CloudOptions
{
    public bool Enabled { get; set; }
    // "S3" for any S3-compatible provider; "Folder" copies to a directory (tests, or a mounted share).
    public string Provider { get; set; } = "S3";
    public string SiteId { get; set; } = "site";
    public string? Endpoint { get; set; }
    public string? Region { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string? FolderPath { get; set; }
    public double UploadMbpsCap { get; set; }
    public int BacklogAlertMinutes { get; set; } = 60;
    public static CloudOptions From(IConfiguration config) => config.GetSection("Cloud").Get<CloudOptions>() ?? new();
}

public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, IDictionary<string, string> metadata, CancellationToken cancellation);
    Task<long?> SizeAsync(string key, CancellationToken cancellation);
    string? ReadUrl(string key, TimeSpan validFor);
    // Keys under a prefix, in ordinal order, strictly after startAfter.
    Task<IReadOnlyList<string>> ListAsync(string prefix, string? startAfter, int max, CancellationToken cancellation);
    Task<Stream?> GetAsync(string key, CancellationToken cancellation);
}
public sealed class FolderObjectStorage(string root) : IObjectStorage
{
    private string PathFor(string key)
    {
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Object key escapes the storage folder.");
        return path;
    }
    public async Task PutAsync(string key, Stream content, string contentType, IDictionary<string, string> metadata, CancellationToken cancellation)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var file = File.Create(temporary)) await content.CopyToAsync(file, cancellation);
        File.Move(temporary, path, overwrite: true);
    }
    public Task<long?> SizeAsync(string key, CancellationToken cancellation)
    {
        var file = new FileInfo(PathFor(key));
        return Task.FromResult(file.Exists ? file.Length : (long?)null);
    }
    public string? ReadUrl(string key, TimeSpan validFor) => null;
    public Task<IReadOnlyList<string>> ListAsync(string prefix, string? startAfter, int max, CancellationToken cancellation)
    {
        var start = Path.GetFullPath(root);
        if (!Directory.Exists(start)) return Task.FromResult<IReadOnlyList<string>>([]);
        var keys = Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".partial"))
            .Select(f => Path.GetRelativePath(start, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && (startAfter is null || string.CompareOrdinal(k, startAfter) > 0))
            .OrderBy(k => k, StringComparer.Ordinal).Take(max).ToList();
        return Task.FromResult<IReadOnlyList<string>>(keys);
    }
    public Task<Stream?> GetAsync(string key, CancellationToken cancellation)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }
}
public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly AmazonS3Client client;
    private readonly string bucket;
    private readonly bool https;
    public S3ObjectStorage(CloudOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Bucket) || string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            throw new InvalidOperationException("Cloud:Endpoint, Bucket, AccessKey and SecretKey are required for the S3 provider.");
        bucket = options.Bucket;
        https = options.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        // S3-compatible providers generally need path-style addressing and do not implement the newer default checksum headers.
        client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config {
            ServiceURL = options.Endpoint, ForcePathStyle = true, AuthenticationRegion = options.Region ?? "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED });
    }
    public async Task PutAsync(string key, Stream content, string contentType, IDictionary<string, string> metadata, CancellationToken cancellation)
    {
        var request = new PutObjectRequest { BucketName = bucket, Key = key, InputStream = content, ContentType = contentType, AutoCloseStream = false, UseChunkEncoding = false, DisablePayloadSigning = https };
        foreach (var (name, value) in metadata) request.Metadata[name] = value;
        await client.PutObjectAsync(request, cancellation);
    }
    public async Task<long?> SizeAsync(string key, CancellationToken cancellation)
    {
        try { return (await client.GetObjectMetadataAsync(bucket, key, cancellation)).ContentLength; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public async Task<IReadOnlyList<string>> ListAsync(string prefix, string? startAfter, int max, CancellationToken cancellation)
    {
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, StartAfter = startAfter, MaxKeys = max }, cancellation);
        return (response.S3Objects ?? []).Select(x => x.Key).ToList();
    }
    public async Task<Stream?> GetAsync(string key, CancellationToken cancellation)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, key, cancellation);
            var copy = new MemoryStream();
            await response.ResponseStream.CopyToAsync(copy, cancellation);
            copy.Position = 0; return copy;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public string? ReadUrl(string key, TimeSpan validFor) =>
        client.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = bucket, Key = key, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.Add(validFor), Protocol = https ? Protocol.HTTPS : Protocol.HTTP });
}

// Snapshots every change to decisions, commands, sessions and alerts into the outbox inside the same SaveChanges transaction.
public sealed class SyncOutboxInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) { Capture(eventData.Context); return result; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Capture(eventData.Context); return ValueTask.FromResult(result); }
    private static void Capture(DbContext? context)
    {
        if (context is not ParkingDb db) return;
        var items = db.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified).Select(x => x.Entity switch {
            AccessEvent e => new SyncOutboxItem { EntityType = nameof(AccessEvent), EntityId = e.Id.ToString(), EvidenceId = e.EvidenceId, Payload = JsonSerializer.Serialize(e, Json) },
            DeviceCommand c => new SyncOutboxItem { EntityType = nameof(DeviceCommand), EntityId = c.Id.ToString(), Payload = JsonSerializer.Serialize(c, Json) },
            ParkingSession s => new SyncOutboxItem { EntityType = nameof(ParkingSession), EntityId = s.Id.ToString(), Payload = JsonSerializer.Serialize(s, Json) },
            ParkingAlert a => new SyncOutboxItem { EntityType = nameof(ParkingAlert), EntityId = a.Id.ToString(), Payload = JsonSerializer.Serialize(a, Json) },
            PresenceRecord p when x.State == EntityState.Added => new SyncOutboxItem { EntityType = nameof(PresenceRecord), EntityId = p.EventId.ToString(), Payload = JsonSerializer.Serialize(p, Json) },
            _ => null
        }).OfType<SyncOutboxItem>().ToList();
        db.Outbox.AddRange(items);
    }
}

public static class VideoNaming
{
    public const string Pattern = "%Y%m%dT%H%M%S.ts";
    // ffmpeg's -strftime uses the station PC's local clock; the PC clock is the single trusted clock for the site (D3).
    public static bool TryParseStart(string fileName, out DateTime startUtc)
    {
        var ok = DateTime.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local);
        startUtc = ok ? local.ToUniversalTime() : default;
        return ok;
    }
    public static string ObjectKey(string siteId, VideoSegment segment) =>
        $"{siteId}/video/{segment.CameraId}/{segment.StartUtc:yyyy/MM/dd}/{segment.FileName}";
    public static bool Overlaps(VideoSegment segment, DateTime fromUtc, DateTime untilUtc) => segment.StartUtc <= untilUtc && segment.EndUtc >= fromUtc;
}

public static class VideoRetention
{
    // Ring buffer: free the oldest segments that are already safe in the cloud. Without cloud upload the station is a plain local recorder.
    public static List<VideoSegment> SelectForDeletion(IEnumerable<VideoSegment> localOldestFirst, long usedBytes, long quotaBytes, bool cloudEnabled)
    {
        var selected = new List<VideoSegment>();
        foreach (var segment in localOldestFirst)
        {
            if (usedBytes <= quotaBytes) break;
            if (cloudEnabled && segment.Status != "Uploaded") continue;
            selected.Add(segment); usedBytes -= segment.SizeBytes;
        }
        return selected;
    }
}

public static class Sntp
{
    // Returns local clock minus server clock, in seconds.
    public static async Task<double> OffsetSecondsAsync(string server, CancellationToken cancellation)
    {
        var request = new byte[48]; request[0] = 0x1B;
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 3000;
        var sent = DateTime.UtcNow;
        await udp.SendAsync(request, server, 123, cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var reply = (await udp.ReceiveAsync(timeout.Token)).Buffer;
        var received = DateTime.UtcNow;
        return ((sent - ReadTimestamp(reply, 32)) + (received - ReadTimestamp(reply, 40))).TotalSeconds / 2;
    }
    public static DateTime ReadTimestamp(byte[] packet, int offset)
    {
        ulong seconds = (ulong)packet[offset] << 24 | (ulong)packet[offset + 1] << 16 | (ulong)packet[offset + 2] << 8 | packet[offset + 3];
        ulong fraction = (ulong)packet[offset + 4] << 24 | (ulong)packet[offset + 5] << 16 | (ulong)packet[offset + 6] << 8 | packet[offset + 7];
        return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds + fraction / 4294967296.0);
    }
}

public static class ParkingAlerts
{
    public static async Task RaiseOnce(ParkingDb db, string reason, string severity, CancellationToken cancellation = default)
    {
        if (!await db.Alerts.AnyAsync(x => x.Reason == reason && !x.Resolved, cancellation)) db.Alerts.Add(new() { Reason = reason, Severity = severity });
    }
}

public sealed class VideoRecorderSupervisor(IServiceScopeFactory scopes, IConfiguration config, IWebHostEnvironment environment,
    IDataProtectionProvider protection, ILogger<VideoRecorderSupervisor> logger) : BackgroundService
{
    private sealed class Recording
    {
        public Process? Process; public string Source = ""; public DateTime StartedUtc; public int Failures; public DateTime NextStartUtc; public long ListOffset;
    }
    private readonly Dictionary<string, Recording> recordings = new();
    private DateTime nextClockCheck = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var options = VideoOptions.From(config);
        var root = options.ResolveDirectory(environment);
        Directory.CreateDirectory(root);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Tick(options, root, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Video recorder tick failed"); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.SegmentSeconds / 6, 2, 10)), stop).ContinueWith(_ => { });
            }
        }
        finally { foreach (var recording in recordings.Values) StopProcess(recording); }
    }

    private async Task Tick(VideoOptions options, string root, CancellationToken stop)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkingDb>();
        var protector = protection.CreateProtector(CameraProtection.Purpose);
        var cloudEnabled = CloudOptions.From(config).Enabled;
        var cameras = await db.Cameras.Where(x => x.Enabled && x.Record24x7).ToListAsync(stop);
        var now = DateTime.UtcNow;
        foreach (var gone in recordings.Keys.Except(cameras.Select(x => x.Id)).ToList()) { StopProcess(recordings[gone]); recordings.Remove(gone); }
        foreach (var camera in cameras)
        {
            var directory = Path.Combine(root, camera.Id);
            Directory.CreateDirectory(directory);
            if (!recordings.TryGetValue(camera.Id, out var recording)) recordings[camera.Id] = recording = new() { NextStartUtc = now };
            string source;
            try { source = protector.Unprotect(camera.SourceProtected); }
            catch (CryptographicException) { await ParkingAlerts.RaiseOnce(db, "CAMERA_SOURCE_UNREADABLE:" + camera.Id, "High", stop); continue; }
            if (recording.Process is { HasExited: false } && recording.Source != source) StopProcess(recording);
            if (recording.Process is { HasExited: true }) { recording.Failures++; recording.NextStartUtc = now.AddSeconds(Math.Min(60, 2 * Math.Pow(2, recording.Failures))); recording.Process.Dispose(); recording.Process = null; }
            if (recording.Process is null && now >= recording.NextStartUtc) Start(options, recording, camera.Id, source, directory);
            await Register(db, camera, recording, directory, options, stop);
            var silentFor = now - (camera.LastSegmentUtc is { } last && last > recording.StartedUtc ? last : recording.StartedUtc);
            if (recording.Process is not null && silentFor > TimeSpan.FromSeconds(options.SegmentSeconds * 3))
            {
                // A frozen stream can keep ffmpeg alive without producing segments; restart it and tell the operators.
                await ParkingAlerts.RaiseOnce(db, "CAMERA_NOT_RECORDING:" + camera.Id, "High", stop);
                StopProcess(recording); recording.NextStartUtc = now;
            }
            else if (camera.LastSegmentUtc > now.AddSeconds(-options.SegmentSeconds * 3))
                foreach (var alert in await db.Alerts.Where(x => x.Reason == "CAMERA_NOT_RECORDING:" + camera.Id && !x.Resolved).ToListAsync(stop)) AlertLifecycle.AutoResolve(alert, "video-recorder", "Recording resumed.");
        }
        await db.SaveChangesAsync(stop);
        await EnforceQuota(db, options, root, cloudEnabled, stop);
        if (!string.IsNullOrWhiteSpace(options.NtpServer) && now >= nextClockCheck)
        {
            nextClockCheck = now.AddHours(1);
            try
            {
                var drift = Math.Abs(await Sntp.OffsetSecondsAsync(options.NtpServer, stop));
                if (drift > options.MaxClockDriftSeconds) { await ParkingAlerts.RaiseOnce(db, "CLOCK_DRIFT", "High", stop); await db.SaveChangesAsync(stop); }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !stop.IsCancellationRequested)
            { logger.LogWarning("Clock check against {Server} failed", options.NtpServer); nextClockCheck = now.AddMinutes(10); }
        }
    }

    private void Start(VideoOptions options, Recording recording, string cameraId, string source, string directory)
    {
        KillLeftover(directory);
        File.Delete(Path.Combine(directory, "segments.csv"));
        recording.Source = source;
        var info = new ProcessStartInfo(options.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = false, RedirectStandardOutput = false, WorkingDirectory = directory };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" }) info.ArgumentList.Add(arg);
        if (source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) { info.ArgumentList.Add("-rtsp_transport"); info.ArgumentList.Add("tcp"); }
        // "file:" sources are simulated cameras for demos and tests: loop a local file in real time.
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) { foreach (var arg in new[] { "-re", "-stream_loop", "-1" }) info.ArgumentList.Add(arg); source = source[5..]; }
        foreach (var arg in new[] { "-i", source, "-map", "0:v", "-map", "0:a?", "-c", "copy", "-f", "segment", "-segment_time", options.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-segment_format", "mpegts", "-reset_timestamps", "1", "-strftime", "1", "-segment_list", "segments.csv", "-segment_list_type", "csv", VideoNaming.Pattern })
            info.ArgumentList.Add(arg);
        try
        {
            recording.Process = Process.Start(info);
            recording.StartedUtc = DateTime.UtcNow; recording.ListOffset = 0;
            if (recording.Process is not null) File.WriteAllText(Path.Combine(directory, "ffmpeg.pid"), recording.Process.Id.ToString(CultureInfo.InvariantCulture));
            logger.LogInformation("Recording camera {Camera}", cameraId);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogError("Cannot start ffmpeg at {Path}: {Message}", options.FfmpegPath, ex.Message);
            recording.Failures++; recording.NextStartUtc = DateTime.UtcNow.AddSeconds(60);
        }
    }

    // An ffmpeg left running by a crashed API would keep writing next to the new one.
    private static void KillLeftover(string directory)
    {
        var pidFile = Path.Combine(directory, "ffmpeg.pid");
        if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile), out var pid)) return;
        try { using var process = Process.GetProcessById(pid); if (process.ProcessName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        File.Delete(pidFile);
    }

    private static void StopProcess(Recording recording)
    {
        try { if (recording.Process is { HasExited: false }) { recording.Process.Kill(entireProcessTree: true); recording.Process.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
        recording.Process?.Dispose(); recording.Process = null;
    }

    // Closed segments come from ffmpeg's segment list; files a crashed ffmpeg never listed are adopted once clearly abandoned.
    private async Task Register(ParkingDb db, Camera camera, Recording recording, string directory, VideoOptions options, CancellationToken stop)
    {
        var closed = new Dictionary<string, double>();
        var list = Path.Combine(directory, "segments.csv");
        if (File.Exists(list))
        {
            await using var stream = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < recording.ListOffset) recording.ListOffset = 0;
            stream.Seek(recording.ListOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(stop);
            var complete = text.LastIndexOf('\n') + 1;
            recording.ListOffset += System.Text.Encoding.UTF8.GetByteCount(text[..complete]);
            foreach (var line in text[..complete].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',');
                if (parts.Length >= 3 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var start) && double.TryParse(parts[2], CultureInfo.InvariantCulture, out var end))
                    closed[Path.GetFileName(parts[0])] = end - start;
            }
        }
        var abandonedBefore = DateTime.UtcNow.AddSeconds(-Math.Max(600, options.SegmentSeconds * 5));
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.ts").Where(x => x.LastWriteTimeUtc < abandonedBefore))
            closed.TryAdd(file.Name, -1);
        if (closed.Count == 0) return;
        var names = closed.Keys.ToList();
        var known = (await db.VideoSegments.Where(x => x.CameraId == camera.Id && names.Contains(x.FileName)).Select(x => x.FileName).ToListAsync(stop)).ToHashSet();
        foreach (var (name, seconds) in closed.Where(x => !known.Contains(x.Key)))
        {
            var file = new FileInfo(Path.Combine(directory, name));
            if (!file.Exists || !VideoNaming.TryParseStart(name, out var startUtc)) continue;
            string hash;
            await using (var content = file.OpenRead()) hash = Convert.ToHexString(await SHA256.HashDataAsync(content, stop)).ToLowerInvariant();
            var endUtc = seconds > 0 ? startUtc.AddSeconds(seconds) : file.LastWriteTimeUtc;
            db.VideoSegments.Add(new() { CameraId = camera.Id, FileName = name, StartUtc = startUtc, EndUtc = endUtc, SizeBytes = file.Length, Sha256 = hash });
            if (seconds > 0) recording.Failures = 0;
            if (camera.LastSegmentUtc is null || endUtc > camera.LastSegmentUtc) camera.LastSegmentUtc = endUtc;
        }
    }

    private async Task EnforceQuota(ParkingDb db, VideoOptions options, string root, bool cloudEnabled, CancellationToken stop)
    {
        var local = db.VideoSegments.Where(x => x.Status == "Recorded" || x.Status == "Failed" || x.Status == "Uploaded");
        var used = await local.SumAsync(x => x.SizeBytes, stop);
        var quota = (long)(options.LocalQuotaGb * 1024 * 1024 * 1024);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        var lowDisk = drive.IsReady && drive.AvailableFreeSpace < drive.TotalSize * options.MinFreeDiskFraction;
        if (used <= quota && !lowDisk) return;
        var oldest = await local.OrderBy(x => x.StartUtc).Take(500).ToListAsync(stop);
        // On a nearly full disk, free exactly the shortfall below the minimum free space (other files may be using the disk too).
        var shortfall = lowDisk ? (long)(drive.TotalSize * options.MinFreeDiskFraction) - drive.AvailableFreeSpace : 0;
        var target = Math.Min(quota, used - shortfall);
        var delete = VideoRetention.SelectForDeletion(oldest, used, target, cloudEnabled);
        foreach (var segment in delete)
        {
            File.Delete(Path.Combine(root, segment.CameraId, segment.FileName));
            segment.Status = "LocalDeleted";
        }
        used -= delete.Sum(x => x.SizeBytes);
        if (cloudEnabled && used > target)
        {
            await ParkingAlerts.RaiseOnce(db, "VIDEO_DISK_FULL", "Critical", stop);
            if (lowDisk)
            {
                // Last resort: the disk must not fill completely, or recording and the database stop. Every sacrificed segment is audited.
                foreach (var segment in VideoRetention.SelectForDeletion(oldest.Except(delete), used, target, cloudEnabled: false))
                {
                    File.Delete(Path.Combine(root, segment.CameraId, segment.FileName));
                    segment.Status = "Discarded"; db.Log("video-recorder", "VideoSegmentDiscarded", segment.Id);
                }
            }
        }
        await db.SaveChangesAsync(stop);
    }
}

public sealed class CloudUploader(IServiceScopeFactory scopes, IConfiguration config, IWebHostEnvironment environment,
    IObjectStorage storage, ILogger<CloudUploader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var cloud = CloudOptions.From(config);
        var video = VideoOptions.From(config);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ParkingDb>();
                var busy = await RunOnceAsync(db, storage, cloud, video.ResolveDirectory(environment), EvidenceStorage.Directory(environment, config), DateTime.UtcNow, stop);
                if (!busy) await Task.Delay(TimeSpan.FromSeconds(5), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Cloud upload pass failed"); await Task.Delay(TimeSpan.FromSeconds(15), stop).ContinueWith(_ => { }); }
        }
    }

    // Records and photos go before video so a video backlog never hides new events. Returns true when work remains.
    public static async Task<bool> RunOnceAsync(ParkingDb db, IObjectStorage storage, CloudOptions cloud, string videoDirectory, string evidenceDirectory, DateTime now, CancellationToken stop)
    {
        var records = await db.Outbox.Where(x => x.Status != "Sent" && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now)).OrderBy(x => x.Id).Take(50).ToListAsync(stop);
        foreach (var item in records)
        {
            try
            {
                using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(item.Payload));
                // Presence keys sort by upload time, so other stations can read them incrementally (PresenceSyncWorker).
                var recordKey = item.EntityType == nameof(PresenceRecord)
                    ? $"{cloud.SiteId}/presence/{now:yyyy/MM/dd/HHmmssfff}-{item.Id:D12}-{item.EntityId}.json"
                    : $"{cloud.SiteId}/records/{item.EntityType}/{item.EntityId}/{item.Id:D12}.json";
                await storage.PutAsync(recordKey, json, "application/json", new Dictionary<string, string>(), stop);
                if (item.EvidenceId is not null)
                {
                    var name = Path.GetFileName(item.EvidenceId);
                    var photo = Path.Combine(evidenceDirectory, name);
                    var key = $"{cloud.SiteId}/evidence/{name}";
                    if (File.Exists(photo) && await storage.SizeAsync(key, stop) is null)
                    {
                        await using var content = new ThrottledStream(File.OpenRead(photo), cloud.UploadMbpsCap);
                        await storage.PutAsync(key, content, name.EndsWith(".png") ? "image/png" : "image/jpeg", new Dictionary<string, string>(), stop);
                    }
                }
                item.Status = "Sent"; item.SentUtc = now; item.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Fail(item, ex, now); }
            await db.SaveChangesAsync(stop);
        }
        var segments = await db.VideoSegments.Where(x => (x.Status == "Recorded" || x.Status == "Failed") && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now))
            .OrderBy(x => x.StartUtc).Take(5).ToListAsync(stop);
        foreach (var segment in segments)
        {
            var path = Path.Combine(videoDirectory, segment.CameraId, segment.FileName);
            if (!File.Exists(path)) { segment.Status = "Missing"; segment.LastError = "Local file not found before upload."; await db.SaveChangesAsync(stop); continue; }
            var key = VideoNaming.ObjectKey(cloud.SiteId, segment);
            try
            {
                await using (var content = new ThrottledStream(File.OpenRead(path), cloud.UploadMbpsCap))
                    await storage.PutAsync(key, content, "video/mp2t", new Dictionary<string, string> { ["sha256"] = segment.Sha256 }, stop);
                var size = await storage.SizeAsync(key, stop);
                if (size != segment.SizeBytes) throw new IOException($"Uploaded size {size} does not match local size {segment.SizeBytes}.");
                segment.Status = "Uploaded"; segment.UploadedUtc = now; segment.ObjectKey = key; segment.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                segment.Attempts++; segment.Status = "Failed"; segment.LastError = ex.Message[..Math.Min(ex.Message.Length, 500)];
                segment.NextAttemptUtc = now.AddSeconds(Math.Min(1800, 10 * Math.Pow(2, Math.Min(segment.Attempts, 10))));
            }
            await db.SaveChangesAsync(stop);
        }
        var backlogBefore = now.AddMinutes(-cloud.BacklogAlertMinutes);
        var oldestPending = await db.VideoSegments.Where(x => x.Status == "Recorded" || x.Status == "Failed").OrderBy(x => x.StartUtc).Select(x => (DateTime?)x.StartUtc).FirstOrDefaultAsync(stop);
        if (oldestPending < backlogBefore || await db.Outbox.AnyAsync(x => x.Status != "Sent" && x.CreatedUtc < backlogBefore, stop))
        { await ParkingAlerts.RaiseOnce(db, "UPLOAD_BACKLOG", "High", stop); await db.SaveChangesAsync(stop); }
        await db.Outbox.Where(x => x.Status == "Sent" && x.SentUtc < now.AddDays(-7)).ExecuteDeleteAsync(stop);
        return records.Count == 50 || segments.Count == 5;
    }
    private static void Fail(SyncOutboxItem item, Exception ex, DateTime now)
    {
        item.Attempts++; item.Status = "Failed"; item.LastError = ex.Message[..Math.Min(ex.Message.Length, 500)];
        item.NextAttemptUtc = now.AddSeconds(Math.Min(1800, 10 * Math.Pow(2, Math.Min(item.Attempts, 10))));
    }
}

// Caps upload bandwidth so 24/7 video cannot starve the site's other traffic. Zero or less means unlimited.
public sealed class ThrottledStream(Stream inner, double megabitsPerSecond) : Stream
{
    private readonly double bytesPerSecond = megabitsPerSecond * 1_000_000 / 8;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long transferred;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellation);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
    {
        var read = await inner.ReadAsync(buffer, cancellation);
        if (bytesPerSecond > 0 && read > 0)
        {
            transferred += read;
            var ahead = transferred / bytesPerSecond - clock.Elapsed.TotalSeconds;
            if (ahead > 0) await Task.Delay(TimeSpan.FromSeconds(ahead), cancellation);
        }
        return read;
    }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

public static class CameraProtection
{
    public const string Purpose = "Parking.CameraSources.v1";
}
