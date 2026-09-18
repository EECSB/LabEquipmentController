namespace LabEquipmentController.Web.Client.Contracts;

// The wire contract between the browser and the server. Deliberately plain records: the
// server maps Core's types into these, so a change inside Core cannot silently alter the
// shape the browser is coded against, and the browser never needs a reference to Core.

/// <summary>
/// One of this machine’s network adapters, as the scan controls offer it.
/// </summary>
/// <param name="WholeRange">
/// Every host on this adapter’s subnet, written out — 192.168.1.1-192.168.1.254. It is what
/// the IP range box is filled with, so that box says what a scan will actually sweep instead
/// of leaving it to a hint, and it says it in the same shape as anything anyone would type
/// into that box themselves rather than in a notation of its own.
///
/// The first and last are the first and last <em>hosts</em>: the network address and the
/// broadcast address are not instruments and are not probed, which is exactly what an empty
/// box has always done. Worked out here rather than in the browser — the browser has no
/// business doing subnet arithmetic, and this is the half that already holds the mask.
/// </param>
public sealed record LocalInterfaceDto(
    string Name, string Address, int PrefixLength, int HostCount, bool HasGateway, string WholeRange);

public sealed record ScanRequest(string? InterfaceAddress, string? Range, string? Ports, int TimeoutMs = 2000);

/// <summary>
/// The serial half of the scan card: which ports to open, and at what speeds.
/// </summary>
/// <param name="Port">
/// One port by name, or null/empty for every port the server has. The counterpart of
/// <see cref="ScanRequest.Range"/> — how much of the bench to cover — rather than of
/// <c>InterfaceAddress</c>, because there is only ever one set of serial ports to choose from.
/// </param>
/// <param name="Bauds">
/// The rates to try on each port, in order, stopping at the first that answers. Empty means
/// <c>SerialScanner.CommonBaudRates</c>. The counterpart of <see cref="ScanRequest.Ports"/>:
/// a short written list of the values worth trying.
/// </param>
public sealed record SerialScanRequest(string? Port, string? Bauds);

/// <param name="TypedAddress">
/// The row written the way it would be typed into the Address box — <c>vxi://host</c> for
/// VXI-11, <c>host:port</c> otherwise. Carried rather than worked out here: the page used to
/// derive it from <c>Port == 111</c>, which is Core's rule kept in a second place, and the
/// desktop kept it in no place at all and showed the port number instead.
/// </param>
/// <param name="Settings">
/// The line settings of a serial row — <c>9600-8-N-1</c> — and empty for a network one. One
/// DTO for both lists rather than two, because the two lists are the same four columns and
/// differ only in what the second one holds: a TCP port for an address, the framing for a
/// port name. A second record would have duplicated the stream, the hub method and the
/// export for the sake of one string.
/// </param>
/// <param name="Answered">
/// Whether this row is an instrument or only a place one could be. True by default, because
/// a network row exists only because something answered at that address. A serial row exists
/// because the port exists, so it is false until the port replies with an identity — and the
/// count in the status line is of instruments, not of adapters nobody has plugged anything
/// into.
/// </param>
public sealed record DeviceDto(string Address, int Port, string Transport, string Identity,
                               string TypedAddress = "", string Settings = "", bool Answered = true);

public sealed record ScanReport(IReadOnlyList<DeviceDto> Devices, int Scanned, bool Capped, string? Error);

public enum ScanStage
{
    /// <summary>The addresses and ports have been worked out; the sweep is about to start.</summary>
    Started,
    /// <summary>Another slice of the address list has been probed.</summary>
    Probed,
    /// <summary>An instrument answered. It is listed the moment this arrives.</summary>
    Found,
    /// <summary>The sweep ended — finished, stopped, or refused before it began.</summary>
    Done,
}

/// <summary>
/// One thing that happened during a scan, in the order it happened.
/// </summary>
/// <remarks>
/// A scan of a /24 takes long enough that a page which shows nothing until it ends looks
/// like a page that has hung, and a scan of a /20 takes long enough that the difference
/// matters a great deal. So the sweep reports as it goes and the browser draws as it hears:
/// how far along it is, and every instrument the moment it answers rather than all of them
/// at the end.
///
/// One record for four kinds of event, with the fields each kind fills. The alternative —
/// four types down a stream — buys nothing here: every one of them is small, they all travel
/// the same channel, and the browser switches on the stage either way.
/// </remarks>
/// <param name="Stage">Which of the four this is.</param>
/// <param name="Hosts">How many addresses the sweep covers. Set from <c>Started</c> onwards.</param>
/// <param name="Probed">How many have been probed so far. Only meaningful on <c>Probed</c>.</param>
/// <param name="Device">The instrument that answered, on <c>Found</c>.</param>
/// <param name="Detail">What is being swept, in words, resolved by the server: the address
/// count, the ports it settled on, and whether the range was cut short. The browser cannot
/// work this out — it only knows what was typed, and blank means "all of them".</param>
/// <param name="Error">Why it stopped, on <c>Done</c>, or null if it simply finished.</param>
public sealed record ScanEvent(
    ScanStage Stage, int Hosts, int Probed, DeviceDto? Device, string? Detail, string? Error);

/// <summary>
/// Open a session. The timeout is the instrument timeout — it covers the connection and every
/// query on it afterwards — and defaults to what the desktop's spinner starts at.
/// </summary>
public sealed record ConnectRequest(string Address, int TimeoutMs = 3000);

/// <summary>What the browser needs to render a console for one instrument.</summary>
public sealed record SessionDto(
    string Id,
    string Address,
    string Transport,
    /// <summary>
    /// The transport with the detail that names the connection - "VXI-11 (core port 771)",
    /// "raw socket (port 5025)". <see cref="Transport"/> is the short form the identity line
    /// shows; this is what the console says it connected over, which is the desktop's
    /// <c>IInstrumentClient.Description</c> and the same words.
    /// </summary>
    string TransportDetail,
    string Identity,
    string Family,
    string ProfileName,
    IReadOnlyList<QuickCommandDto> QuickCommands,
    bool SupportsWaveform,
    bool SupportsScreenshot,
    IReadOnlyList<ReadoutDto> Readouts,
    string? CatalogName,
    int CatalogCommandCount,
    /// <summary>
    /// The commands in flight or waiting their turn on this connection, oldest first. A
    /// connection carries one conversation at a time, so anything pressed while one is in
    /// flight queues — and the console shows the queue rather than appearing to have ignored
    /// the press. Kept fresh by the hub's "Queued" message.
    /// </summary>
    IReadOnlyList<string> Queued,
    /// <summary>
    /// True while a script or a live readout is driving this instrument. The console locks
    /// itself out meanwhile — the desktop's <c>InstrumentSession.IsBusy</c>, which it sets in
    /// exactly those two places. Everything is serialized, so a command typed during a run
    /// cannot corrupt a reply; it interleaves, and a command that changes the instrument's
    /// function between two steps of a sweep changes what the sweep measured.
    ///
    /// Carried on the session rather than worked out in the page, because the bench is one
    /// shared workspace: a console open in a second tab is watching the same instrument. Kept
    /// fresh by the hub's "Driven" message.
    /// </summary>
    bool Driven);

public sealed record QuickCommandDto(string Label, string Command);

public sealed record ReadoutDto(string Label, string Query, string Unit);

public sealed record CommandRequest(string Text);

/// <summary>
/// One exchange. <paramref name="Reply"/> is null for a command that draws no answer, which
/// is different from an empty reply — the console shows those differently, and conflating
/// them is how "no response" starts looking like a successful read of nothing.
/// </summary>
public sealed record CommandReply(string Command, string? Reply, bool IsQuery, double Seconds, string? Error);

/// <summary>
/// One channel out of a capture, or why that channel could not be read.
///
/// A channel that is switched off, or that this instrument does not have, fails on its own
/// rather than failing the capture: a two-channel scope asked for four should draw the two it
/// has and say what happened to the others.
/// </summary>
public sealed record TraceDto(int Channel, IReadOnlyList<double> Voltage, string? Error);

/// <summary>
/// A capture of one or more channels, against one time axis.
///
/// One axis because a scope samples every channel on the same time base and the same trigger —
/// that is what makes two traces comparable, and comparing them is the whole reason to capture
/// them together. The times come from the first channel that answered.
/// </summary>
public sealed record WaveformSetDto(
    IReadOnlyList<double> Time, IReadOnlyList<TraceDto> Traces, double XIncrement, string? Error);

/// <summary>
/// What an instrument said when asked to list its own commands.
///
/// <see cref="Query"/> travels so the console can name what it sent in its log, as the desktop
/// does — the point of the line is that you can see the query and try it yourself. Failure is
/// not an error: most budget instruments do not implement SCPI-99's <c>SYSTem:HELP:HEADers?</c>
/// at all, and that is the ordinary case rather than something going wrong.
/// </summary>
public sealed record DiscoveryReply(string Query, bool Success, int Count, string Headers);

/// <summary>
/// SCPI-99's one standard "list your commands" query, spelled out here so a console can say
/// what it is about to try before the answer arrives — which on an instrument that does not
/// implement it means several seconds of nothing.
///
/// Core's <c>CommandDiscovery.Query</c> is what actually goes down the wire, and the reply
/// carries it back; this is the same string on the near side of the network. If the two ever
/// part company the console's own two lines will disagree in front of you.
/// </summary>
public static class Discovery
{
    public const string Query = "SYSTem:HELP:HEADers?";
}

public sealed record CatalogSummary(string Family, string Instrument, string Manufacturer, int CommandCount, int BenchVerified, string? GuideTitle, string? GuideUrl);

/// <summary>
/// One programming guide the server holds a copy of.
///
/// <c>Path</c> is where it sits under the collection's root, with forward slashes, which is
/// what the fetch URL is built from — the absolute path on the server's disk is not something
/// a page has any use for, and is not something a page should be told either.
/// </summary>
public sealed record DatasheetDto(string Path, string Name, long Bytes);

/// <summary>A guide on its way to the server's collection, base64 as the extractor's uploads are.</summary>
public sealed record DatasheetUpload(string FileName, string Base64, string? Manufacturer);

/// <summary>
/// What the About box states. Every field is measured on the server at the moment it is
/// asked — the version off the assembly, the runtime and OS off the framework, the catalog
/// totals by counting the catalogs — so nothing on that box can quietly go stale the way a
/// hand-written figure does. That is the rule the desktop's AboutForm is held to, and the
/// web build has no excuse to be looser about it.
/// </summary>
public sealed record AboutDto(
    string Version, string Framework, string Os,
    int Catalogs, int Commands, int BenchVerified,
    string Repository, string License);

/// <summary>
/// One catalog entry as the browser sees it. <c>AiExtracted</c> travels because it must be
/// shown: an entry a model read out of a datasheet is not the same claim as one transcribed
/// from a guide, and the UI marks it apart for exactly the reason SPEC §10 exists.
/// </summary>
public sealed record CatalogCommandDto(string Category, string Syntax, string Description, string? Example, bool IsQuery, bool BenchVerified, bool CrossChecked, bool AiExtracted);

public sealed record ScriptRunRequest(string SessionId, string Script);

/// <summary>
/// A multi-instrument script and its bindings, alias to session id. To run it, every alias the
/// script declares; to ask what it binds to, only what has been picked by hand in the table.
/// </summary>
public sealed record SequenceRunRequest(string Script, IReadOnlyDictionary<string, string> Bindings);

/// <summary>
/// One DEVICE line of a script, and what it is bound to on the bench right now — a row of the
/// table over the editor, worked out on the server by the rule the desktop's strip uses.
/// </summary>
/// <param name="SessionId">The session playing the part, or null.</param>
/// <param name="Note">Why nothing is — "2 connected: pick one", "taken by dmm" — or null.</param>
public sealed record SequenceRequirement(string Alias, string Model, string? SessionId = null, string? Note = null);

public sealed record ScriptOutputLine(string Text, string Kind);

public sealed record RecordedRow(IReadOnlyList<string> Values);

public sealed record RunSummary(string RunId, IReadOnlyList<string> Columns, bool Failed, string? Error);

// ------------------------------------------------------------------------ the plot

/// <summary>
/// A table of recorded rows and what to draw from it: which column runs across the bottom,
/// which ones are curves, and whether either axis is logarithmic.
/// </summary>
/// <remarks>
/// The arithmetic behind a plot — reading numbers out of whatever the instrument said,
/// choosing axis ranges, placing ticks a person would have chosen — lives in Core's
/// ResultPlot, which is where the desktop's plot gets it too. The browser cannot call Core
/// (the whole library, catalogs and all, is not something to download to draw a curve), so
/// it asks the server, and the two builds plot the same numbers the same way.
/// </remarks>
/// <param name="YColumns">
/// Null asks the server to choose — the first sensible plot, which is the first column across
/// and everything else that has anything to draw. It is the difference between a plot that
/// appears and a plot that has to be assembled, and the rule for it is the desktop's.
/// </param>
public sealed record PlotRequest(
    IReadOnlyList<string> Columns,
    IReadOnlyList<RecordedRow> Rows,
    int XColumn,
    IReadOnlyList<int>? YColumns,
    bool LogX = false,
    bool LogY = false);

/// <summary>A point as a fraction of each axis, 0 at the origin and 1 at the far end.</summary>
/// <remarks>
/// Fractions rather than values, so the page is pure geometry: mapping a value onto an axis
/// is Core's <c>ResultPlot.Fraction</c>, and a log axis is not a mapping worth writing twice.
/// </remarks>
public sealed record PlotPointDto(double X, double Y);

public sealed record PlotSeriesDto(string Name, string Colour, IReadOnlyList<PlotPointDto> Points);

/// <param name="At">Where the tick sits along its axis, 0 to 1.</param>
public sealed record PlotTickDto(double At, string Label);

public sealed record PlotAxisDto(double Min, double Max, bool Logarithmic, IReadOnlyList<PlotTickDto> Ticks);

/// <param name="CanLogX">
/// Whether a log axis is meaningful — refused rather than fudged when anything on it is zero
/// or negative, so the switch is disabled instead of silently dropping those points.
/// </param>
/// <param name="ChosenY">Which columns were actually drawn, so the tick boxes can say so.</param>
/// <param name="Plottable">Which columns have anything in them that reads as a number.</param>
/// <param name="GuessedUnit">
/// The unit offered for the value axis, from the column heading or the commands recorded
/// beside the readings. Only ever a guess — an instrument asked for volts can be wired across
/// a shunt and reading amps — so anything typed into the box wins.
/// </param>
public sealed record PlotReply(
    IReadOnlyList<PlotSeriesDto> Series,
    PlotAxisDto X,
    PlotAxisDto Y,
    bool CanLogX,
    bool CanLogY,
    IReadOnlyList<int> ChosenY,
    IReadOnlyList<int> Plottable,
    string XName,
    string GuessedUnit);

/// <summary>
/// Which of the desktop's two capture windows is wanted: <c>WaveformForm</c>, which keeps a
/// repeat toggle because a trace is watched, or <c>ScreenCaptureForm</c>, which does not
/// because a screen grab is taken once.
/// </summary>
public enum CaptureMode
{
    Waveform,
    Screen,
}

public sealed record WaveformDto(IReadOnlyList<double> Time, IReadOnlyList<double> Voltage, double XIncrement, string? Error);

public sealed record ScreenshotDto(string ContentType, string Base64, int Bytes, string Command, string? Error);

public sealed record ExampleDto(string Name, string Script);

// -------------------------------------------------------------------- meter readout

/// <summary>
/// One polled reading, and the figures for the run so far.
///
/// The figures travel with it because the series they come from is Core's
/// <c>ReadingSeries</c> — including its cap and its "showing the last N of M" — and the
/// browser half cannot reach Core. Counting them again in the page would be a second
/// implementation of a ring buffer to keep in step with the first.
/// </summary>
public sealed record ReadingDto(
    double Seconds, double Value,
    int Count, long TotalTaken, double Min, double Max, double Mean,
    string? Error);

// ------------------------------------------------------------------ script language

/// <summary>One section of the script-language guide: a heading, a paragraph, an example.</summary>
public sealed record ScriptSectionDto(string Heading, string Prose, string Example);

/// <summary>
/// The guide for one of the two script languages. Written once in Core's ScriptGuide, which
/// the desktop app's reference window reads as well — the browser cannot reach Core, so it
/// comes over the wire instead of being copied into a second page of prose that would drift.
/// </summary>
public sealed record ScriptGuideDto(string Title, string Lead, IReadOnlyList<ScriptSectionDto> Sections);

/// <summary>
/// One entry on the Snippets menu: what it is, what it is for, the word that triggers it, and
/// the lines it writes. Straight out of Core's ScriptLanguage, so the menu the desktop shows
/// and the menu here are the same menu.
/// </summary>
public sealed record SnippetDto(string Trigger, string Title, string Summary, string Body);

/// <summary>What the editor is asking about: the whole script, and the part-word under the caret.</summary>
/// <param name="Script">
/// The whole of it, because what can come next depends on what has already been written — the aliases
/// a sequence declared, the names a capture bound.
/// </param>
/// <param name="Prefix">The part-word under the caret. Empty offers everything.</param>
/// <param name="Sequence">Which dialect: the sequence language has words the single-instrument one has not.</param>
/// <param name="SessionId">
/// Whose catalog to offer commands from, if any. A console's script editor knows the instrument it
/// runs against; the multi-instrument one does not, and offers the language alone.
/// </param>
public sealed record CompletionRequest(string Script, string Prefix, bool Sequence, string? SessionId = null);

/// <param name="Text">What gets inserted when it is chosen — unless <paramref name="Body"/> is set.</param>
/// <param name="Detail">The grey text beside it, saying what kind of thing this is.</param>
/// <param name="Kind">Snippet, Keyword, Alias, Variable or Command, as Core names them.</param>
/// <param name="Body">A snippet's template, placeholders and all, or null for a plain word.</param>
public sealed record CompletionDto(string Text, string Detail, string Kind, string? Body);

// ----------------------------------------------------------------------------- AI

/// <summary>
/// One provider's preset, as the desktop's AiProviderInfo holds it. Sent to the browser
/// because the client half cannot reference Core — it has no instrument logic in it at all —
/// and a provider dropdown that cannot fill in that provider's endpoint and model is a
/// dropdown that makes you go and look them up.
/// </summary>
public sealed record AiProviderDto(
    string Name, string Label, string DefaultBaseUrl, string DefaultModel,
    bool SupportsPdfUpload, string PdfCostNote);

/// <summary>
/// The AI connection as the server has it, and everything a settings box needs to edit it.
///
/// The key is the one thing that does not come back: <see cref="Configured"/> says whether
/// there is one and <see cref="KeyFromConfiguration"/> says where it came from, which is all
/// the box needs to know to label its own field. The desktop's dialog works the same way —
/// dots in the box and a placeholder that says a key is stored.
/// </summary>
public sealed record AiStatus(
    bool Configured, string Provider, string Model, string Endpoint, int TimeoutSeconds,
    bool? ExtractTextLocally, bool KeyFromConfiguration, string? Reason,
    IReadOnlyList<AiProviderDto> Providers, string Effort, IReadOnlyList<string> Efforts,
    IReadOnlyList<AiConnectionDto> Connections, string SelectedId);

/// <summary>
/// One connection in the list, as a picker needs it.
///
/// The flat fields above describe whichever of these is selected, which is what every
/// window that spends a connection is actually using — this is the list to choose from.
/// <c>Label</c> is composed on the server by <c>AiConnections.Labels</c>, which is what
/// keeps two connections on the same model apart without making the user name them.
/// </summary>
public sealed record AiConnectionDto(
    string Id, string Label, string Name, string Provider, string Model, bool Configured);

/// <summary>
/// An edited connection on its way back to the server.
///
/// <see cref="ApiKey"/> is three-state on purpose, as the desktop's box is: null leaves
/// whatever is stored alone, a value replaces it, and empty forgets it. A web form has no
/// other way to unset a key it is never shown.
/// </summary>
public sealed record AiSettingsUpdate(
    string Provider, string Endpoint, string Model, bool? ExtractTextLocally,
    int TimeoutSeconds, string? ApiKey, string? Effort = null,
    string? Id = null, string? Name = null);

/// <summary>The connection as it stands after an edit, or why the edit was refused.</summary>
public sealed record AiSettingsReply(AiStatus? Status, string? Error);

/// <summary>
/// Which of the two AI windows is wanted. They are two forms on the desktop —
/// <c>ScriptAiForm</c>, opened from a script editor, and <c>DatasheetExtractForm</c>, opened
/// from a console — reached from different buttons and doing different jobs.
/// </summary>
public enum AiTool
{
    Script,
    Datasheet,
}

/// <summary>
/// One exchange already had, on its way back to the model — Core's <c>ScriptTurn</c>, which
/// the browser cannot reach.
///
/// The conversation is held by the browser and sent whole with each request, rather than kept
/// on the server. The server has no notion of a window, and two script windows open at once
/// are two conversations; a server-side history would splice them into one.
/// </summary>
public sealed record AiTurn(string Request, string Script, string Notes, IReadOnlyList<string> Undocumented);

public sealed record AiScriptRequest(string Request, IReadOnlyList<string> SessionIds, bool IsSequence, string? CurrentScript, string? RecentOutput, IReadOnlyList<AiTurn>? History = null);

/// <param name="Notes">
/// The model's own one-line account of what it did. The desktop has always shown this under
/// the draft it is about; this build dropped it on the floor until the window became a
/// conversation and there was somewhere for it to live.
/// </param>
public sealed record AiScriptReply(string Script, IReadOnlyList<string> Undocumented, string? Error, string Notes = "");

public sealed record AiExtractRequest(string FileName, string Base64);

/// <param name="Token">
/// Names this extraction on the server, so the ticked commands can be saved without sending
/// them back up. A round trip through the DTO would drop the one field it does not carry —
/// the note recording what a guide prints that looks wrong — and a lossy return journey for
/// data that never left is not a trade worth making.
/// </param>
public sealed record AiExtractReply(
    int Found, int Rejected, IReadOnlyList<CatalogCommandDto> Commands, string? Token, string? Error);

/// <summary>
/// Save the ticked commands from an extraction. Nothing is written until this is asked for:
/// what comes out of a datasheet is extracted, not verified, and the reading of it is the
/// point of showing it first.
/// </summary>
/// <param name="Keep">Indexes into the commands the extraction returned.</param>
/// <summary>
/// Keep these, for the instrument the window was opened on.
/// </summary>
/// <remarks>
/// The session rather than a name, because there is no name to give: the desktop files an
/// extraction under the instrument's model, out of its own <c>*IDN?</c>, and asks nobody. A box
/// to type one in was this build's invention, and an invitation to file the same instrument's
/// commands under two spellings.
/// </remarks>
public sealed record AiSaveExtractRequest(string Token, string SessionId, IReadOnlyList<int> Keep);

public sealed record AiSaveExtractReply(int Saved, string? SavedTo, string? Error);
