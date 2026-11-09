using ExtractConsole.Config;
using ExtractConsole.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ExtractConsole.Services;

/// <summary>
/// Lists what CLEO actually transferred, from the archive folder. Read-only - nothing
/// here deletes or writes.
/// <para>
/// The archive is a better answer to "what went to Strata" than the staging share: the
/// staging folder is emptied by the pickup within the hour, so outside a short window it
/// shows nothing, while the archive is the durable record and also holds what Strata
/// sent back.
/// </para>
/// </summary>
public sealed partial class FileService(EnvironmentConfig env)
{
    private readonly EnvironmentConfig _env = env;

    public List<OutputFile> List(DateTime forDate, int top = 500)
    {
        if (string.IsNullOrWhiteSpace(_env.ArchiveShare))
            throw new InvalidOperationException(
                $"No archive share configured for {_env.Name}. Set ArchiveShare in appsettings.json.");

        var dir = new DirectoryInfo(_env.ArchiveShare);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"{_env.ArchiveShare} is not reachable from this machine.");

        var day = forDate.Date;

        return dir.EnumerateFiles()
                  .Select(Describe)
                  // Prefer the transfer stamp in the name over the file timestamp: the
                  // stamp is when CLEO moved it, which is what "sent on this day" means.
                  .Where(f => (f.TransferredLocal ?? f.ModifiedLocal).Date == day)
                  .OrderByDescending(f => f.TransferredLocal ?? f.ModifiedLocal)
                  .Take(top)
                  .ToList();
    }

    private static OutputFile Describe(FileInfo fi)
    {
        var m = ArchiveSuffixRegex().Match(fi.Name);

        if (!m.Success)
            return new OutputFile(fi.Name, fi.Name, "", fi.Length, fi.LastWriteTime, null);

        DateTime? transferred = null;
        if (DateTime.TryParseExact(m.Groups["stamp"].Value, "yyyyMMdd_HHmmssfff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            transferred = parsed;
        }

        return new OutputFile(
            fi.Name,
            m.Groups["orig"].Value,
            m.Groups["dir"].Value.ToUpperInvariant(),
            fi.Length,
            fi.LastWriteTime,
            transferred);
    }

    /// <summary>Matches CLEO's "original.yyyyMMdd_HHmmssfff_OUT" rename.</summary>
    [GeneratedRegex(@"^(?<orig>.+)\.(?<stamp>\d{8}_\d{9})_(?<dir>IN|OUT)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveSuffixRegex();
}
