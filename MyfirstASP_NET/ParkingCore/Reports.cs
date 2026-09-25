using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MyfirstASP_NET.ParkingCore;

// Multi-criteria search (GC-33), statistics (GC-35) and Excel export (GC-34). All three use the same filter.
public class ReportFilter
{
    public DateTime? From { get; set; }
    public DateTime? Until { get; set; }
    public Guid? AreaId { get; set; }
    public string? LaneId { get; set; }
    public string? Outcome { get; set; }
    public string? Reason { get; set; }
    public string? Source { get; set; }
    public string? Plate { get; set; }
    public int? PersonId { get; set; }
    public string? Shift { get; set; }
    public override string ToString() => string.Join(", ", new[] {
        From is null ? null : "from " + From.Value.ToString("u"), Until is null ? null : "until " + Until.Value.ToString("u"), AreaId is null ? null : "area " + AreaId,
        LaneId is null ? null : "lane " + LaneId, Outcome is null ? null : "outcome " + Outcome, Reason is null ? null : "reason ~ " + Reason, Source is null ? null : "source " + Source,
        Plate is null ? null : "plate " + Plate, PersonId is null ? null : "person " + PersonId, Shift is null ? null : "shift " + Shift }.Where(x => x is not null)) is { Length: > 0 } text ? text : "none";
}

// Excel texts in Vietnamese (GC-39); English is the key and the fallback.
public static class ReportText
{
    private static readonly Dictionary<string, string> Vi = new()
    {
        ["Overview"] = "Tổng quan", ["Events"] = "Sự kiện", ["By area"] = "Theo khu vực", ["Performance"] = "Hiệu suất", ["Evidence"] = "Bằng chứng",
        ["Exported by"] = "Người xuất", ["Exported at (local)"] = "Thời điểm xuất (giờ địa phương)", ["Time zone"] = "Múi giờ", ["Filters"] = "Bộ lọc",
        ["Allow"] = "Cho phép", ["Deny"] = "Từ chối", ["Manual review"] = "Chờ bảo vệ duyệt", ["Passages confirmed"] = "Lượt qua đã xác nhận",
        ["Alerts"] = "Cảnh báo", ["Alerts closed"] = "Cảnh báo đã đóng", ["False alarms"] = "Báo động giả", ["False-alarm rate (of closed alerts)"] = "Tỷ lệ báo động giả (trên cảnh báo đã đóng)",
        ["Time (local)"] = "Thời gian (địa phương)", ["Area"] = "Khu vực", ["Location"] = "Vị trí", ["Lane"] = "Làn", ["Direction"] = "Chiều", ["Plate"] = "Biển số",
        ["Person"] = "Người", ["Category"] = "Loại", ["Outcome"] = "Kết quả", ["Reason"] = "Lý do", ["Source"] = "Nguồn", ["Guard"] = "Bảo vệ", ["Shift"] = "Ca",
        ["PPE"] = "Bảo hộ", ["Decision ms"] = "Quyết định (ms)", ["Command"] = "Lệnh", ["Passage (local)"] = "Qua cổng (địa phương)", ["Alert status"] = "Trạng thái cảnh báo",
        ["Evidence SHA-256"] = "SHA-256 bằng chứng", ["Entries passed"] = "Lượt vào đã qua", ["Exits passed"] = "Lượt ra đã qua", ["Denied"] = "Từ chối",
        ["Inside now"] = "Đang ở trong", ["(no area)"] = "(chưa có khu vực)", ["Measure"] = "Chỉ số", ["Count"] = "Số lượng", ["Mean (s)"] = "Trung bình (giây)",
        ["Median (s)"] = "Trung vị (giây)", ["P95 (s)"] = "P95 (giây)",
        ["Message received → decision (t1→t2)"] = "Nhận bản tin → quyết định (t1→t2)", ["Message received → open command (t1→t3)"] = "Nhận bản tin → lệnh mở (t1→t3)",
        ["Open command → controller ack (t3→t4)"] = "Lệnh mở → bộ điều khiển xác nhận (t3→t4)", ["Alert → acknowledged"] = "Cảnh báo → tiếp nhận", ["Alert → closed"] = "Cảnh báo → đóng",
        ["Event time (local)"] = "Thời gian sự kiện (địa phương)", ["Kind"] = "Loại", ["Evidence ID"] = "Mã bằng chứng", ["Size (bytes)"] = "Kích thước (byte)",
        ["SHA-256"] = "SHA-256", ["Taken by"] = "Người chụp", ["Kept until"] = "Lưu đến", ["Plate/main"] = "Biển số/ảnh chính",
        ["kept (no retention limit)"] = "lưu vô thời hạn", ["none"] = "không",
    };
    public static Func<string, string> For(string? lang) => lang == "en" ? (s => s) : (s => Vi.TryGetValue(s, out var v) ? v : s);
}

public static class Reports
{
    public const int MaxRows = 100_000;

    public static IQueryable<AccessEvent> Events(ParkingDb db, ReportFilter f, HashSet<string>? allowedLanes)
    {
        var q = db.Events.AsNoTracking().AsQueryable();
        if (allowedLanes?.ToList() is { } allowed) q = q.Where(x => allowed.Contains(x.LaneId));
        if (f.AreaId is Guid area) { var lanes = AreaCatalog.LaneIds(db, area); q = q.Where(x => lanes.Contains(x.LaneId)); }
        if (f.From is DateTime from) q = q.Where(x => x.CreatedUtc >= from);
        if (f.Until is DateTime until) q = q.Where(x => x.CreatedUtc < until);
        if (!string.IsNullOrWhiteSpace(f.LaneId)) q = q.Where(x => x.LaneId == f.LaneId);
        if (!string.IsNullOrWhiteSpace(f.Outcome)) q = q.Where(x => x.Outcome == f.Outcome);
        if (!string.IsNullOrWhiteSpace(f.Reason)) q = q.Where(x => x.Reason.Contains(f.Reason));
        if (!string.IsNullOrWhiteSpace(f.Source)) q = q.Where(x => x.Source == f.Source);
        if (!string.IsNullOrWhiteSpace(f.Plate)) { var plate = VietnamPlate.Normalize(f.Plate); q = q.Where(x => x.Plate == plate); }
        if (f.PersonId is int person) q = q.Where(x => x.PersonId == person);
        if (!string.IsNullOrWhiteSpace(f.Shift)) q = q.Where(x => x.Shift == f.Shift);
        return q;
    }

    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(x => x).ToList();
        var rank = p / 100 * (sorted.Count - 1);
        var low = (int)Math.Floor(rank); var high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }
    public static (double? Mean, double? Median, double? P95, int Count) Summary(IReadOnlyList<double> values) =>
        (values.Count == 0 ? null : values.Average(), Percentile(values, 50), Percentile(values, 95), values.Count);

    // Bucket key in the site's local time. "shift" uses the shift stored on the event (empty = outside any shift).
    public static string Bucket(AccessEvent e, string groupBy, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.CreatedUtc, DateTimeKind.Utc), zone);
        return groupBy switch {
            "hour" => local.ToString("HH:00"), "month" => local.ToString("yyyy-MM"), "year" => local.ToString("yyyy"),
            "shift" => e.Shift ?? "(no shift)", "lane" => e.LaneId, _ => local.ToString("yyyy-MM-dd") };
    }
}

[ApiController, Route("api/v1/parking"), Authorize(Policy = Perm.ViewLive)]
public class ReportController(ParkingDb db, AreaScope scope, IConfiguration config) : ControllerBase
{
    private string Actor => User.Identity?.Name ?? "unknown";

    // GC-33: history search with every filter; paged.
    [HttpGet("search")]
    public async Task<object> Search([FromQuery] ReportFilter filter, int page = 1, int pageSize = 50)
    {
        var q = Reports.Events(db, filter, await scope.Lanes());
        pageSize = Math.Clamp(pageSize, 10, 500);
        return new { total = await q.CountAsync(), items = await q.OrderByDescending(x => x.CreatedUtc).Skip((Math.Clamp(page, 1, 100000) - 1) * pageSize).Take(pageSize).ToListAsync() };
    }

    // GC-35: counts per day / month / year / hour / shift / lane. Parking areas usually use hour; mine areas use shift.
    [HttpGet("stats"), Authorize(Policy = Perm.ViewReports)]
    public async Task<object> Stats([FromQuery] ReportFilter filter, string groupBy = "day")
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await db.Site()).TimeZone);
        var events = await Reports.Events(db, filter, await scope.Lanes()).Take(Reports.MaxRows + 1).ToListAsync();
        var ids = events.Select(e => e.Id).ToList();
        var passed = (await db.Commands.AsNoTracking().Where(c => ids.Contains(c.EventId) && c.PassageUtc != null).Select(c => c.EventId).ToListAsync()).ToHashSet();
        var lanes = await db.Lanes.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Direction);
        return new {
            groupBy, truncated = events.Count > Reports.MaxRows,
            rows = events.Take(Reports.MaxRows).GroupBy(e => Reports.Bucket(e, groupBy, zone)).OrderBy(g => g.Key).Select(g => new {
                bucket = g.Key, events = g.Count(), allow = g.Count(e => e.Outcome == "Allow"), deny = g.Count(e => e.Outcome == "Deny"),
                review = g.Count(e => e.Outcome == "ManualReview"), guardApproved = g.Count(e => e.Reason == "GUARD_APPROVED"),
                entries = g.Count(e => passed.Contains(e.Id) && lanes.GetValueOrDefault(e.LaneId) == "Entry"),
                exits = g.Count(e => passed.Contains(e.Id) && lanes.GetValueOrDefault(e.LaneId) == "Exit") })
        };
    }

    // GC-34: Excel with 5 sheets using the same filter as the screen. Audited.
    [HttpGet("export"), Authorize(Policy = Perm.ViewReports)]
    public async Task<IActionResult> Export([FromQuery] ReportFilter filter, [FromQuery] string? lang = "vi")
    {
        var L = ReportText.For(lang);
        var allowed = await scope.Lanes();
        var events = await Reports.Events(db, filter, allowed).OrderBy(x => x.CreatedUtc).Take(Reports.MaxRows + 1).ToListAsync();
        if (events.Count > Reports.MaxRows) return Conflict(new { message = $"More than {Reports.MaxRows:N0} events match. Narrow the filter." });
        var site = await db.Site();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone);
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var ids = events.Select(e => e.Id).ToList();
        var commands = await db.Commands.AsNoTracking().Where(c => ids.Contains(c.EventId)).ToDictionaryAsync(c => c.EventId);
        var alerts = await db.Alerts.AsNoTracking().Where(a => a.EventId != null && ids.Contains(a.EventId.Value)).ToListAsync();
        var images = await db.EvidenceImages.AsNoTracking().Where(i => ids.Contains(i.EventId)).ToListAsync();
        var lanes = await db.Lanes.AsNoTracking().ToDictionaryAsync(l => l.Id);
        var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id);
        var areas = await db.Areas.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var vehicles = await db.Vehicles.AsNoTracking().ToDictionaryAsync(v => v.NormalizedPlate);
        (string Area, string Location) Place(string laneId) => lanes.TryGetValue(laneId, out var lane) && lane.LocationId is Guid l && locations.TryGetValue(l, out var loc)
            ? (areas.TryGetValue(loc.AreaId, out var a) ? a.Name : "", loc.Name) : ("", "");
        var evidenceDirectory = EvidenceStorage.Directory(HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>(), config);
        var retentionDays = config.GetValue<int>("Parking:EvidenceRetentionDays");

        using var book = new XLWorkbook();
        // 1. Overview
        var overview = book.AddWorksheet(L("Overview"));
        var closed = alerts.Where(a => a.Resolved).ToList();
        var falseAlarms = closed.Count(a => a.Status == "FalseAlarm");
        var rowsOverview = new List<(string, object)> {
            ("Exported by", Actor), ("Exported at (local)", Local(DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm")), ("Time zone", site.TimeZone), ("Filters", filter.ToString()),
            ("Events", events.Count), ("Allow", events.Count(e => e.Outcome == "Allow")), ("Deny", events.Count(e => e.Outcome == "Deny")),
            ("Manual review", events.Count(e => e.Outcome == "ManualReview")), ("Passages confirmed", commands.Values.Count(c => c.PassageUtc != null)),
            ("Alerts", alerts.Count), ("Alerts closed", closed.Count), ("False alarms", falseAlarms),
            ("False-alarm rate (of closed alerts)", closed.Count == 0 ? "—" : Math.Round(100.0 * falseAlarms / closed.Count, 1) + " %") };
        foreach (var severity in alerts.GroupBy(a => a.Severity)) rowsOverview.Add(("Alerts · " + severity.Key, severity.Count()));
        for (var i = 0; i < rowsOverview.Count; i++) { overview.Cell(i + 1, 1).Value = rowsOverview[i].Item1.StartsWith("Alerts · ") ? L("Alerts") + rowsOverview[i].Item1[6..] : L(rowsOverview[i].Item1); overview.Cell(i + 1, 2).Value = XLCellValue.FromObject(rowsOverview[i].Item2); }
        overview.Column(1).Style.Font.Bold = true;

        // 2. Events
        var sheet = book.AddWorksheet(L("Events"));
        string[] headers = ["Time (local)", "Area", "Location", "Lane", "Direction", "Plate", "Person", "Category", "Outcome", "Reason", "Source", "Guard", "Shift", "PPE",
            "Decision ms", "Command", "Passage (local)", "Alert status", "Evidence", "Evidence SHA-256"];
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = L(headers[c]);
        var row = 2;
        foreach (var e in events)
        {
            var (areaName, locationName) = Place(e.LaneId);
            commands.TryGetValue(e.Id, out var command);
            object?[] values = [Local(e.CreatedUtc), areaName, locationName, e.LaneId, lanes.GetValueOrDefault(e.LaneId)?.Direction, e.Plate, e.PersonId,
                vehicles.GetValueOrDefault(e.Plate)?.Category, e.Outcome, e.Reason, e.Source, e.ReviewBy, e.Shift, e.PpeOutcome,
                e.ReceivedUtc is DateTime r ? Math.Round((e.CreatedUtc - r).TotalMilliseconds) : null, command?.Status,
                command?.PassageUtc is DateTime p ? Local(p) : null, string.Join(", ", alerts.Where(a => a.EventId == e.Id).Select(a => a.Status)), e.EvidenceId, e.EvidenceSha256];
            for (var c = 0; c < values.Length; c++) sheet.Cell(row, c + 1).Value = XLCellValue.FromObject(values[c]);
            row++;
        }
        sheet.Column(1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss"; sheet.Column(17).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";

        // 3. By area
        var byArea = book.AddWorksheet(L("By area"));
        string[] areaHeaders = ["Area", "Events", "Entries passed", "Exits passed", "Denied", "Manual review", "Alerts", "Inside now"];
        for (var c = 0; c < areaHeaders.Length; c++) byArea.Cell(1, c + 1).Value = L(areaHeaders[c]);
        var (insideNow, _) = PresenceBook.Compute(await PresenceBook.Window(db, DateTime.UtcNow).ToListAsync());
        row = 2;
        foreach (var group in events.GroupBy(e => Place(e.LaneId).Area).OrderBy(g => g.Key))
        {
            var code = areas.Values.FirstOrDefault(a => a.Name == group.Key)?.Code;
            object?[] values = [group.Key.Length == 0 ? L("(no area)") : group.Key, group.Count(),
                group.Count(e => commands.GetValueOrDefault(e.Id)?.PassageUtc != null && lanes.GetValueOrDefault(e.LaneId)?.Direction == "Entry"),
                group.Count(e => commands.GetValueOrDefault(e.Id)?.PassageUtc != null && lanes.GetValueOrDefault(e.LaneId)?.Direction == "Exit"),
                group.Count(e => e.Outcome == "Deny"), group.Count(e => e.Outcome == "ManualReview"), alerts.Count(a => group.Any(e => e.Id == a.EventId)),
                code is null ? null : insideNow.Count(x => x.AreaCode == code)];
            for (var c = 0; c < values.Length; c++) byArea.Cell(row, c + 1).Value = XLCellValue.FromObject(values[c]);
            row++;
        }

        // 4. Processing performance (mean / median / P95, seconds)
        var perf = book.AddWorksheet(L("Performance"));
        string[] perfHeaders = ["Measure", "Count", "Mean (s)", "Median (s)", "P95 (s)"];
        for (var c = 0; c < perfHeaders.Length; c++) perf.Cell(1, c + 1).Value = L(perfHeaders[c]);
        var measures = new (string, List<double>)[] {
            ("Message received → decision (t1→t2)", events.Where(e => e.ReceivedUtc != null).Select(e => (e.CreatedUtc - e.ReceivedUtc!.Value).TotalSeconds).ToList()),
            ("Message received → open command (t1→t3)", events.Where(e => e.ReceivedUtc != null && commands.ContainsKey(e.Id)).Select(e => (commands[e.Id].CreatedUtc - e.ReceivedUtc!.Value).TotalSeconds).ToList()),
            ("Open command → controller ack (t3→t4)", commands.Values.Where(c => c.AckUtc != null).Select(c => (c.AckUtc!.Value - c.CreatedUtc).TotalSeconds).ToList()),
            ("Alert → acknowledged", alerts.Where(a => a.AcknowledgedUtc != null).Select(a => (a.AcknowledgedUtc!.Value - a.CreatedUtc).TotalSeconds).ToList()),
            ("Alert → closed", alerts.Where(a => a.ResolvedUtc != null).Select(a => (a.ResolvedUtc!.Value - a.CreatedUtc).TotalSeconds).ToList()) };
        row = 2;
        foreach (var (name, values) in measures)
        {
            var (mean, median, p95, n) = Reports.Summary(values);
            perf.Cell(row, 1).Value = L(name); perf.Cell(row, 2).Value = n;
            perf.Cell(row, 3).Value = mean is null ? "—" : Math.Round(mean.Value, 3); perf.Cell(row, 4).Value = median is null ? "—" : Math.Round(median.Value, 3);
            perf.Cell(row, 5).Value = p95 is null ? "—" : Math.Round(p95.Value, 3); row++;
        }

        // 5. Evidence (ID, size, SHA-256, retention)
        var evidence = book.AddWorksheet(L("Evidence"));
        string[] evHeaders = ["Event time (local)", "Kind", "Evidence ID", "Size (bytes)", "SHA-256", "Taken by", "Kept until"];
        for (var c = 0; c < evHeaders.Length; c++) evidence.Cell(1, c + 1).Value = L(evHeaders[c]);
        row = 2;
        long? Size(string id) { var f = new FileInfo(Path.Combine(evidenceDirectory, Path.GetFileName(id))); return f.Exists ? f.Length : null; }
        object Until(DateTime created) => retentionDays > 0 ? Local(created.AddDays(retentionDays)) : L("kept (no retention limit)");
        foreach (var e in events.Where(e => e.EvidenceId != null))
        {
            object?[] values = [Local(e.CreatedUtc), L("Plate/main"), e.EvidenceId, Size(e.EvidenceId!), e.EvidenceSha256, e.Source, Until(e.CreatedUtc)];
            for (var c = 0; c < values.Length; c++) evidence.Cell(row, c + 1).Value = XLCellValue.FromObject(values[c]);
            row++;
        }
        foreach (var image in images)
        {
            object?[] values = [Local(events.First(e => e.Id == image.EventId).CreatedUtc), image.Kind, image.FileId, Size(image.FileId), image.Sha256, image.TakenBy, Until(image.CreatedUtc)];
            for (var c = 0; c < values.Length; c++) evidence.Cell(row, c + 1).Value = XLCellValue.FromObject(values[c]);
            row++;
        }
        foreach (var ws in book.Worksheets) { ws.Row(1).Style.Font.Bold = true; ws.SheetView.FreezeRows(1); ws.Columns().AdjustToContents(8, 60); }

        db.Log(Actor, "ExportExcel:" + events.Count + " events", filter.ToString()); await db.SaveChangesAsync();
        await using var stream = new MemoryStream();
        book.SaveAs(stream);
        Response.Headers.CacheControl = "no-store";
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"parking-report-{Local(DateTime.UtcNow):yyyyMMdd-HHmm}.xlsx");
    }
}
