using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MyfirstASP_NET.ParkingCore;

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
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            foreach (var lane in await db.Lanes.Where(x => !x.Simulation && (x.LastHeartbeatUtc == null || x.LastHeartbeatUtc < cutoff)).ToListAsync(stop))
            {
                var reason = "DEVICE_OFFLINE:" + lane.Id;
                if (!await db.Alerts.AnyAsync(x => x.Reason == reason && !x.Resolved, stop)) db.Alerts.Add(new() { Reason = reason, Severity = "High" });
            }
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
public class ParkingHealthController(ParkingDb db, IHttpClientFactory clients, IConfiguration config) : ControllerBase
{
    [HttpGet, Authorize(Roles = "Admin,Guard,Auditor")]
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
        return new { database = await db.Database.CanConnectAsync(), plateAi = ai, liveBarrierEnabled = config.GetValue<bool>("Parking:EnableLiveBarrier"), approvedModel = config["Parking:ApprovedPlateModel"], lanes = await db.Lanes.AsNoTracking().ToListAsync() };
    }
}
