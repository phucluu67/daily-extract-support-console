using ExtractConsole.Config;
using System.Security.Principal;

namespace ExtractConsole.Services;

public enum Role { None, Finance, Viewer, Operator }

/// <summary>What one person may see and do, worked out once from who they are.</summary>
public sealed record Access(
    Role Role,
    string User,
    /// <summary>The group or account entry that granted the role - shown so a wrong role can be traced.</summary>
    string? GrantedBy,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> Tabs,
    bool CanChange)
{
    public bool MaySeeEnvironment(string name) =>
        Environments.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves a Windows identity to a role, and decides which API paths that role may call.
/// <para>
/// Today the identity is the account running the exe. Hosted, it would be the signed-in
/// visitor; nothing here changes except where the identity comes from.
/// </para>
/// </summary>
public static class AccessControl
{
    public static readonly string[] AllTabs =
        ["history", "volume", "recon", "files", "upserts", "adhoc", "ssis", "diagnostics"];

    /// <summary>Finance asked for exactly these four, and nothing above the tabs.</summary>
    public static readonly string[] FinanceTabs = ["history", "volume", "recon", "files"];

    public static Access Resolve(AccessConfig cfg, WindowsIdentity identity, IEnumerable<EnvironmentConfig> envs)
    {
        var user = identity.Name;
        var enabled = envs.Where(e => e.Enabled).Select(e => e.Name).ToList();

        if (!cfg.IsConfigured)
            return new Access(Role.Operator, user, "access control not configured", enabled, AllTabs, true);

        var principal = new WindowsPrincipal(identity);

        // Widest role wins.
        if (Match(cfg.Operators, user, principal) is { } op)
            return new Access(Role.Operator, user, op, enabled, AllTabs, true);

        if (Match(cfg.Viewers, user, principal) is { } vw)
            return new Access(Role.Viewer, user, vw, Restrict(enabled, cfg.ViewerEnvironments), AllTabs, false);

        if (Match(cfg.Finance, user, principal) is { } fin)
            return new Access(Role.Finance, user, fin, Restrict(enabled, cfg.FinanceEnvironments), FinanceTabs, false);

        return new Access(Role.None, user, null, [], [], false);
    }

    /// <summary>
    /// The API each role may call. Hiding a tab in the browser is not access control - the
    /// endpoint behind it has to refuse too, or anyone could ask for it directly.
    /// </summary>
    public static bool MayCall(Access a, string method, string path)
    {
        path = path.ToLowerInvariant();

        // Everyone, including a refused user, gets bootstrap - it is how the page learns why.
        if (path == "/api/bootstrap") return true;

        switch (a.Role)
        {
            case Role.Operator:
                return true;

            case Role.Viewer:
                // Every read; no change of any kind.
                return HttpMethods.IsGet(method);

            case Role.Finance:
                if (!HttpMethods.IsGet(method)) return false;
                return path == "/api/history"
                    || path.StartsWith("/api/run/", StringComparison.Ordinal)   // a run's steps, from history
                    || path == "/api/volumes"
                    || path == "/api/recon"
                    || path == "/api/files";

            default:
                return false;
        }
    }

    public static string Describe(Role role) => role switch
    {
        Role.Operator => "Operator",
        Role.Viewer => "Viewer",
        Role.Finance => "Finance",
        _ => "No access"
    };

    private static string? Match(IEnumerable<string> entries, string user, WindowsPrincipal principal)
    {
        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry)) continue;

            if (string.Equals(entry, user, StringComparison.OrdinalIgnoreCase)) return entry;

            // Group membership from the logon token, nested groups included. A name that
            // does not resolve (a typo, a group that does not exist yet) simply does not
            // match - it must never be the reason the console fails to start.
            try { if (principal.IsInRole(entry)) return entry; }
            catch (SystemException) { }
        }
        return null;
    }

    private static List<string> Restrict(List<string> enabled, List<string> allowed) =>
        [.. enabled.Where(e => allowed.Contains(e, StringComparer.OrdinalIgnoreCase))];
}
