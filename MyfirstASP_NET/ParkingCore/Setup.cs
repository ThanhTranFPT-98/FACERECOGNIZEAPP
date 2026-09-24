using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace MyfirstASP_NET.ParkingCore;

[Authorize(Roles = "Admin,Guard,Auditor")]
public class ParkingHub : Hub { }

public static class ParkingSetup
{
    public static void AddParking(this WebApplicationBuilder builder)
    {
        var directory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        if (builder.Configuration["Parking:Provider"] == "SqlServer")
        {
            builder.Services.AddDbContext<ParkingSqlServerDb>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("Parking")));
            builder.Services.AddScoped<ParkingDb>(services => services.GetRequiredService<ParkingSqlServerDb>());
        }
        else builder.Services.AddDbContext<ParkingDb>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Parking") ?? $"Data Source={Path.Combine(directory, "parking.db")}"));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => {
                options.Cookie.Name = "parking-session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                options.Events.OnValidatePrincipal = async ctx => {
                    var db = ctx.HttpContext.RequestServices.GetRequiredService<ParkingDb>();
                    var name = ctx.Principal?.Identity?.Name;
                    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Username == name && x.Active);
                    if (user is null || !ctx.Principal!.IsInRole(user.Role)) { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(); }
                };
            }).AddScheme<AuthenticationSchemeOptions, DeviceAuthentication>("Device", _ => { });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-Token");
        builder.Services.AddRateLimiter(options => {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });
        builder.Services.AddHttpClient("parking-ai", client => client.Timeout = TimeSpan.FromSeconds(20));
        builder.Services.AddHttpClient("parking-devices", client => client.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton<ParkingLock>();
        builder.Services.AddScoped<ParkingService>();
        builder.Services.AddHostedService<ParkingMaintenance>();
    }
    public static async Task InitializeParking(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkingDb>();
        await db.Database.MigrateAsync();
        if (!await db.Users.AnyAsync())
        {
            var password = app.Configuration["Parking:BootstrapPassword"];
            if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
                throw new InvalidOperationException("Set Parking__BootstrapPassword (at least 12 characters) for first startup. No default password is provided.");
            var user = new ParkingUser { Username = (app.Configuration["Parking:BootstrapUsername"] ?? "admin").Trim().ToUpperInvariant(), Role = "Admin" };
            user.PasswordHash = new PasswordHasher<ParkingUser>().HashPassword(user, password);
            db.Users.Add(user);
        }
        if (!await db.Lanes.AnyAsync()) db.Lanes.AddRange(new Lane(), new Lane { Id = "exit-1", Direction = "Exit" });
        // A command left Pending across a process crash has an unknown physical outcome; never replay it.
        foreach (var command in await db.Commands.Where(x => x.Status == "Pending").ToListAsync()) command.Status = "Unknown";
        await db.SaveChangesAsync();
    }
    public static void UseParkingSecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(async (context, next) => {
            if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && !context.Request.Path.StartsWithSegments("/api/v1/parking/device"))
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { message = "Invalid CSRF token. Reload and sign in again." }); return; }
            }
            // Legacy management APIs were unauthenticated. Restrict them to admins until their finer roles are migrated.
            if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/auth")
                && !context.Request.Path.StartsWithSegments("/api/v1/parking") && !UserIsAdmin(context))
            { context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true ? 403 : 401; return; }
            if (context.Request.Path.StartsWithSegments("/snapshots") && !UserIsAdmin(context))
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
            await next();
        });
    }
    private static bool UserIsAdmin(HttpContext context) => context.User.IsInRole("Admin");
}
public sealed class ParkingLock { public SemaphoreSlim Gate { get; } = new(1, 1); }
