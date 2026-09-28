using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

public sealed class ParkingMaintenance(IServiceScopeFactory scopes, IConfiguration config, IHttpClientFactory clients,
    IWebHostEnvironment environment, ParkingLock mutex, ILogger<ParkingMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stop);
                await Tick(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Parking maintenance failed"); }
        }
    }
    private async Task Tick(CancellationToken stop)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkingDb>();
        await mutex.Gate.WaitAsync(stop);
        try
        {
            var site = await db.Site(stop);
            var checkedAt = DateTime.UtcNow;
            foreach (var lane in await db.Lanes.Where(x => !x.Simulation).ToListAsync(stop))
            {
                // Each lane may have its own offline threshold (GC-44), so the cutoff is evaluated per lane.
                if (lane.LastHeartbeatUtc is DateTime seen && checkedAt - seen <= LaneTimings.Resolve(site, lane).OfflineAlert) continue;
                var reason = "DEVICE_OFFLINE:" + lane.Id;
                if (!await db.Alerts.AnyAsync(x => x.Reason == reason && !x.Resolved, stop)) db.Alerts.Add(new() { Reason = reason, Severity = "High" });
            }
            // GC-27: AI devices that went silent.
            foreach (var camera in await db.Cameras.Where(x => x.Enabled && x.SilentAlertSeconds != null && x.ExternalDevice != null).ToListAsync(stop))
            {
                var reason = "CAMERA_SILENT:" + camera.Id;
                var silent = camera.LastMessageUtc is null || (DateTime.UtcNow - camera.LastMessageUtc.Value).TotalSeconds > camera.SilentAlertSeconds;
                var open = await db.Alerts.Where(x => x.Reason == reason && !x.Resolved).ToListAsync(stop);
                if (silent && open.Count == 0) db.Alerts.Add(new() { Reason = reason, Severity = "High" });
                if (!silent) foreach (var alert in open) AlertLifecycle.AutoResolve(alert, "maintenance", "Device is sending again.");
            }
            // GC-19: overstay per area.
            var overstayAreas = await db.Areas.Where(a => a.OverstayMinutes != null).ToListAsync(stop);
            if (overstayAreas.Count > 0)
            {
                var codes = overstayAreas.Select(a => a.Code).ToList();
                var (inside, _) = PresenceBook.Compute(await PresenceBook.Window(db, DateTime.UtcNow).Where(x => codes.Contains(x.AreaCode)).ToListAsync(stop));
                foreach (var person in inside)
                {
                    var limit = overstayAreas.First(a => a.Code == person.AreaCode).OverstayMinutes!.Value;
                    if ((DateTime.UtcNow - person.SinceUtc).TotalMinutes <= limit) continue;
                    var reason = "OVERSTAY:" + person.AreaCode + ":" + person.SubjectKey + ":" + person.SinceUtc.ToString("yyyyMMddHHmm");
                    if (!await db.Alerts.AnyAsync(x => x.Reason == reason, stop)) db.Alerts.Add(new() { Reason = reason, Severity = "High" });
                }
            }
            // Camera inbox: drop the raw payload (base64 images) 7 days after processing; failed messages are kept 30 days for diagnosis.
            var processedBefore = DateTime.UtcNow.AddDays(-7); var failedBefore = DateTime.UtcNow.AddDays(-30);
            foreach (var item in await db.CameraInbox.Where(x => x.Payload != null && x.ProcessedUtc != null
                && ((x.Status != "Failed" && x.ProcessedUtc < processedBefore) || x.ProcessedUtc < failedBefore)).Take(200).ToListAsync(stop)) item.Payload = null;
            var days = config.GetValue<int>("Parking:EvidenceRetentionDays");
            if (days > 0)
            {
                var before = DateTime.UtcNow.AddDays(-days);
                foreach (var entry in await db.Events.Where(x => x.CreatedUtc < before && x.EvidenceId != null).Take(100).ToListAsync(stop))
                {
                    var path = Path.Combine(EvidenceStorage.Directory(environment, config), Path.GetFileName(entry.EvidenceId!));
                    File.Delete(path);
                    entry.EvidenceId = null; db.Log("retention-worker", "DeleteExpiredEvidence", entry.Id);
                }
            }
            await db.SaveChangesAsync(stop);
        }
        finally { mutex.Gate.Release(); }
        var url = config["Parking:AlertWebhookUrl"];
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme != "https") throw new InvalidOperationException("Alert webhook must use HTTPS.");
        var now = DateTime.UtcNow;
        var alerts = await db.Alerts.Where(x => !x.Resolved && x.DeliveredUtc == null && x.DeliveryAttempts < 5 && (x.NextDeliveryUtc == null || x.NextDeliveryUtc <= now)).Take(10).ToListAsync(stop);
        foreach (var alert in alerts)
        {
            alert.DeliveryAttempts++;
            alert.NextDeliveryUtc = now.AddSeconds(Math.Pow(2, alert.DeliveryAttempts) * 15);
            await db.SaveChangesAsync(stop);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Parking:AlertWebhookKey"]);
                request.Headers.Add("Idempotency-Key", alert.Id.ToString());
                request.Content = JsonContent.Create(new { alert.Id, alert.EventId, alert.Reason, alert.Severity, alert.CreatedUtc });
                using var response = await clients.CreateClient("parking-devices").SendAsync(request, stop);
                response.EnsureSuccessStatusCode(); alert.DeliveredUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(stop);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            { logger.LogWarning("Alert {AlertId} delivery attempt {Attempt} failed", alert.Id, alert.DeliveryAttempts); }
        }
    }
}
[ApiController, Route("api/v1/parking/health")]
public class ParkingHealthController(ParkingDb db, IHttpClientFactory clients, IConfiguration config, AreaScope scope) : ControllerBase
{
    [HttpGet, Authorize(Policy = Perm.ViewLive)]
    public async Task<object> Get()
    {
        var ai = "Unavailable";
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await clients.CreateClient("parking-ai").GetAsync((config["Parking:PlateAiUrl"] ?? "http://127.0.0.1:8001") + "/health", cancellation.Token);
            ai = response.IsSuccessStatusCode ? "Ready" : "NotReady";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        var allowed = (await scope.Lanes())?.ToList();
        var inbox = await db.CameraInbox.GroupBy(x => x.Status).Select(g => new { status = g.Key, count = g.Count(), last = g.Max(x => x.ReceivedUtc) }).ToListAsync();
        var inboxErrors = await db.CameraInbox.AsNoTracking().Where(x => x.Status == "Failed").OrderByDescending(x => x.Id).Take(5)
            .Select(x => new { x.Id, x.Adapter, x.ReceivedUtc, x.Error }).ToListAsync();
        return new { inbox, inboxErrors, database = await db.Database.CanConnectAsync(), plateAi = ai, liveBarrierEnabled = config.GetValue<bool>("Parking:EnableLiveBarrier"), approvedModel = config["Parking:ApprovedPlateModel"], lanes = await db.Lanes.AsNoTracking().Where(x => allowed == null || allowed.Contains(x.Id)).ToListAsync() };
    }
}
