using BBTech.Api;
using BBTech.Api.Data;
using Microsoft.AspNetCore.Http.Json;
using Npgsql;

// ---------------------------------------------------------------------------------------------
// Command-line tools (run on the server host, never exposed over HTTP):
//   BBTech.Api migrate
//   BBTech.Api create-user <userId> "<Name>" "<Role>"            (bootstrap the first Super Admin)
//   BBTech.Api create-workstation "<name>" [--admin]             (prints the API key ONCE)
// Without arguments the API runs.
// ---------------------------------------------------------------------------------------------
if (args.Length > 0 && args[0] is "migrate" or "create-user" or "create-workstation" or "assign-operators")
{
    var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
    var db = new Database(config.GetConnectionString("Postgres"));
    var applied = await db.MigrateAsync();
    Console.WriteLine(applied.Count == 0 ? "Database is up to date." : "Applied: " + string.Join(", ", applied));
    if (args[0] == "create-user" && args.Length >= 4)
    {
        await using var conn = await db.OpenAsync();
        await AdminOps.UpsertUserAsync(conn, new UserUpsertRequest { UserId = args[1], Name = args[2], Role = args[3] }, "cli");
        Console.WriteLine("User " + args[1] + " created/updated.");
    }
    else if (args[0] == "assign-operators" && args.Length >= 3 && Guid.TryParse(args[1], out var wsId))
    {
        // assign-operators <workstationId> <userId> [<userId> ...]: who may submit records (and QC decisions) with this USB key.
        await using var conn = await db.OpenAsync();
        foreach (var u in args.Skip(2))
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO workstation_operators (workstation_id, user_id) VALUES (@w, @u) ON CONFLICT DO NOTHING", conn);
            cmd.Parameters.AddWithValue("w", wsId);
            cmd.Parameters.AddWithValue("u", u);
            await cmd.ExecuteNonQueryAsync();
        }
        Console.WriteLine("Assigned " + (args.Length - 2) + " operator(s) to workstation " + wsId + ".");
    }
    else if (args[0] == "create-workstation" && args.Length >= 2)
    {
        var (id, key) = await db.CreateWorkstationAsync(args[1], args.Contains("--admin"));
        Console.WriteLine("Workstation " + id + " created. API key (shown only now, store it safely):");
        Console.WriteLine(key);
    }
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.AddSingleton(sp => new Database(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddSingleton<SyncProcessor>();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 20 * 1024 * 1024);

var app = builder.Build();
await app.Services.GetRequiredService<Database>().MigrateAsync();

if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (ctx, next) =>
{
    // Plain HTTP only from this machine (development / a TLS-terminating reverse proxy on the same host).
    // (A null remote address only occurs for the in-process test host.)
    var remote = ctx.Connection.RemoteIpAddress;
    if (!ctx.Request.IsHttps && remote != null && !System.Net.IPAddress.IsLoopback(remote)
        && !string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("HTTPS required");
        return;
    }
    await next();
});

app.MapGet("/health", async (Database db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT 1", conn);
    await cmd.ExecuteScalarAsync();
    return Results.Ok(new { status = "ok" });
});

var v1 = app.MapGroup("/v1").AddEndpointFilter<WorkstationAuth>();

v1.MapGet("/config/master", async (Database db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(await MasterData.BuildAsync(conn));
});

v1.MapGet("/users", async (Database db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(await MasterData.OperatorsAsync(conn));
});

v1.MapPost("/sync/upload", async (HttpContext ctx, SyncUploadBatchDto batch, SyncProcessor sync) =>
{
    var ws = WorkstationAuth.Current(ctx);
    if (ctx.Request.Headers.TryGetValue("X-Idempotency-Key", out var key) && batch != null && key != batch.BatchId)
        return Results.BadRequest(new SyncUploadResultDto { Success = false, ErrorMessage = "Idempotency key does not match the batch id." });
    var result = await sync.ProcessAsync(batch, ws.Id);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

v1.MapGet("/devices/{deviceId}/status", async (string deviceId, Database db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(await Devices.StatusAsync(conn, deviceId));
});

v1.MapGet("/devices/{deviceId}/results", async (string deviceId, Database db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(await Devices.LatestResultsAsync(conn, deviceId));
});

v1.MapPost("/devices", async (SyncQueueItemDto device, Database db) =>
{
    if (string.IsNullOrWhiteSpace(device?.DeviceId) || device.DeviceId.Length > 200) return Results.BadRequest("DeviceId required.");
    await using var conn = await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        @"INSERT INTO devices (device_id, serial_number, manufacturer, model) VALUES (@d, @s, @m, @mo)
          ON CONFLICT (device_id) DO UPDATE SET last_seen_at = now()", conn);
    cmd.Parameters.AddWithValue("d", device.DeviceId);
    cmd.Parameters.AddWithValue("s", device.DeviceSerial ?? "");
    cmd.Parameters.AddWithValue("m", device.DeviceManufacturer ?? "");
    cmd.Parameters.AddWithValue("mo", device.DeviceModel ?? "");
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok(await Devices.StatusAsync(conn, device.DeviceId));
});

// Full history of one laptop (timeline; also used by the tool to restore a history wiped by a Windows install).
v1.MapGet("/devices/{deviceId}/history", async (string deviceId, Database db) =>
{
    await using var conn = await db.OpenAsync();
    var rows = await ServerLifecycle.HistoryAsync(conn, deviceId, null, withEvidence: true);
    return Results.Ok(rows.Select(r => new DeviceHistoryDto
    {
        RecordId = r.TestResultId, DeviceId = deviceId, OperatorId = r.OperatorId, OperatorName = r.OperatorName, Department = r.Department,
        TestId = r.TestId, TestName = r.TestName, Status = r.Status, Notes = r.Comment, Details = r.Details, TestedAt = r.StartedAt ?? default,
    }));
});

// Lifecycle timeline in plain words (who did what, when, result, why returned).
v1.MapGet("/devices/{deviceId}/timeline", async (string deviceId, Database db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(BBTech.Core.Testing.LaptopLifecycle.Timeline(await ServerLifecycle.HistoryAsync(conn, deviceId)));
});

// Latest technical snapshot (identity, inventory, raw measured health with sources).
v1.MapGet("/devices/{deviceId}/snapshot", async (string deviceId, Database db) =>
{
    await using var conn = await db.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT inventory::text FROM device_snapshots WHERE device_id = @d ORDER BY captured_at DESC LIMIT 1", conn);
    cmd.Parameters.AddWithValue("d", deviceId);
    var json = (string)await cmd.ExecuteScalarAsync();
    return json == null ? Results.NotFound() : Results.Content(json, "application/json");
});

// ITMS: laptops whose LATEST final QC decision is PASS.
v1.MapGet("/devices/ready", async (Database db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(await Devices.ReadyAsync(conn));
});

// ---- administration (admin workstation key + an identified admin user in X-BBTech-Actor)
var admin = v1.MapGroup("/admin").AddEndpointFilter<AdminAuth>();

admin.MapPost("/users", async (HttpContext ctx, UserUpsertRequest req, Database db) =>
{
    await using var conn = await db.OpenAsync();
    string error = await AdminOps.UpsertUserAsync(conn, req, AdminAuth.Actor(ctx).UserId);
    return error == null ? Results.Ok(await MasterData.OperatorsAsync(conn)) : Results.BadRequest(error);
});

admin.MapPost("/packages", async (HttpContext ctx, SoftwarePackageDto pkg, Database db) =>
{
    string error = MasterData.ValidatePackage(pkg);
    if (error != null) return Results.BadRequest(error);
    await using var conn = await db.OpenAsync();
    await AdminOps.UpsertPackageAsync(conn, pkg, AdminAuth.Actor(ctx).UserId);
    return Results.Ok(await MasterData.BuildAsync(conn));
});

admin.MapPut("/test-definitions", async (HttpContext ctx, List<TestRequirementDto> tests, Database db) =>
{
    await using var conn = await db.OpenAsync();
    string error = await AdminOps.ReplaceTestDefinitionsAsync(conn, tests, AdminAuth.Actor(ctx).UserId);
    return error == null ? Results.Ok(await MasterData.BuildAsync(conn)) : Results.BadRequest(error);
});

admin.MapPost("/laptop-models", async (HttpContext ctx, LaptopModelDto model, Database db) =>
{
    await using var conn = await db.OpenAsync();
    string error = await AdminOps.UpsertLaptopModelAsync(conn, model, AdminAuth.Actor(ctx).UserId);
    return error == null ? Results.Ok() : Results.BadRequest(error);
});

admin.MapPost("/workstations", async (HttpContext ctx, WorkstationRequest req, Database db) =>
{
    if (string.IsNullOrWhiteSpace(req?.Name)) return Results.BadRequest("Name required.");
    var (id, key) = await db.CreateWorkstationAsync(req.Name.Trim(), req.IsAdmin);
    await using var conn = await db.OpenAsync();
    await ServerAudit.WriteAsync(conn, null, AdminAuth.Actor(ctx).UserId, AdminAuth.Actor(ctx).Name, "WORKSTATION_CREATED", req.Name, "", id.ToString(), req.IsAdmin ? "admin" : "workstation", WorkstationAuth.Current(ctx).Id);
    return Results.Ok(new { WorkstationId = id, ApiKey = key });
});

// Restrict which operators may submit records from a workstation key (empty list = no restriction).
admin.MapPut("/workstations/{workstationId:guid}/operators", async (HttpContext ctx, Guid workstationId, List<string> userIds, Database db) =>
{
    await using var conn = await db.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();
    await using (var del = new NpgsqlCommand("DELETE FROM workstation_operators WHERE workstation_id = @w", conn, tx))
    {
        del.Parameters.AddWithValue("w", workstationId);
        await del.ExecuteNonQueryAsync();
    }
    foreach (var u in (userIds ?? new()).Distinct())
    {
        await using var ins = new NpgsqlCommand(
            @"INSERT INTO workstation_operators (workstation_id, user_id)
              SELECT @w, user_id FROM users WHERE user_id = @u AND deleted_at IS NULL
              AND EXISTS (SELECT 1 FROM workstations WHERE workstation_id = @w)", conn, tx);
        ins.Parameters.AddWithValue("w", workstationId);
        ins.Parameters.AddWithValue("u", u);
        if (await ins.ExecuteNonQueryAsync() == 0) return Results.BadRequest("Unknown workstation or user: " + u);
    }
    var actor = AdminAuth.Actor(ctx);
    await ServerAudit.WriteAsync(conn, tx, actor.UserId, actor.Name, "PERMISSION_CHANGED", "workstation:" + workstationId, "",
        string.Join(", ", userIds ?? new()), "workstation operator allow-list", WorkstationAuth.Current(ctx).Id);
    await tx.CommitAsync();
    return Results.Ok();
});

admin.MapDelete("/results/{recordId}", async (HttpContext ctx, string recordId, Database db) =>
{
    await using var conn = await db.OpenAsync();
    string error = await AdminOps.TombstoneResultAsync(conn, recordId, AdminAuth.Actor(ctx));
    return error == null ? Results.Ok() : Results.BadRequest(error);
});

// Management report: where laptops are, time per stage, rework, QC failure reasons, work per operator.
admin.MapGet("/reports/lifecycle", async (Database db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(await ServerLifecycle.ReportAsync(conn));
});

admin.MapGet("/audit", async (int? limit, Database db) =>
{
    await using var conn = await db.OpenAsync();
    return Results.Ok(await AdminOps.AuditAsync(conn, Math.Clamp(limit ?? 200, 1, 2000)));
});

app.Run();
return 0;

public partial class Program { }
