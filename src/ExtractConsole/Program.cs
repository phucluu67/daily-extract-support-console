using ExtractConsole.Config;
using ExtractConsole.Models;
using ExtractConsole.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

// Models.ActionResult collides with Mvc.ActionResult, which [FromBody] pulls into scope.
using ActionResult = ExtractConsole.Models.ActionResult;

// ---------------------------------------------------------------------------
//  Command line
// ---------------------------------------------------------------------------

var argSet = args.Select(a => a.Trim()).ToArray();
bool Has(string flag) => argSet.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
string? Val(string flag)
{
    var i = Array.FindIndex(argSet, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < argSet.Length ? argSet[i + 1] : null;
}

if (Has("--help") || Has("-h") || Has("/?"))
{
    Console.WriteLine("""
        ExtractConsole - web console for the CLARITY -> STRATAJAZZ daily extract

          ExtractConsole.exe                  loopback only, opens your browser  (default)
          ExtractConsole.exe --port 5173      pin the port instead of picking a free one
          ExtractConsole.exe --no-browser     start without launching a browser
          ExtractConsole.exe --config PATH    use a different settings file
          ExtractConsole.exe --serve          bind all interfaces, READ-ONLY, for a shared URL
          ExtractConsole.exe --serve --allow-actions
                                              shared URL WITH start/stop - see docs/DEPLOYMENT.md
                                              first: every visitor acts as the launching account

        Unattended check - no web server, no browser. Run by the Agent job
        "DBA - CLARITY Extract Checker"; see docs/RUNBOOK.md section 8.

          ExtractConsole.exe --check          check every enabled environment once and exit
                      --env DEV               just this one
                      --dry-run               report to the console; mail nothing, record nothing
                      --force                 report again on a run already reported
                      --fail-on-critical      exit 2 on a Critical finding instead of 0

        Exit codes:  0 ran (findings, if any, were mailed)   2 --fail-on-critical fired
                     3 could NOT check - unreachable, unknown --env, or Enabled:false

        Config lives in appsettings.json beside the exe.
        """);
    return 0;
}

var serve = Has("--serve");
var allowActions = !serve || Has("--allow-actions");
var openBrowser = !Has("--no-browser") && !serve;
var port = int.TryParse(Val("--port"), out var p) ? p : FreePort();

// ---------------------------------------------------------------------------
//  Config
// ---------------------------------------------------------------------------

var exeDir = AppContext.BaseDirectory;

// Anchor the content root to the exe, never to the caller's working directory.
// CreateSlimBuilder otherwise takes Directory.GetCurrentDirectory() and opens a file
// provider on it at once - and this is launched three different ways with three
// different working directories: double-clicked, run by the test harness, and started
// by a SQL Agent job step. The Agent case is the one that bites: the proxy account's
// cwd was C:\Windows\TEMP, which it may write but may not enumerate, so startup died
// with "Error reading the C:\Windows\TEMP\ directory" long before any of our own code
// ran. Nothing here ever serves files from the content root - wwwroot is embedded - so
// pinning it to the exe's own folder costs nothing and removes the dependency.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDir
});
builder.Logging.ClearProviders();
if (serve) builder.Logging.AddConsole();
var configPath = Val("--config") is { } cp ? Path.GetFullPath(cp) : Path.Combine(exeDir, "appsettings.json");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Settings file not found: {configPath}");
    return 1;
}

// CreateSlimBuilder has already registered appsettings.json. Leaving it in place would
// LAYER the chosen file on top of it rather than replace it, and .NET merges
// configuration arrays by index - so an environment in the chosen file would silently
// inherit leftover ChildJobs, DisabledSteps and ReconTables from the default file,
// widening the action allowlist with jobs the operator never named. Drop every JSON
// source first so exactly one settings file is in play. Environment variables and
// command-line sources are left alone.
foreach (var jsonSource in builder.Configuration.Sources
             .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
             .ToList())
{
    builder.Configuration.Sources.Remove(jsonSource);
}

builder.Configuration
    .SetBasePath(Path.GetDirectoryName(configPath)!)
    .AddJsonFile(Path.GetFileName(configPath), optional: false, reloadOnChange: true);

var config = builder.Configuration.Get<AppConfig>() ?? new AppConfig();
if (config.Environments.Count == 0)
{
    Console.Error.WriteLine("appsettings.json defines no environments. Nothing to manage.");
    return 1;
}

// ---------------------------------------------------------------------------
//  --check : run headless, report, exit. No web server, no browser.
// ---------------------------------------------------------------------------
//  Deliberately ahead of the audit log: --check never starts or stops anything,
//  so it has nothing to audit, and it runs as a service account with no
//  %LOCALAPPDATA% to write one to.

if (Has("--check"))
{
    var dryRun = Has("--dry-run");
    var force = Has("--force");
    var wanted = Val("--env");

    List<EnvironmentConfig> targets;

    if (wanted is null)
    {
        targets = config.Environments.Where(e => e.Enabled).ToList();
    }
    else if (config.Find(wanted) is not { } named)
    {
        var known = string.Join(", ", config.Environments.Select(e => e.Name));
        Console.Error.WriteLine($"Unknown environment '{wanted}'. This config defines: {known}");
        return 3;
    }
    else
    {
        targets = [named];
    }

    // A scheduled checker that silently checks nothing is worse than one that fails:
    // the job goes green every tick while nobody is watching the extract. Name an
    // environment that is switched off and you get told, loudly.
    targets = targets.Where(t => t.Enabled).ToList();
    if (targets.Count == 0)
    {
        Console.Error.WriteLine(wanted is null
            ? "No enabled environments to check."
            : $"Environment '{wanted}' is present but Enabled=false, so nothing was checked.");
        return 3;
    }

    var worst = 0;

    foreach (var target in targets)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {target.Name} ===");

        try
        {
            var svc = new CheckService(config, target);
            var report = await svc.RunAsync(force, CancellationToken.None);
            report = await svc.RecordAndNotifyAsync(report, dryRun, CancellationToken.None);

            if (report.Findings.Count == 0)
            {
                Console.WriteLine("  Nothing to report.");
                continue;
            }

            Console.Write(CheckService.BuildMessage(report));

            if (report.AlreadyReported)
                Console.WriteLine("  (already reported for this run - not mailed again)");
            else if (dryRun)
                Console.WriteLine("  (dry run - nothing mailed, nothing recorded)");
            else if (report.Notified)
                Console.WriteLine($"  Mailed to {config.Check.Recipients}");
            else if (report.NotifyError is { } err)
                Console.WriteLine($"  MAIL FAILED: {err}");
            else if (string.IsNullOrWhiteSpace(config.Check.Recipients))
                Console.WriteLine("  (no recipients configured - console only)");

            if (report.Critical > 0) worst = Math.Max(worst, 2);
            else if (report.Warnings > 0) worst = Math.Max(worst, 1);
        }
        catch (Exception ex)
        {
            // The checker itself breaking is different from the checker finding a
            // problem, and the exit code keeps them apart.
            Console.Error.WriteLine($"  Checker failed on {target.Name}: {ex.Message}");
            return 3;
        }
    }

    Console.WriteLine();
    // Exit 0 even when findings exist: the mail is the alert, and a non-zero code here
    // would make the Agent job red for a data problem rather than a checker problem.
    // --fail-on-critical is there for anyone who wants the opposite.
    return Has("--fail-on-critical") && worst >= 2 ? 2 : 0;
}

var audit = new AuditLog(config.AuditLogPath);

// Who is using the console, and how much of it they get. In this per-PC version that is
// the account running the exe; hosted, it would be the signed-in visitor.
var access = AccessControl.Resolve(config.Access,
    System.Security.Principal.WindowsIdentity.GetCurrent(), config.Environments);

// Changing anything needs both: a console not started read-only, and a role allowed to.
var canChange = allowActions && access.CanChange;

// A per-launch token. The listener is on loopback, but any process on this machine could
// otherwise drive it; the token means only the browser handed the URL can.
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

builder.WebHost.ConfigureKestrel(k =>
{
    if (serve) k.Listen(IPAddress.Any, port);
    else k.Listen(IPAddress.Loopback, port);
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

var app = builder.Build();

// ---------------------------------------------------------------------------
//  Token gate
// ---------------------------------------------------------------------------

app.Use(async (ctx, next) =>
{
    // The shell page is reachable without a token so the browser can load it from the
    // launch URL; it then reads ?t= and sends the header on every API call.
    var path = ctx.Request.Path.Value ?? "/";
    var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    if (isApi)
    {
        var supplied = ctx.Request.Headers["X-Console-Token"].FirstOrDefault()
                       ?? ctx.Request.Query["t"].FirstOrDefault();

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(supplied ?? ""),
                System.Text.Encoding.UTF8.GetBytes(token)))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Bad or missing console token. Relaunch the app." });
            return;
        }

        // Role gate. Enforced here, on every call, so a tab the browser hides cannot be
        // reached by asking for its endpoint directly.
        if (!AccessControl.MayCall(access, ctx.Request.Method, path))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = access.Role == Role.None
                    ? $"{access.User} is not in any group allowed to use this console."
                    : $"Not available to the {AccessControl.Describe(access.Role)} role."
            });
            return;
        }
    }

    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

// Served from inside the assembly, not from a wwwroot folder next to the exe.
var webFiles = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });

// ---------------------------------------------------------------------------
//  Helpers
// ---------------------------------------------------------------------------

EnvironmentConfig Env(string? name)
{
    // Every endpoint resolves its environment here, so this is where a role limited to
    // PROD is held to PROD - whatever the request asks for.
    var allowed = config.Environments.Where(e => e.Enabled && access.MaySeeEnvironment(e.Name)).ToList();
    if (allowed.Count == 0)
        throw new UnauthorizedAccessException("No environment is available to your role.");

    var env = string.IsNullOrWhiteSpace(name)
        ? allowed.FirstOrDefault(e => e.IsDefault) ?? allowed[0]
        : config.Find(name) ?? throw new KeyNotFoundException($"Unknown environment '{name}'.");

    if (!env.Enabled)
        throw new InvalidOperationException(
            $"Environment '{env.Name}' is a placeholder - fill in its servers in appsettings.json and set Enabled: true.");

    if (!access.MaySeeEnvironment(env.Name))
        throw new UnauthorizedAccessException(
            $"{env.Name} is not available to the {AccessControl.Describe(access.Role)} role.");

    return env;
}

/// <summary>Runs a panel query so one failing panel degrades to a message instead of a blank dashboard.</summary>
static async Task<IResult> Panelled<T>(Func<Task<T>> work)
{
    try
    {
        return Results.Ok(Panel<T>.Ok(await work()));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Ok(Panel<T>.Fail(Describe(ex)));
    }
}

static string Describe(Exception ex) => ex switch
{
    Microsoft.Data.SqlClient.SqlException { Number: 18456 } =>
        "SQL Server refused the login for your Windows account.",
    Microsoft.Data.SqlClient.SqlException { Number: 229 or 300 or 262 } s =>
        $"Your account lacks permission for this query ({s.Message.Split('.')[0]}).",
    Microsoft.Data.SqlClient.SqlException { Number: -2 } =>
        "The query timed out. The instance may be busy, or the extract is mid-run.",
    Microsoft.Data.SqlClient.SqlException s when s.Number is 53 or 40615 or 10061 =>
        $"Could not reach the instance. {s.Message}",
    Microsoft.Data.SqlClient.SqlException s => s.Message,
    UnauthorizedAccessException => ex.Message,
    DirectoryNotFoundException => ex.Message,
    _ => ex.Message
};

// ---------------------------------------------------------------------------
//  API
// ---------------------------------------------------------------------------

app.MapGet("/api/bootstrap", () => Results.Ok(new
{
    // The page builds itself from this: which environments, which tabs, whether to offer
    // any action at all. It is a convenience for the UI - every endpoint re-checks.
    access = new
    {
        role = AccessControl.Describe(access.Role),
        roleKey = access.Role.ToString().ToLowerInvariant(),
        user = access.User,
        grantedBy = access.GrantedBy,
        tabs = access.Tabs,
        canChange,
        statusPanel = access.Role is Role.Operator or Role.Viewer
    },
    environments = config.Environments.Where(e => access.MaySeeEnvironment(e.Name)).Select(e => new EnvironmentInfo(
        e.Name, e.Enabled, e.IsDefault, e.IsProduction,
        e.ControlServer, e.DataServer, e.FileShare, e.ArchiveShare, e.JobName,
        $"{e.AuditDatabase}.dbo.DailyReconCurrentProcessLog on {e.DataServer}",
        $"{e.StagingDatabase}.dbo.etl_audit_log on {e.DataServer}")),
    pollSeconds = Math.Max(5, config.PollSeconds),
    actionsEnabled = canChange,
    mode = serve ? "shared" : "local",
    user = $"{Environment.UserDomainName}\\{Environment.UserName}",
    machine = Environment.MachineName,
    auditLogPath = audit.FilePath,
    diagnosticsLocation = config.Environments.FirstOrDefault(e => e.Enabled) is { } de
        ? $"{de.DiagnosticsDatabase}.diag.FailureRule on {de.ControlServer}"
        // No database named here: the caption follows configuration, so relocating the
        // catalogue never leaves a stale name on screen.
        : "diag.FailureRule",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"
}));

app.MapGet("/api/status", ([FromQuery] string? env, CancellationToken ct) =>
    Panelled(() => new JobService(Env(env)).GetStatusAsync(ct)));

app.MapGet("/api/history", ([FromQuery] string? env, [FromQuery] int? top, CancellationToken ct) =>
    Panelled(() => new JobService(Env(env)).GetHistoryAsync(top ?? 40, ct)));

app.MapGet("/api/run/{instanceId:long}", (long instanceId, [FromQuery] string? env, CancellationToken ct) =>
    Panelled(() => new JobService(Env(env)).GetRunStepsAsync(instanceId, ct)));

app.MapGet("/api/ssis", ([FromQuery] string? env, [FromQuery] long? runId, CancellationToken ct) =>
    Panelled(async () =>
    {
        var e = Env(env);
        var window = await new JobService(e).GetRunWindowAsync(runId, ct);
        return await new SsisService(e).GetRunReportAsync(window, ct);
    }));

app.MapGet("/api/ssis/{executionId:long}/messages", (long executionId, [FromQuery] string? env, CancellationToken ct) =>
    Panelled(() => new SsisService(Env(env)).GetMessagesAsync(executionId, ct)));

app.MapGet("/api/recon", ([FromQuery] string? env, [FromQuery] string? date,
                          [FromQuery] bool? exact, [FromQuery] bool? records, CancellationToken ct) =>
    Panelled(() => new ReconService(Env(env))
        .GetCountsAsync(ParseDate(date), exact ?? false, records ?? false, ct)));

app.MapGet("/api/files", ([FromQuery] string? env, [FromQuery] string? date) =>
    Panelled(() => Task.FromResult(new FileService(Env(env)).List(ParseDate(date)))));

// No date defaults to the newest day the log holds, not to today: DEV runs ad hoc, so
// "today" is usually empty there and an empty panel reads like a fault.
app.MapGet("/api/upserts", ([FromQuery] string? env, [FromQuery] string? date, CancellationToken ct) =>
    Panelled(() => new UpsertLogService(Env(env)).GetAsync(ParseDateOrNull(date), ct)));

// No tab reads this any more - the Action log was dropped because a per-machine file
// cannot answer "who restarted it?" across a team, which the run history's Invoked by
// column does properly. The file is still written, and still worth being able to read.
app.MapGet("/api/audit", () => Results.Ok(Panel<List<string>>.Ok(audit.Tail(100).ToList())));

app.MapGet("/api/diagnostics", ([FromQuery] string? env, [FromQuery] int? days, CancellationToken ct) =>
    Panelled(() => new DiagnosticsService(Env(env)).GetReportAsync(days ?? 90, ct)));

app.MapPost("/api/diagnostics/rules/{ruleId:int}/solutions",
    async (int ruleId, [FromBody] NewFailureRule req, CancellationToken ct) =>
{
    if (!canChange) return Forbidden();
    var env = Env(req.Environment);
    try
    {
        var result = await new DiagnosticsService(env).AddSolutionsToRuleAsync(ruleId, req.Solutions, ct);
        audit.Write(env.Name, "ADD-SOLUTION", $"rule {ruleId}", result.Message, result.Success);
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Ok(new ActionResult(false, Describe(ex), null));
    }
});

app.MapPost("/api/diagnostics/rules", async ([FromBody] NewFailureRule req, CancellationToken ct) =>
{
    // Writing to the shared catalogue is an action, so it follows the same read-only rule.
    if (!canChange) return Forbidden();

    var env = Env(req.Environment);
    try
    {
        var result = await new DiagnosticsService(env).AddRuleAsync(req, ct);
        audit.Write(env.Name, "ADD-RULE", req.Title ?? "(untitled)", result.Message, result.Success);
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Ok(new ActionResult(false, Describe(ex), null));
    }
});

app.MapPost("/api/actions/start", async ([FromBody] ActionRequest req, CancellationToken ct) =>
{
    if (!canChange) return Forbidden();
    var env = Env(req.Environment);

    // Enforced here, not only in the browser: on production the caller must name the
    // environment. DEV and PROD differ by one character in the host name.
    if (env.IsProduction && !string.Equals(req.Confirm, env.Name, StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new ActionResult(
            false, $"Not confirmed - starting a job on {env.Name} requires echoing the environment name.", null));

    try
    {
        var result = await new JobService(env).StartAsync(req.JobName, req.FromStepId, ct);
        audit.Write(env.Name, req.FromStepId is null ? "START" : $"START@step{req.FromStepId}",
                    req.JobName ?? env.JobName, result.Message, result.Success);
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        audit.Write(env.Name, "START", req.JobName ?? env.JobName, Describe(ex), false);
        return Results.Ok(new ActionResult(false, Describe(ex), null));
    }
});

app.MapPost("/api/actions/stop", async ([FromBody] ActionRequest req, CancellationToken ct) =>
{
    if (!canChange) return Forbidden();

    // Stopping mid-extract can leave partial data, so the client must echo the job name.
    var env = Env(req.Environment);
    var target = req.JobName ?? env.JobName;
    if (!string.Equals(req.Confirm, target, StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new ActionResult(false, "Stop not confirmed - the typed job name did not match.", null));

    try
    {
        var result = await new JobService(env).StopAsync(req.JobName, ct);
        audit.Write(env.Name, "STOP", target, result.Message, result.Success);
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        audit.Write(env.Name, "STOP", target, Describe(ex), false);
        return Results.Ok(new ActionResult(false, Describe(ex), null));
    }
});

// ---- extract volume ------------------------------------------------------------

// Defaults to the last 90 days. Both ends inclusive.
app.MapGet("/api/volumes", ([FromQuery] string? env, [FromQuery] string? from, [FromQuery] string? to,
                            CancellationToken ct) =>
    Panelled(() => new VolumeService(Env(env)).GetAsync(
        ParseDateOrNull(from) ?? DateTime.Today.AddDays(-89),
        ParseDateOrNull(to) ?? DateTime.Today, ct)));

// ---- AdHoc HAR lists --------------------------------------------------------

app.MapGet("/api/adhoc", ([FromQuery] string? env) =>
    Panelled(() => Task.FromResult(new AdhocListService(Env(env)).ListAll())));

app.MapGet("/api/adhoc/{key}", (string key, [FromQuery] string? env, [FromQuery] string? q) =>
    Panelled(() => Task.FromResult(new AdhocListService(Env(env)).Detail(key, q))));

app.MapPost("/api/adhoc/{key}/append", (string key, [FromBody] AdhocChangeRequest req, CancellationToken ct) =>
    AdhocChange(key, req, append: true, ct));

app.MapPost("/api/adhoc/{key}/remove", (string key, [FromBody] AdhocChangeRequest req, CancellationToken ct) =>
    AdhocChange(key, req, append: false, ct));

/// <summary>
/// Preview (Commit false) is read-only and always allowed. A commit has to clear the same
/// gates as starting a job - plus one of its own: not while the extract is running.
/// </summary>
async Task<IResult> AdhocChange(string key, AdhocChangeRequest req, bool append, CancellationToken ct)
{
    var action = append ? "ADHOC-APPEND" : "ADHOC-REMOVE";
    EnvironmentConfig env;
    try { env = Env(req.Environment); }
    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
    {
        return Results.Ok(AdhocRefused(append, ex.Message));
    }

    if (req.Commit)
    {
        if (!canChange)
            return Results.Ok(AdhocRefused(append, ReadOnlyReason()));

        if (env.IsProduction && !string.Equals(req.Confirm, env.Name, StringComparison.OrdinalIgnoreCase))
            return Results.Ok(AdhocRefused(append,
                $"Not confirmed - changing an AdHoc list on {env.Name} requires echoing the environment name."));

        // The lists are read by the staging packages early in a run and replaced by step 14
        // near its end, so an entry written mid-run is either never used or wiped. Append
        // between runs - after step 14 has done its part - which is the team's workflow.
        try
        {
            var status = await new JobService(env).GetStatusAsync(ct);
            if (status.IsRunning)
                return Results.Ok(AdhocRefused(append,
                    $"The extract is running on {env.Name} (started {status.StartedLocal:g}). Change the list after it "
                    + "finishes - anything written now is either read too late or replaced by step 14."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If we cannot tell whether it is running, do not risk writing into a run.
            return Results.Ok(AdhocRefused(append,
                $"Could not confirm the extract is idle, so nothing was written. {Describe(ex)}"));
        }
    }

    try
    {
        var svc = new AdhocListService(env);
        var result = append ? svc.Append(key, req) : svc.Remove(key, req);

        if (req.Commit)
        {
            audit.Write(env.Name, action, key,
                result.Committed
                    ? $"{result.ChangeCount} entries; {result.EntriesBefore} -> {result.EntriesAfter}"
                    : result.Message,
                result.Committed);
        }
        return Results.Ok(result);
    }
    catch (IOException ex) when (req.Commit)
    {
        // Most often the file is open in a staging package or an editor.
        audit.Write(env.Name, action, key, ex.Message, false);
        return Results.Ok(AdhocRefused(append, $"Could not write the file - it may be open elsewhere. {ex.Message}"));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        if (req.Commit) audit.Write(env.Name, action, key, Describe(ex), false);
        return Results.Ok(AdhocRefused(append, Describe(ex)));
    }
}

static AdhocChangeResult AdhocRefused(bool append, string message) =>
    new(false, message, append ? "append" : "remove", false, 0, [], 0, 0, 0, null, [], 0, 0);

IResult Forbidden() => Results.Ok(new ActionResult(false, ReadOnlyReason(), null));

string ReadOnlyReason() => !access.CanChange
    ? $"Your account has the {AccessControl.Describe(access.Role)} role, which can view but not change anything."
    : "This console was started read-only (--serve without --allow-actions).";

static DateTime ParseDate(string? s) =>
    DateTime.TryParse(s, out var d) ? d.Date : DateTime.Today;

/// <summary>
/// As <see cref="ParseDate"/> but keeps "no date given" distinct from "today". The
/// upsert log needs that distinction: it defaults to the newest day it holds, and on
/// DEV - which runs ad hoc - today is usually not that day.
/// </summary>
static DateTime? ParseDateOrNull(string? s) =>
    DateTime.TryParse(s, out var d) ? d.Date : null;

// ---------------------------------------------------------------------------
//  Run
// ---------------------------------------------------------------------------

var url = $"http://{(serve ? Environment.MachineName.ToLowerInvariant() : "127.0.0.1")}:{port}/?t={token}";

Console.WriteLine();
Console.WriteLine("  CLARITY -> STRATAJAZZ extract console");
Console.WriteLine($"  {url}");
Console.WriteLine();
if (serve)
{
    Console.WriteLine(allowActions
        ? "  SHARED MODE WITH ACTIONS. Every visitor with the URL acts as "
          + $"{Environment.UserDomainName}\\{Environment.UserName} against SQL Server."
        : "  Shared mode, read-only. Add --allow-actions to enable start/stop.");
    Console.WriteLine();
}
Console.WriteLine(access.Role == Role.None
    ? $"  {access.User} is not in any group allowed to use this console."
    : $"  {access.User} - {AccessControl.Describe(access.Role)} role ({access.GrantedBy})"
      + (access.Role == Role.Operator ? "" : $", {string.Join("/", access.Environments)} only"));
Console.WriteLine();
Console.WriteLine("  Leave this window open. Ctrl+C to quit.");
Console.WriteLine();

if (openBrowser)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch (Exception ex) { Console.WriteLine($"  (could not open a browser: {ex.Message})"); }
}

app.Run();
return 0;

static int FreePort()
{
    using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}
