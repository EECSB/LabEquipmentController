namespace LabEquipmentController.Web.Client.Contracts;

// The wire contract between the browser and the server. Deliberately plain records: the
// server maps Core's types into these, so a change inside Core cannot silently alter the
// shape the browser is coded against, and the browser never needs a reference to Core.

public sealed record LocalInterfaceDto(string Name, string Address, int PrefixLength, int HostCount, bool HasGateway);

public sealed record ScanRequest(string? InterfaceAddress, string? Range, string? Ports, int TimeoutMs = 2000);

public sealed record DeviceDto(string Address, int Port, string Transport, string Identity);

public sealed record ScanReport(IReadOnlyList<DeviceDto> Devices, int Scanned, bool Capped, string? Error);

public sealed record ConnectRequest(string Address, int TimeoutMs = 5000);

/// <summary>What the browser needs to render a console for one instrument.</summary>
public sealed record SessionDto(
    string Id,
    string Address,
    string Transport,
    string Identity,
    string Family,
    string ProfileName,
    IReadOnlyList<QuickCommandDto> QuickCommands,
    bool SupportsWaveform,
    bool SupportsScreenshot,
    IReadOnlyList<ReadoutDto> Readouts,
    string? CatalogName,
    int CatalogCommandCount);

public sealed record QuickCommandDto(string Label, string Command);

public sealed record ReadoutDto(string Label, string Query, string Unit);

public sealed record CommandRequest(string Text);

/// <summary>
/// One exchange. <paramref name="Reply"/> is null for a command that draws no answer, which
/// is different from an empty reply — the console shows those differently, and conflating
/// them is how "no response" starts looking like a successful read of nothing.
/// </summary>
public sealed record CommandReply(string Command, string? Reply, bool IsQuery, double Seconds, string? Error);

public sealed record CatalogSummary(string Family, string Instrument, string Manufacturer, int CommandCount, int BenchVerified, string? GuideTitle, string? GuideUrl);

/// <summary>
/// One catalog entry as the browser sees it. <c>AiExtracted</c> travels because it must be
/// shown: an entry a model read out of a datasheet is not the same claim as one transcribed
/// from a guide, and the UI marks it apart for exactly the reason SPEC §10 exists.
/// </summary>
public sealed record CatalogCommandDto(string Category, string Syntax, string Description, string? Example, bool IsQuery, bool BenchVerified, bool CrossChecked, bool AiExtracted);

public sealed record ScriptRunRequest(string SessionId, string Script);

public sealed record SequenceRunRequest(string Script, IReadOnlyDictionary<string, string> Bindings);

public sealed record SequenceRequirement(string Alias, string Model);

public sealed record ScriptOutputLine(string Text, string Kind);

public sealed record RecordedRow(IReadOnlyList<string> Values);

public sealed record RunSummary(string RunId, IReadOnlyList<string> Columns, bool Failed, string? Error);

public sealed record WaveformDto(IReadOnlyList<double> Time, IReadOnlyList<double> Voltage, double XIncrement, string? Error);

public sealed record ScreenshotDto(string ContentType, string Base64, int Bytes, string Command, string? Error);

public sealed record ExampleDto(string Name, string Script);

// ----------------------------------------------------------------------------- AI

public sealed record AiStatus(bool Configured, string Provider, string Model, string? Reason);

public sealed record AiScriptRequest(string Request, IReadOnlyList<string> SessionIds, bool IsSequence, string? CurrentScript, string? RecentOutput);

public sealed record AiScriptReply(string Script, IReadOnlyList<string> Undocumented, string? Error);

public sealed record AiExtractRequest(string FileName, string Base64, string InstrumentKey);

public sealed record AiExtractReply(int Found, int Rejected, IReadOnlyList<CatalogCommandDto> Commands, string? SavedTo, string? Error);
