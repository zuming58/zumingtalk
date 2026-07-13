using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Storage;

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? rootDirectory = null)
    {
        DataDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zumingtalk");
        DatabasePath = Path.Combine(DataDirectory, "zumingtalk.sqlite3");
        RecordingsDirectory = Path.Combine(DataDirectory, "recordings");
        LogsDirectory = Path.Combine(DataDirectory, "logs");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string RecordingsDirectory { get; }

    public string LogsDirectory { get; }
}
