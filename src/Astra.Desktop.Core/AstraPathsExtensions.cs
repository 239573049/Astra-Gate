using Astra.Core;
namespace Astra.Desktop.Core;

public static class AstraPathsExtensions
{
    /// <summary>The server's log file for a given day: logs/astra-YYYYMMDD.log (<c>FileLogger</c> writes it).</summary>
    public static string DailyLogFile(this AstraPaths paths, DateTime date) => Path.Combine(paths.LogsDir, $"astra-{date:yyyyMMdd}.log");
}
