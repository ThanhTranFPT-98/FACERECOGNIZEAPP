using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DSAVehicle.ParkingCore;

public class Role
{
    [MaxLength(40)] public string Id { get; set; } = "";
    [MaxLength(100)] public string Name { get; set; } = "";
    public bool BuiltIn { get; set; }
    // Permission set version this built-in role was last seeded with.
    public int SeedVersion { get; set; } = 1;
}
public class RolePermission
{
    [MaxLength(40)] public string RoleId { get; set; } = "";
    [MaxLength(40)] public string Permission { get; set; } = "";
}

// Endpoints ask for a permission, never a role name (GC-36): roles are data an admin can change.
public static class Perm
{
    public const string ViewLive = "ViewLive", ViewEvidence = "ViewEvidence", OperateLane = "OperateLane", ManageVisitors = "ManageVisitors",
        HandleAlerts = "HandleAlerts", ManageVehicles = "ManageVehicles", ManageSite = "ManageSite", ManageUsers = "ManageUsers",
        ViewAudit = "ViewAudit", Reconcile = "Reconcile", ManagePeople = "ManagePeople", MarkFalseAlarm = "MarkFalseAlarm", ViewReports = "ViewReports";
    public static readonly string[] All = [ViewLive, ViewEvidence, OperateLane, ManageVisitors, HandleAlerts, ManageVehicles, ManageSite, ManageUsers, ViewAudit, Reconcile, ManagePeople, MarkFalseAlarm, ViewReports];
    // Version in which each permission appeared. Built-in roles seeded earlier receive it once on upgrade (see RoleCatalog.Seed).
    public static readonly Dictionary<string, int> Introduced = new() { [ManagePeople] = 2, [MarkFalseAlarm] = 2, [ViewReports] = 2 };
    public const int Version = 2;
    // Role-name policy for actions that could grant more rights than the caller has (editing roles, assigning Admin).
    public const string AdminOnly = "AdminOnly";
}

public static class RoleCatalog
{
    public const string Admin = "Admin";
    // Admin, Guard and Auditor reproduce the rights of the former fixed roles exactly, so upgraded accounts behave the same.
    public static readonly Dictionary<string, (string Name, string[] Permissions)> BuiltIn = new()
    {
        [Admin] = ("Administrator", Perm.All),
        ["Guard"] = ("Guard / operator", [Perm.ViewLive, Perm.ViewEvidence, Perm.OperateLane, Perm.ManageVisitors, Perm.HandleAlerts]),
        ["Auditor"] = ("Auditor", [Perm.ViewLive, Perm.ViewAudit]),
        ["Viewer"] = ("Viewer", [Perm.ViewLive]),
        ["SafetyOfficer"] = ("Safety officer", [Perm.ViewLive, Perm.ViewEvidence, Perm.HandleAlerts, Perm.MarkFalseAlarm, Perm.ViewAudit, Perm.ViewReports]),
        ["Manager"] = ("Manager", [Perm.ViewLive, Perm.ViewEvidence, Perm.ManageVisitors, Perm.ManageVehicles, Perm.ManagePeople, Perm.ViewAudit, Perm.ViewReports]),
        ["Technician"] = ("Technician", [Perm.ViewLive, Perm.ManageSite]),
    };

    // Adds missing built-in roles. Existing built-in roles get permissions introduced after they were seeded, once;
    // anything an admin removed or added before stays as the admin left it.
    public static async Task Seed(ParkingDb db)
    {
        var existing = await db.Roles.ToListAsync();
        foreach (var (id, (name, permissions)) in BuiltIn)
        {
            var role = existing.FirstOrDefault(x => x.Id == id);
            if (role is null)
            {
                db.Roles.Add(new Role { Id = id, Name = name, BuiltIn = true, SeedVersion = Perm.Version });
                db.RolePermissions.AddRange(permissions.Select(p => new RolePermission { RoleId = id, Permission = p }));
                continue;
            }
            if (!role.BuiltIn || role.SeedVersion >= Perm.Version) continue;
            var have = await db.RolePermissions.Where(x => x.RoleId == id).Select(x => x.Permission).ToListAsync();
            db.RolePermissions.AddRange(permissions.Where(p => Perm.Introduced.GetValueOrDefault(p) > role.SeedVersion && !have.Contains(p))
                .Select(p => new RolePermission { RoleId = id, Permission = p }));
            role.SeedVersion = Perm.Version;
        }
    }

    public static async Task<HashSet<string>> Permissions(ParkingDb db, string? roleId)
    {
        if (roleId == Admin) return Perm.All.ToHashSet();
        if (string.IsNullOrEmpty(roleId) || roleId == "Device") return [];
        return (await db.RolePermissions.AsNoTracking().Where(x => x.RoleId == roleId).Select(x => x.Permission).ToListAsync()).ToHashSet();
    }

    public static async Task<Role> Save(ParkingDb db, RoleInput input)
    {
        var id = input.Id.Trim();
        if (id == Admin) throw new InvalidOperationException("The Admin role always has every permission and cannot be changed.");
        var unknown = input.Permissions.Except(Perm.All).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException("Unknown permission: " + string.Join(", ", unknown));
        var role = await db.Roles.FindAsync(id);
        if (role is null) { role = new Role { Id = id }; db.Roles.Add(role); }
        role.Name = input.Name.Trim();
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync());
        // Every role can see the live view; without it the other permissions have no screen to act from.
        db.RolePermissions.AddRange(input.Permissions.Append(Perm.ViewLive).Distinct().Select(p => new RolePermission { RoleId = id, Permission = p }));
        return role;
    }

    public static async Task<Role> Delete(ParkingDb db, string id)
    {
        var role = await db.Roles.FindAsync(id) ?? throw new InvalidOperationException("Role not found.");
        if (role.BuiltIn) throw new InvalidOperationException("Built-in roles cannot be deleted.");
        if (await db.Users.AnyAsync(x => x.Role == id)) throw new InvalidOperationException("Move the users of this role to another role first.");
        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync());
        db.Roles.Remove(role); return role;
    }

    // Lockout and escalation rules for changing an account. Caller must hold ManageUsers.
    public static async Task<ParkingUser> UpdateUser(ParkingDb db, Guid id, UserUpdate input, string actorName, bool actorIsAdmin)
    {
        var user = await db.Users.FindAsync(id) ?? throw new InvalidOperationException("User not found.");
        if (user.Username == actorName) throw new InvalidOperationException("You cannot change your own role, expiry or status.");
        if (!await db.Roles.AnyAsync(x => x.Id == input.Role)) throw new InvalidOperationException("Unknown role.");
        if ((input.Role == Admin || user.Role == Admin) && !actorIsAdmin) throw new InvalidOperationException("Only an administrator can grant or change the Admin role.");
        var expires = input.ExpiresUtc?.UtcDateTime;
        var staysAdmin = input.Role == Admin && input.Active && (expires is null || expires > DateTime.UtcNow);
        if (user.Role == Admin && !staysAdmin && !await OtherActiveAdmin(db, user.Id)) throw new InvalidOperationException("At least one active administrator must remain.");
        user.Role = input.Role; user.ExpiresUtc = expires; user.Active = input.Active;
        return user;
    }
    private static Task<bool> OtherActiveAdmin(ParkingDb db, Guid except)
    {
        var now = DateTime.UtcNow;
        return db.Users.AnyAsync(x => x.Id != except && x.Role == Admin && x.Active && (x.ExpiresUtc == null || x.ExpiresUtc > now));
    }
    public static bool Usable(ParkingUser user, DateTime now) => user.Active && (user.ExpiresUtc is null || user.ExpiresUtc > now);
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;
public sealed class PermissionHandler(ParkingDb db) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        var role = context.User.FindFirstValue(ClaimTypes.Role);
        if (!(await RoleCatalog.Permissions(db, role)).Contains(requirement.Permission)) return;
        // CR-005: site-wide configuration needs an account without area limits.
        if (AreaScope.UnscopedOnly.Contains(requirement.Permission) && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            && await AreaScope.LanesFor(db, id, role) is not null) return;
        context.Succeed(requirement);
    }
}

public class RoleInput
{
    [Required, RegularExpression("^[a-zA-Z0-9_-]{1,40}$")] public string Id { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public string[] Permissions { get; set; } = [];
}
public class UserUpdate
{
    public Guid[] AreaIds { get; set; } = [];
    [Required, StringLength(40)] public string Role { get; set; } = "Guard";
    public DateTimeOffset? ExpiresUtc { get; set; }
    public bool Active { get; set; } = true;
}

[ApiController, Route("api/auth/roles")]
public class RoleController(ParkingDb db) : ControllerBase
{
    [HttpGet, Authorize(Policy = Perm.ManageUsers)]
    public async Task<object> Get()
    {
        var permissions = await db.RolePermissions.AsNoTracking().ToListAsync();
        var roles = await db.Roles.AsNoTracking().OrderByDescending(x => x.BuiltIn).ThenBy(x => x.Id).ToListAsync();
        var counts = await db.Users.GroupBy(x => x.Role).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        return new { permissions = Perm.All, roles = roles.Select(r => new { r.Id, r.Name, r.BuiltIn,
            permissions = r.Id == RoleCatalog.Admin ? Perm.All : permissions.Where(p => p.RoleId == r.Id).Select(p => p.Permission).ToArray(),
            users = counts.FirstOrDefault(c => c.Key == r.Id)?.Count ?? 0 }) };
    }
    [HttpPut, Authorize(Policy = Perm.AdminOnly)]
    public Task<IActionResult> Save(RoleInput input) => Run(async () => { var role = await RoleCatalog.Save(db, input); db.Log(Actor, "SaveRole:" + string.Join(",", input.Permissions), role.Id); return role; });
    [HttpDelete("{id}"), Authorize(Policy = Perm.AdminOnly)]
    public Task<IActionResult> Delete(string id) => Run(async () => { var role = await RoleCatalog.Delete(db, id); db.Log(Actor, "DeleteRole", id); return role; });
    private string Actor => User.Identity?.Name ?? "unknown";
    private async Task<IActionResult> Run(Func<Task<object>> action)
    {
        try { var result = await action(); await db.SaveChangesAsync(); return Ok(result); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }
}
