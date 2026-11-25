using BBTech.Api.Data;
using Npgsql;

namespace BBTech.Api;

public sealed record WorkstationIdentity(Guid Id, string Name, bool IsAdmin);

/// <summary>
/// Every /v1 call needs a valid, active workstation key (X-BBTech-Api-Key). Only the key's SHA-256 is
/// stored, so a database leak does not reveal usable keys. Fail-closed: no key, unknown key or inactive
/// workstation = 401.
/// </summary>
public sealed class WorkstationAuth : IEndpointFilter
{
    public const string Header = "X-BBTech-Api-Key";
    private const string ItemKey = "bbtech.workstation";

    public static WorkstationIdentity Current(HttpContext ctx) => (WorkstationIdentity)ctx.Items[ItemKey];

    public async ValueTask<object> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ctx = context.HttpContext;
        string key = ctx.Request.Headers[Header].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) return Results.Unauthorized();

        var db = ctx.RequestServices.GetRequiredService<Database>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"UPDATE workstations SET last_seen_at = now() WHERE key_hash = @h AND active
              RETURNING workstation_id, name, is_admin", conn);
        cmd.Parameters.AddWithValue("h", Database.Sha256Hex(key));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return Results.Unauthorized();
        ctx.Items[ItemKey] = new WorkstationIdentity(r.GetGuid(0), r.GetString(1), r.GetBoolean(2));
        return await next(context);
    }
}

/// <summary>
/// Admin endpoints: an ADMIN workstation key AND an identified, active admin user (X-BBTech-Actor) whose
/// rights come from the database. A client saying "I am admin" is not enough.
/// </summary>
public sealed class AdminAuth : IEndpointFilter
{
    public const string ActorHeader = "X-BBTech-Actor";
    private const string ItemKey = "bbtech.actor";

    public static ServerUser Actor(HttpContext ctx) => (ServerUser)ctx.Items[ItemKey];

    public async ValueTask<object> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ctx = context.HttpContext;
        var ws = WorkstationAuth.Current(ctx);
        if (ws == null || !ws.IsAdmin) return Results.StatusCode(403);

        var db = ctx.RequestServices.GetRequiredService<Database>();
        await using var conn = await db.OpenAsync();
        var actor = await ServerUser.LoadAsync(conn, ctx.Request.Headers[ActorHeader].ToString());
        if (actor == null || !actor.Active || !actor.IsAdmin)
        {
            await ServerAudit.WriteAsync(conn, null, ctx.Request.Headers[ActorHeader].ToString(), "unknown", "PERMISSION_DENIED",
                ctx.Request.Path, "", "", "admin endpoint refused", ws.Id);
            return Results.StatusCode(403);
        }
        ctx.Items[ItemKey] = actor;
        return await next(context);
    }
}
