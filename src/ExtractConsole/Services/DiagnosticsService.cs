using ExtractConsole.Config;
using ExtractConsole.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace ExtractConsole.Services;

/// <summary>
/// The shared failure catalogue: maps a step's error text to what it means and what to
/// do about it.
/// <para>
/// Rules live in SQL rather than a file beside the exe so the whole team reads one
/// catalogue and a rule added today survives the next rebuild. Matching happens here in
/// the app against messages already fetched, so showing a diagnosis costs one small
/// query for the rule list and nothing per step.
/// </para>
/// </summary>
public sealed class DiagnosticsService(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;
    private readonly Db _db = new(env);

    /// <summary>Rules are stable; re-reading them on every poll would be wasteful.</summary>
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly Dictionary<string, (DateTime LoadedUtc, List<FailureRule> Rules)> Cache = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    public async Task<List<FailureRule>> GetRulesAsync(bool forceReload, CancellationToken ct)
    {
        var key = $"{_env.Name}|{_env.EffectiveDiagnosticsServer}|{_env.DiagnosticsDatabase}";

        if (!forceReload)
        {
            await CacheGate.WaitAsync(ct);
            try
            {
                if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.LoadedUtc < CacheFor)
                    return hit.Rules;
            }
            finally { CacheGate.Release(); }
        }

        var rules = await LoadRulesAsync(ct);

        await CacheGate.WaitAsync(ct);
        try { Cache[key] = (DateTime.UtcNow, rules); }
        finally { CacheGate.Release(); }

        return rules;
    }

    private async Task<List<FailureRule>> LoadRulesAsync(CancellationToken ct)
    {
        await using var cn = _db.Diagnostics();
        await cn.OpenAsync(ct);

        // Both sets in one round trip; the solutions are stitched on in memory.
        const string sql = """
            SELECT RuleId, IsEnabled, Pattern, MatchType, Title, Cause,
                   Priority, AddedBy, AddedUtc
            FROM diag.FailureRule
            ORDER BY Priority, RuleId;

            SELECT SolutionId, RuleId, Ordinal, Solution, Detail, IsVerified, AddedBy
            FROM diag.FailureSolution
            ORDER BY RuleId, Ordinal, SolutionId;
            """;

        await using var cmd = _db.Cmd(cn, sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var list = new List<FailureRule>();
        var byId = new Dictionary<int, List<FailureSolution>>();

        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            var solutions = new List<FailureSolution>();
            byId[id] = solutions;

            list.Add(new FailureRule(
                id, r.GetBoolean(1), r.GetString(2), r.GetString(3),
                r.GetString(4), Db.Str(r, 5), r.GetInt32(6),
                Db.Str(r, 7), Db.Dt(r, 8), solutions));
        }

        if (await r.NextResultAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (!byId.TryGetValue(r.GetInt32(1), out var target)) continue;
                target.Add(new FailureSolution(
                    r.GetInt32(0), r.GetInt32(2), r.GetString(3),
                    Db.Str(r, 4), r.GetBoolean(5), Db.Str(r, 6)));
            }
        }

        return list;
    }

    /// <summary>
    /// The best-matching rule for a message, or null. Lowest Priority wins, which is how
    /// a specific rule beats the SSIS catch-all.
    /// </summary>
    public static FailureRule? Match(IReadOnlyList<FailureRule> rules, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        foreach (var rule in rules.Where(x => x.IsEnabled).OrderBy(x => x.Priority).ThenBy(x => x.RuleId))
        {
            if (Matches(rule, message)) return rule;
        }
        return null;
    }

    private static bool Matches(FailureRule rule, string message)
    {
        if (string.Equals(rule.MatchType, "Regex", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Regex.IsMatch(message, rule.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
            }
            // A bad or pathological pattern must not take the status panel down with it.
            catch (ArgumentException) { return false; }
            catch (RegexMatchTimeoutException) { return false; }
        }

        return message.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Validates a pattern before it is stored, so a bad rule cannot be saved.</summary>
    public static string? ValidatePattern(string pattern, string matchType)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Pattern cannot be empty.";
        if (pattern.Length > 450) return "Pattern is longer than 450 characters.";

        if (!string.Equals(matchType, "Regex", StringComparison.OrdinalIgnoreCase)) return null;

        try { _ = Regex.Match("", pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException ex) { return $"Not a valid regular expression: {ex.Message}"; }
        return null;
    }

    public async Task<ActionResult> AddRuleAsync(NewFailureRule req, CancellationToken ct)
    {
        var matchType = string.Equals(req.MatchType, "Regex", StringComparison.OrdinalIgnoreCase)
            ? "Regex" : "Contains";

        if (ValidatePattern(req.Pattern ?? "", matchType) is { } problem)
            return new ActionResult(false, problem, null);

        if (string.IsNullOrWhiteSpace(req.Title))
            return new ActionResult(false, "Title cannot be empty - it is what the operator reads first.", null);

        await using var cn = _db.Diagnostics();
        await cn.OpenAsync(ct);

        // One per line, trimmed, blanks dropped. Numbered 10, 20, 30 ... leaving gaps so
        // a step can be slipped between two later without renumbering.
        var solutions = (req.Solutions ?? "")
            .Split('\n')
            .Select(s => s.Trim().TrimStart('-', '*', '•').Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.Length > 400 ? s[..400] : s)
            .ToList();

        const string sql = """
            INSERT INTO diag.FailureRule (Pattern, MatchType, Title, Cause, Priority, AddedBy)
            OUTPUT INSERTED.RuleId
            VALUES (@pattern, @matchType, @title, @cause, @priority, @addedBy);
            """;

        var who = $"{Environment.UserDomainName}\\{Environment.UserName}";

        await using var cmd = _db.Cmd(cn, sql);
        cmd.Parameters.Add("@pattern", SqlDbType.NVarChar, 450).Value = req.Pattern!.Trim();
        cmd.Parameters.Add("@matchType", SqlDbType.VarChar, 10).Value = matchType;
        cmd.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = req.Title!.Trim();
        cmd.Parameters.Add("@cause", SqlDbType.NVarChar, 2000).Value = (object?)req.Cause?.Trim() ?? DBNull.Value;
        cmd.Parameters.Add("@priority", SqlDbType.Int).Value = req.Priority is > 0 ? req.Priority : 100;
        cmd.Parameters.Add("@addedBy", SqlDbType.NVarChar, 128).Value = who;

        try
        {
            var id = await cmd.ExecuteScalarAsync(ct);
            if (id is not null and not DBNull && solutions.Count > 0)
                await AddSolutionsAsync(cn, Convert.ToInt32(id), solutions, who, startAt: 10, ct);

            Cache.Clear();      // the new rule must apply on the next refresh
            return new ActionResult(true,
                $"Added rule {id} with {solutions.Count} solution(s). It applies on the next refresh.",
                id?.ToString());
        }
        catch (SqlException ex) when (ex.Number is 229 or 230)
        {
            return new ActionResult(false,
                "Your account can read the catalogue but not add to it. INSERT on "
                + $"{_env.DiagnosticsDatabase}.diag.FailureRule is needed - see sql/20_install_diagnostics.sql.", null);
        }
    }

    private async Task AddSolutionsAsync(
        SqlConnection cn, int ruleId, IReadOnlyList<string> solutions, string who, int startAt, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO diag.FailureSolution (RuleId, Ordinal, Solution, AddedBy)
            VALUES (@ruleId, @ordinal, @solution, @addedBy);
            """;

        var ordinal = startAt;
        foreach (var s in solutions)
        {
            await using var cmd = _db.Cmd(cn, sql);
            cmd.Parameters.Add("@ruleId", SqlDbType.Int).Value = ruleId;
            cmd.Parameters.Add("@ordinal", SqlDbType.Int).Value = ordinal;
            cmd.Parameters.Add("@solution", SqlDbType.NVarChar, 400).Value = s;
            cmd.Parameters.Add("@addedBy", SqlDbType.NVarChar, 128).Value = who;
            await cmd.ExecuteNonQueryAsync(ct);
            ordinal += 10;
        }
    }

    /// <summary>Appends candidate fixes to a rule that already exists.</summary>
    public async Task<ActionResult> AddSolutionsToRuleAsync(int ruleId, string? text, CancellationToken ct)
    {
        var solutions = (text ?? "")
            .Split('\n')
            .Select(s => s.Trim().TrimStart('-', '*', '•').Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.Length > 400 ? s[..400] : s)
            .ToList();

        if (solutions.Count == 0)
            return new ActionResult(false, "Nothing to add - enter at least one line.", null);

        await using var cn = _db.Diagnostics();
        await cn.OpenAsync(ct);

        // Continue the existing numbering rather than colliding with it.
        await using var maxCmd = _db.Cmd(cn,
            "SELECT ISNULL(MAX(Ordinal), 0) FROM diag.FailureSolution WHERE RuleId = @ruleId;");
        maxCmd.Parameters.Add("@ruleId", SqlDbType.Int).Value = ruleId;
        var max = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct) ?? 0);

        try
        {
            await AddSolutionsAsync(cn, ruleId,
                solutions, $"{Environment.UserDomainName}\\{Environment.UserName}", max + 10, ct);
            Cache.Clear();
            return new ActionResult(true, $"Added {solutions.Count} solution(s) to rule {ruleId}.", null);
        }
        catch (SqlException ex) when (ex.Number is 229 or 230)
        {
            return new ActionResult(false,
                "Your account can read the catalogue but not add to it. INSERT on "
                + $"{_env.DiagnosticsDatabase}.diag.FailureSolution is needed.", null);
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            return new ActionResult(false, $"Rule {ruleId} does not exist.", null);
        }
    }

    /// <summary>
    /// Recent failures across the job and its child jobs, each paired with the rule that
    /// explains it - or nothing, which is the interesting case: an unexplained failure is
    /// a rule waiting to be written.
    /// </summary>
    public async Task<DiagnosticsReport> GetReportAsync(int days, CancellationToken ct)
    {
        var rules = await GetRulesAsync(forceReload: true, ct);

        var jobNames = _env.AllJobNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var paramNames = jobNames.Select((_, i) => $"@j{i}").ToList();

        var sql = $"""
            SET NOCOUNT ON;
            SELECT TOP (200)
                   j.name,
                   h.step_id,
                   h.step_name,
                   CONVERT(datetime,
                     STUFF(STUFF(CAST(h.run_date AS char(8)), 5, 0, '-'), 8, 0, '-') + ' ' +
                     STUFF(STUFF(RIGHT('000000' + CAST(h.run_time AS varchar(6)), 6), 5, 0, ':'), 3, 0, ':')),
                   h.message
            FROM msdb.dbo.sysjobhistory AS h
            JOIN msdb.dbo.sysjobs AS j ON j.job_id = h.job_id
            WHERE h.run_status = 0
              AND h.step_id > 0
              AND j.name IN ({string.Join(", ", paramNames)})
              AND h.run_date >= CONVERT(int, CONVERT(char(8), DATEADD(day, -@days, GETDATE()), 112))
            ORDER BY h.instance_id DESC;
            """;

        await using var cn = _db.Control();
        await cn.OpenAsync(ct);
        await using var cmd = _db.Cmd(cn, sql);
        for (var i = 0; i < jobNames.Count; i++)
            cmd.Parameters.Add(paramNames[i], SqlDbType.NVarChar, 128).Value = jobNames[i];
        cmd.Parameters.Add("@days", SqlDbType.Int).Value = Math.Clamp(days, 1, 365);

        var failures = new List<DiagnosedFailure>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var message = Db.Str(r, 4);
            var rule = Match(rules, message);
            failures.Add(new DiagnosedFailure(
                r.GetString(0), r.GetInt32(1), r.GetString(2), Db.Dt(r, 3), message,
                rule?.RuleId, rule?.Title, rule?.Cause, rule?.Solutions ?? []));
        }

        return new DiagnosticsReport(
            rules,
            failures,
            failures.Count,
            failures.Count(f => f.RuleId is not null),
            failures.Count(f => f.RuleId is null));
    }
}
