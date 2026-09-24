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
    [RegularExpression("^(Admin|Guard|Auditor)$")] public string Role { get; set; } = "Guard";
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
        if (user is null || new PasswordHasher<ParkingUser>().VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Invalid username or password." });
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        db.Log(user.Username, "Login", user.Id);
        await db.SaveChangesAsync();
        return Ok(new { user.Username, user.Role });
    }
    [Authorize, HttpGet("me")]
    public object Me() => new { username = User.Identity!.Name, role = User.FindFirstValue(ClaimTypes.Role) };
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(); return NoContent(); }
    [Authorize(Roles = "Admin"), HttpGet("users")]
    public async Task<object> Users() => await db.Users.Select(x => new { x.Id, x.Username, x.Role, x.Active }).ToListAsync();
    [Authorize(Roles = "Admin"), HttpPost("users/{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();
        if (user.Username == User.Identity!.Name || user.Role == "Admin") return Conflict(new { message = "Administrator accounts cannot be deactivated through this endpoint." });
        user.Active = false; db.Log(User.Identity.Name!, "DeactivateUser", id); await db.SaveChangesAsync(); return NoContent();
    }
    [Authorize(Roles = "Admin"), HttpPost("users")]
    public async Task<IActionResult> Create(NewUserRequest request)
    {
        var name = request.Username.Trim().ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.Username == name)) return Conflict(new { message = "Username already exists." });
        var user = new ParkingUser { Username = name, Role = request.Role };
        user.PasswordHash = new PasswordHasher<ParkingUser>().HashPassword(user, request.Password);
        db.Users.Add(user); db.Log(User.Identity!.Name!, "CreateUser", user.Id); await db.SaveChangesAsync();
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
