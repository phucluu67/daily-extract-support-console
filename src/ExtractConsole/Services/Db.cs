using ExtractConsole.Config;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ExtractConsole.Services;

/// <summary>
/// Connection factory. Every connection is Integrated Security - the app holds no
/// credentials and acts strictly as the signed-in Windows user, so SQL Server's own
/// permissions are the authorisation boundary and the audit trail names a real person.
/// </summary>
public sealed class Db(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;

    public SqlConnection Control(string database = "msdb") => Open(_env.ControlServer, database);

    public SqlConnection Data(string? database = null) => Open(_env.DataServer, database ?? _env.DataDatabase);

    /// <summary>
    /// The shared failure catalogue. Routed through its own setting rather than calling
    /// Control directly, so relocating it stays a configuration change.
    /// </summary>
    public SqlConnection Diagnostics() =>
        Open(_env.EffectiveDiagnosticsServer, _env.DiagnosticsDatabase);

    private SqlConnection Open(string server, string database)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException($"Environment '{_env.Name}' has no server configured.");

        var csb = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = _env.TrustServerCertificate,
            Encrypt = true,
            ConnectTimeout = _env.ConnectTimeoutSeconds,
            ApplicationName = "ExtractConsole",
            Pooling = true
        };
        return new SqlConnection(csb.ConnectionString);
    }

    public SqlCommand Cmd(SqlConnection cn, string sql)
    {
        var cmd = new SqlCommand(sql, cn) { CommandTimeout = _env.CommandTimeoutSeconds };
        return cmd;
    }

    public SqlCommand Proc(SqlConnection cn, string proc)
    {
        var cmd = new SqlCommand(proc, cn)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = _env.CommandTimeoutSeconds
        };
        return cmd;
    }

    // ---- reader helpers -------------------------------------------------

    public static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static int? Int(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static long? Long(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    public static DateTime? Dt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);

    /// <summary>
    /// SQL Agent stores durations as the integer HHMMSS (so 40500 is 4h 05m 00s, not
    /// forty thousand seconds). Decoding it as a plain number is the classic bug here.
    /// </summary>
    public static int DurationToSeconds(int runDuration)
    {
        var abs = Math.Abs(runDuration);
        var h = abs / 10000;
        var m = abs / 100 % 100;
        var s = abs % 100;
        return h * 3600 + m * 60 + s;
    }
}
