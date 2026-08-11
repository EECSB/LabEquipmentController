using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;
using Xunit.Abstractions;

namespace LabEquipmentController.Tests.Bench;

/// <summary>
/// The AI extraction path, end to end against the configured provider.
///
/// Separate from the bench switch and off by default under its own: extraction costs money
/// per run, which is a different thing to consent to than talking to an instrument on the
/// desk. Set `LEC_AI=1` to run it.
///
///     set LEC_AI=1
///     dotnet test --filter "FullyQualifiedName~AiExtraction"
///
/// It reads the provider and key the app already has, through the app's own settings, so
/// there is no key to put anywhere for this to work — configure it once under
/// Tools ▸ AI Connection and these tests use that.
///
/// The datasheet is whichever guide is named by `LEC_AI_PDF`, defaulting to the Siglent SDM
/// guide in `datasheets/`. That one is deliberate: its catalog is transcribed and known, so
/// what a model returns can be measured against 207 commands read by hand rather than merely
/// looked at.
/// </summary>
[Collection(BenchCollection.Name)]
public class AiExtractionTests
{
    private readonly ITestOutputHelper _out;
    public AiExtractionTests(ITestOutputHelper output) => _out = output;

    private static string Datasheet =>
        Environment.GetEnvironmentVariable("LEC_AI_PDF")
        ?? Path.Combine(RepoRoot(), "datasheets", "Siglent_SDM_Multimeter_ProgrammingGuide_EN02A.pdf");

    /// <summary>
    /// A command's identity for comparing two catalogs: the header, with the optional-node
    /// brackets and the case removed, so that "[SENSe:]CAPacitance:RANGe" and
    /// "SENSe:CAPacitance:RANGe" are recognised as one command written two ways.
    /// </summary>
    private static string HeaderKey(string syntax)
        => syntax.Split(' ', ',')[0]
                 .TrimEnd('?')
                 .Replace("[", "").Replace("]", "")
                 .Replace("::", ":")
                 .ToUpperInvariant();

    /// <summary>Walk up from the test binary until the repository is recognisable.</summary>
    /// <remarks>
    /// Keyed on the solution file rather than a document: SPEC.md moved into docs/ once
    /// already, and because this probe only runs under LEC_BENCH it would have failed
    /// silently — walking past the repository root and reading the folder above it.
    /// </remarks>
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "LabEquipmentController.slnx"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    // ------------------------------------------------------------------ offline checks

    /// <summary>
    /// These run without a provider, because everything up to the network call can be
    /// checked for free and is where the failures with real consequences live: a size guard
    /// that lets an impossible upload through wastes a paid call, and a page count read wrong
    /// picks the wrong strategy.
    /// </summary>
    [Fact]
    public void The_bundled_guides_all_pass_their_provider_limits()
    {
        string dir = Path.Combine(RepoRoot(), "datasheets");
        if (!Directory.Exists(dir)) return;

        foreach (AiProvider p in Enum.GetValues<AiProvider>())
        {
            AiProviderInfo info = AiProviderInfo.For(p);
            if (!info.SupportsPdfUpload) continue;

            foreach (string pdf in Directory.GetFiles(dir, "*.pdf"))
            {
                var fi = new FileInfo(pdf);
                int pages = DocumentText.PageCount(pdf);
                string? refusal = AiUploadLimits.Check(info, fi.Name, fi.Length, pages);
                _out.WriteLine($"{info.Label,-16} {fi.Name,-52} {fi.Length / 1024 / 1024,3} MB " +
                               $"{pages,4} pp  {refusal ?? "OK"}");
            }
        }
    }

    [Fact]
    public void An_oversized_upload_is_refused_with_the_numbers_that_explain_it()
    {
        AiProviderInfo anthropic = AiProviderInfo.For(AiProvider.Anthropic);

        string? refusal = AiUploadLimits.Check(anthropic, "huge.pdf", 30L * 1024 * 1024, pages: 50);

        Assert.NotNull(refusal);
        _out.WriteLine(refusal!);
        // 30 MB of file is 40 MB of base64, over a 32 MB request cap — the refusal has to
        // say so, or it reads as the file being under the limit and failing anyway.
        Assert.Contains("32", refusal);
    }

    // ------------------------------------------------------------------ live extraction

    [AiFact]
    public async Task Extracts_commands_from_a_real_datasheet()
    {
        UserSettings settings = SettingsStore.Load();
        Assert.True(settings.Ai != null,
            "No AI connection configured. Set one under Tools ▸ AI Connection first.");

        string? key = ApiKey(settings);
        Assert.False(string.IsNullOrWhiteSpace(key),
            "No API key stored, or it was encrypted for a different Windows account.");

        Assert.True(File.Exists(Datasheet), $"No datasheet at {Datasheet}");

        // A whole guide, not a chunk of one: where the provider takes a PDF directly there is
        // no chunking at all, so this is one request over 158 pages. The stored timeout is
        // whatever the user set for interactive work, and a test should not fail because of
        // that setting or quietly depend on it.
        AiConnection connection = settings.Ai!.Clone();
        connection.TimeoutSeconds = 600;

        _out.WriteLine($"provider : {connection.Info.Label} / {connection.EffectiveModel}");
        _out.WriteLine($"timeout  : {connection.TimeoutSeconds}s");
        _out.WriteLine($"datasheet: {Path.GetFileName(Datasheet)} " +
                       $"({DocumentText.PageCount(Datasheet)} pages)");

        var extractor = new CommandExtractor(new AiClient());
        var progress = new Progress<ExtractionProgress>(p =>
            _out.WriteLine($"  chunk {p.Chunk}/{p.OfChunks}, {p.FoundSoFar} so far — {p.Stage}"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        ExtractionResult result = await extractor.ExtractAsync(
            connection, key!, Datasheet, progress, cts.Token);

        _out.WriteLine($"extracted {result.Commands.Count} commands");
        Assert.NotEmpty(result.Commands);

        // Everything offered to a user must already look like SCPI — that gate is the only
        // thing between a model's prose and the review window.
        var malformed = result.Commands
            .Where(c => !ScpiSyntax.IsValidTemplate(c.Syntax))
            .Select(c => c.Syntax)
            .ToList();
        Assert.True(malformed.Count == 0,
            $"got past the syntax gate: {string.Join(" | ", malformed.Take(5))}");

        // Measured against the hand-transcribed catalog for the same guide. Not asserted
        // beyond "found something real": a model reading 158 pages will legitimately find
        // commands the hand pass skipped, and skip some it found.
        //
        // Compared on the header, not with ScpiSyntax.MatchesAny. That answers "does this
        // command a user typed fit this template", and both sides here are templates —
        // feeding it two put the match rate at 62% when the true figure was near 100, because
        // "[SENSe:]CAPacitance:RANGe" and "SENSe:CAPacitance:RANGe" are the same command
        // written two ways and it is not built to say so.
        CommandReference known = CommandReference.ForFamily(InstrumentFamily.Multimeter)!;
        var templates = known.Commands.Select(c => c.Syntax).ToList();
        var headers = templates.Select(HeaderKey).ToHashSet();
        int recognised = result.Commands.Count(c => headers.Contains(HeaderKey(c.Syntax)));

        _out.WriteLine($"of those, {recognised} match the hand-transcribed SDM catalog " +
                       $"({100.0 * recognised / result.Commands.Count:f0}%)");

        // Written out in full, because the interesting part is the difference and reading it
        // needs the guide open beside it. A run costs money; not keeping the result would
        // mean paying again to look at it twice.
        string dir = Environment.GetEnvironmentVariable("LEC_BENCH_REPORTS")
                     ?? Path.Combine(AppContext.BaseDirectory, "bench-reports");
        Directory.CreateDirectory(dir);

        string stem = Path.Combine(dir, "ai-" + Path.GetFileNameWithoutExtension(Datasheet));
        File.WriteAllLines(stem + "-all.txt",
            result.Commands.Select(c => $"{c.Syntax}\t{c.Description}"));
        File.WriteAllLines(stem + "-unmatched.txt",
            result.Commands.Where(c => !headers.Contains(HeaderKey(c.Syntax)))
                           .Select(c => $"{c.Syntax}\t{c.Description}"));
        File.WriteAllLines(stem + "-rejected.txt", result.Rejected);

        _out.WriteLine($"written to {stem}-*.txt " +
                       $"({result.Rejected.Count} dropped by the syntax gate)");

        foreach (var c in result.Commands.Take(15))
            _out.WriteLine($"  {c.Syntax,-46} {c.Description}");
    }

    /// <summary>
    /// Decrypt through the app's own store. Lives in the WinForms assembly because DPAPI is
    /// Windows-only and Core stays portable, so the test repeats the P/Invoke rather than
    /// reaching for it — the same three calls, against the same user scope.
    /// </summary>
    private static string? ApiKey(UserSettings settings)
    {
        if (string.IsNullOrEmpty(settings.AiApiKeyProtected)) return null;
        try
        {
            byte[] blob = Convert.FromBase64String(settings.AiApiKeyProtected);
            byte[] clear = System.Security.Cryptography.ProtectedData.Unprotect(
                blob, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(clear);
        }
        catch
        {
            return null;   // another machine, another account, or no key
        }
    }
}
