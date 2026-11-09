using System.Text;
using System.Text.RegularExpressions;

namespace ExtractConsole.Services;

/// <summary>
/// Works out from a step's own T-SQL whether it has been commented out - i.e. whether it
/// reports success to SQL Agent while doing nothing.
/// <para>
/// This used to be hand-maintained config, and the config went stale: PROD step 9 was
/// re-enabled (its disable block wrapped in <c>/* */</c>) while the console kept calling
/// it disabled. Reading the step body makes the console self-correcting, because these
/// steps are demonstrably switched on and off over time.
/// </para>
/// <para>
/// Deliberately conservative: only an empty body, or the exact
/// <c>IF (1 = 1) ... RETURN</c> short-circuit idiom used in this job, counts. Anything
/// else is treated as a normal step. A false "disabled" label would be worse than none.
/// </para>
/// </summary>
public static partial class StepBody
{
    /// <summary>
    /// True when the step cannot do any work. <paramref name="command"/> may be a prefix
    /// of the real command - the guard, if present, is always at the top.
    /// </summary>
    public static bool IsNoOp(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return true;

        var body = StripComments(command).Trim();
        if (body.Length == 0) return true;

        // The guard has to come first to short-circuit anything. A tautology further down
        // (after real work, or a `WHILE 1 = 1` polling loop in the orchestrator steps)
        // must not count - hence the anchored match.
        if (!LeadingTautologyRegex().IsMatch(body)) return false;

        return ReturnRegex().IsMatch(body);
    }

    /// <summary>
    /// True when a step names <paramref name="jobName"/> as a quoted string - which is how
    /// every step that launches a child job identifies it, whether it calls
    /// <c>sp_start_job 'X'</c> directly or sets <c>@JobName = N'X'</c> first. Pass the body
    /// with comments already stripped (<see cref="StripComments"/>), so a call that has been
    /// commented out does not count. Single quotes for T-SQL, double for PowerShell steps.
    /// <para>
    /// This replaces mapping child jobs to steps by step NUMBER in configuration. Numbers
    /// move whenever a step is inserted - PROD gained one on 2026-10-08 and another on
    /// 2026-10-22 - and every time the console attached the child jobs to the wrong step.
    /// </para>
    /// </summary>
    public static bool NamesJob(string? strippedBody, string jobName)
    {
        if (string.IsNullOrEmpty(strippedBody) || string.IsNullOrWhiteSpace(jobName)) return false;
        var name = jobName.Trim();
        return strippedBody.Contains("'" + name.Replace("'", "''") + "'", StringComparison.OrdinalIgnoreCase)
            || strippedBody.Contains("\"" + name + "\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes <c>--</c> line comments and nestable <c>/* */</c> block comments, leaving
    /// string literals alone so a <c>'--'</c> inside quotes is not mistaken for a comment.
    /// </summary>
    public static string StripComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // String literal - copy verbatim, honouring '' as an escaped quote.
            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            sb.Append("''");
                            i += 2;
                            continue;
                        }
                        sb.Append('\'');
                        i++;
                        break;
                    }
                    sb.Append(sql[i]);
                    i++;
                }
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; continue; }
                    if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; i += 2; continue; }
                    i++;
                }
                sb.Append(' ');     // keep tokens either side apart
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^IF\s*\(?\s*1\s*=\s*1\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingTautologyRegex();

    [GeneratedRegex(@"\bRETURN\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnRegex();
}
