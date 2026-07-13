namespace Zumingtalk.Domain.Services;

public interface IAppPaths
{
    string DataDirectory { get; }

    string DatabasePath { get; }

    string RecordingsDirectory { get; }

    string LogsDirectory { get; }
}
