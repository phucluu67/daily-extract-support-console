using System.Text;

namespace ExtractConsole.Services;

/// <summary>
/// Append-only local record of every start/stop the console issued. SQL Agent already
/// records who invoked a job, but that history ages out with msdb retention and does not
/// say the request came from here - this does, and it is the first thing to read when
/// someone asks "who restarted it?".
/// </summary>
public sealed class AuditLog
{
    private readonly string _path;
    private readonly Lock _gate = new();

    /// <summary>
    /// True when the log could not be opened. Logging must never be the reason the
    /// console will not start - this was a real crash: under a service account
    /// %LOCALAPPDATA% is not set, so the default path stayed literal, resolved against
    /// C:\Windows\system32, and the constructor threw UnauthorizedAccessException
    /// before anything useful had happened.
    /// </summary>
    public bool Disabled { get; }

    public AuditLog(string configuredPath)
    {
        _path = Environment.ExpandEnvironmentVariables(configuredPath);

        // An unexpanded %VAR% means the variable does not exist for this account. Put
        // the file beside the exe rather than somewhere surprising relative to the
        // caller's working directory.
        if (_path.Contains('%') || !System.IO.Path.IsPathRooted(_path))
            _path = System.IO.Path.Combine(AppContext.BaseDirectory, "audit.log");

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Disabled = true;
        }
    }

    public string FilePath => _path;

    public void Write(string environment, string action, string jobName, string detail, bool success)
    {
        if (Disabled) return;

        var line = string.Join(" | ",
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            Environment.UserDomainName + "\\" + Environment.UserName,
            Environment.MachineName,
            environment,
            action,
            jobName,
            success ? "OK" : "FAILED",
            detail.ReplaceLineEndings(" "));

        lock (_gate)
        {
            try { File.AppendAllText(_path, line + System.Environment.NewLine, Encoding.UTF8); }
            catch (IOException) { /* never let logging break the action it describes */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public IEnumerable<string> Tail(int lines)
    {
        try
        {
            if (Disabled || !File.Exists(_path)) return [];
            return File.ReadLines(_path).TakeLast(lines).Reverse();
        }
        catch (IOException) { return []; }
    }
}
