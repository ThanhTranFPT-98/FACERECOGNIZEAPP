using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MyfirstASP_NET.ParkingCore;

public class LoginRequest
{
    [Required, StringLength(100)] public string Username { get; set; } = "";
    [Required, StringLength(200, MinimumLength = 12)] public string Password { get; set; } = "";
}
public class NewUserRequest : LoginRequest
{
    [Required, StringLength(40)] public string Role { get; set; } = "Guard";
    public DateTimeOffset? ExpiresUtc { get; set; }
    public Guid[] AreaIds { get; set; } = [];
}
[ApiController, Route("api/auth")]
public class SessionController(ParkingDb db, IAntiforgery antiforgery) : ControllerBase
{
    [AllowAnonymous, HttpGet("csrf")]
    public object Csrf() => new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken };

    [AllowAnonymous, EnableRateLimiting("login"), HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var name = request.Username.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == name && x.Active);
        if (user?.ExpiresUtc <= DateTime.UtcNow) return Unauthorized(new { message = "This account has expired. Ask an administrator to extend it." });
        if (user is null || new PasswordHasher<ParkingUser>().VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Invalid username or password." });
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        db.Log(user.Username, "Login", user.Id);
        await db.SaveChangesAsync();
        return Ok(new { user.Username, user.Role, permissions = await RoleCatalog.Permissions(db, user.Role) });
    }
    // The UI hides what the user can't do; the server still checks every call.
    [Authorize, HttpGet("me")]
    public async Task<object> Me() => new { username = User.Identity!.Name, role = User.FindFirstValue(ClaimTypes.Role),
        permissions = await RoleCatalog.Permissions(db, User.FindFirstValue(ClaimTypes.Role)),
        areaIds = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? await AreaScope.AreasOf(db, id) : [] };
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(); return NoContent(); }
    [Authorize(Policy = Perm.ManageUsers), HttpGet("users")]
    public async Task<object> Users()
    {
        var areas = await db.UserAreas.AsNoTracking().ToListAsync();
        return (await db.Users.AsNoTracking().ToListAsync()).Select(x => new { x.Id, x.Username, x.Role, x.Active, x.ExpiresUtc,
            areaIds = areas.Where(a => a.UserId == x.Id).Select(a => a.AreaId) });
    }
    [Authorize(Policy = Perm.ManageUsers), HttpPut("users/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UserUpdate input)
    {
        try
        {
            var user = await RoleCatalog.UpdateUser(db, id, input, User.Identity!.Name!, User.IsInRole(RoleCatalog.Admin));
            await AreaScope.SetAreas(db, user, input.AreaIds);
            db.Log(User.Identity.Name!, $"UpdateUser:{user.Role}:{user.ExpiresUtc:o}:{user.Active}:areas={string.Join(",", input.AreaIds)}", id); await db.SaveChangesAsync();
            return Ok(new { user.Id, user.Username, user.Role, user.Active, user.ExpiresUtc });
        }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }
    [Authorize(Policy = Perm.ManageUsers), HttpPost("users/{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();
        if (user.Username == User.Identity!.Name || user.Role == "Admin") return Conflict(new { message = "Administrator accounts cannot be deactivated through this endpoint." });
        user.Active = false; db.Log(User.Identity.Name!, "DeactivateUser", id); await db.SaveChangesAsync(); return NoContent();
    }
    [Authorize(Policy = Perm.ManageUsers), HttpPost("users")]
    public async Task<IActionResult> Create(NewUserRequest request)
    {
        var name = request.Username.Trim().ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.Username == name)) return Conflict(new { message = "Username already exists." });
        if (!await db.Roles.AnyAsync(x => x.Id == request.Role)) return Conflict(new { message = "Unknown role." });
        if (request.Role == RoleCatalog.Admin && !User.IsInRole(RoleCatalog.Admin)) return Conflict(new { message = "Only an administrator can create administrators." });
        var user = new ParkingUser { Username = name, Role = request.Role, ExpiresUtc = request.ExpiresUtc?.UtcDateTime };
        user.PasswordHash = new PasswordHasher<ParkingUser>().HashPassword(user, request.Password);
        db.Users.Add(user);
        try { await AreaScope.SetAreas(db, user, request.AreaIds); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        db.Log(User.Identity!.Name!, "CreateUser", user.Id); await db.SaveChangesAsync();
        return Ok(new { user.Id, user.Username, user.Role });
    }
}
public sealed class DeviceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = configuration["Parking:DeviceKey"];
        var provided = Request.Headers["X-Device-Key"].ToString();
        if (string.IsNullOrEmpty(expected) || expected.Length < 32 || string.IsNullOrEmpty(provided)) return Task.FromResult(AuthenticateResult.Fail("Device credentials required."));
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        if (!CryptographicOperations.FixedTimeEquals(a, b)) return Task.FromResult(AuthenticateResult.Fail("Invalid device credentials."));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "lane-worker"), new Claim(ClaimTypes.Role, "Device") }, "Device"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Device")));
    }
}
