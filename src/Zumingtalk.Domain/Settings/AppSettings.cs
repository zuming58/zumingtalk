using Zumingtalk.Domain.Dictation;

namespace Zumingtalk.Domain.Settings;

public sealed record RecognitionSettings(
    string Provider,
    string ApiKeyPreview,
    bool SemanticPunctuationEnabled,
    string MicrophoneName,
    int MicrophoneDeviceNumber = 0);

public sealed record HotkeySettings(
    string PrimaryHotkey,
    bool FallbackHotkeyEnabled,
    string FallbackHotkey);

public sealed record CompatibilitySettings(
    string LastTargetApplication,
    TextInsertionMethod LastInsertionMethod,
    bool LastInsertionSucceeded,
    TextInsertionMethod PreferredMode);

public sealed record LocalDataSettings(
    string RecordingsDirectory,
    int RetentionDays);

public sealed record AppSettings(
    RecognitionSettings Recognition,
    HotkeySettings Hotkeys,
    CompatibilitySettings Compatibility,
    LocalDataSettings LocalData,
    string SupportEmail = "");

public sealed record AliyunCredentialSettings(
    string AppKey,
    string AccessKeyId,
    string AccessKeySecret,
    string RegionId = "cn-shanghai",
    string Endpoint = "wss://nls-gateway-cn-shanghai.aliyuncs.com/ws/v1");

public sealed record BailianCredentialSettings(
    string ApiKey,
    string Model = "fun-asr-realtime",
    string Endpoint = "wss://dashscope.aliyuncs.com/api-ws/v1/inference");

public sealed record ZumingtalkCloudCredentialSettings(
    string ServiceBaseUrl,
    string DeviceToken);
