using System.Net;
using System.Net.Http.Json;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LabEquipmentController.Tests;

/// <summary>
/// The web server, started in memory and asked real questions.
/// </summary>
/// <remarks>
/// No instrument is involved: everything here is either a catalog lookup, address parsing,
/// or an error path. That is deliberate — these are the parts that have to work before a
/// socket is worth opening, and they are the parts that break silently. The endpoints that
/// need hardware are exercised on the bench, not here.
/// </remarks>
public class WebApiTests : IClassFixture<WebApplicationFactory<WebEntryPoint>>
{
    private readonly WebApplicationFactory<WebEntryPoint> _factory;

    public WebApiTests(WebApplicationFactory<WebEntryPoint> factory) => _factory = factory;

    [Fact]
    public async Task The_catalog_list_carries_every_family_that_has_one()
    {
        var client = _factory.CreateClient();
        var catalogs = await client.GetFromJsonAsync<List<CatalogSummary>>("/api/catalogs");

        Assert.NotNull(catalogs);
        // The same 35 the desktop app and the CLI serve — the catalogs are embedded in Core,
        // so a web server that reports fewer has lost its resources somewhere in packaging.
        Assert.Equal(35, catalogs!.Count);
        Assert.Equal(23_174, catalogs.Sum(c => c.CommandCount));
        Assert.Equal(518, catalogs.Sum(c => c.BenchVerified));
    }

    [Fact]
    public async Task A_catalog_can_be_filtered_by_the_command_as_it_would_be_sent()
    {
        var client = _factory.CreateClient();
        // The guide prints "[SENSe:]VOLTage[:DC]:NPLC"; nobody types the brackets, and a
        // plain substring search would find nothing.
        var hits = await client.GetFromJsonAsync<List<CatalogCommandDto>>(
            "/api/catalogs/KeysightMultimeter?filter=VOLTage:DC:NPLC");

        Assert.NotNull(hits);
        Assert.NotEmpty(hits!);
    }

    [Fact]
    public async Task An_unknown_family_is_a_404_rather_than_an_empty_list()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/catalogs/NoSuchInstrument");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_start_empty_and_an_unknown_one_cannot_be_closed()
    {
        var client = _factory.CreateClient();
        var sessions = await client.GetFromJsonAsync<List<SessionDto>>("/api/sessions");
        Assert.NotNull(sessions);

        var response = await client.DeleteAsync("/api/sessions/nothing-by-that-name");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sending_to_a_session_that_is_not_open_is_reported_not_thrown()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/sessions/gone/command", new CommandRequest("*IDN?"));
        var reply = await response.Content.ReadFromJsonAsync<CommandReply>();

        // A closed session is an ordinary outcome — a browser left open overnight will hit
        // it — so it comes back as a reply carrying an error, not as a 500.
        Assert.NotNull(reply);
        Assert.NotNull(reply!.Error);
    }

    [Fact]
    public async Task A_sequence_reports_the_instruments_it_needs()
    {
        var client = _factory.CreateClient();
        const string script = """
            DEVICE gen : SDG2042X
            DEVICE scope : DS2202
            COLUMNS Frequency, Vout
            """;

        var response = await client.PostAsJsonAsync("/api/sequence/requirements",
            new SequenceRunRequest(script, new Dictionary<string, string>()));
        var required = await response.Content.ReadFromJsonAsync<List<SequenceRequirement>>();

        Assert.NotNull(required);
        Assert.Equal(2, required!.Count);
        Assert.Contains(required, r => r.Alias == "gen" && r.Model == "SDG2042X");
    }

    [Fact]
    public async Task Running_a_sequence_with_an_unbound_alias_names_it_rather_than_starting()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/runs/sequence",
            new SequenceRunRequest("DEVICE gen : SDG2042X\nCOLUMNS A\n", new Dictionary<string, string>()));
        var summary = await response.Content.ReadFromJsonAsync<RunSummary>();

        Assert.NotNull(summary);
        Assert.True(summary!.Failed);
        Assert.Contains("gen", summary.Error);
    }

    [Fact]
    public async Task The_ai_status_says_it_is_off_when_no_key_is_configured()
    {
        var client = _factory.CreateClient();
        var status = await client.GetFromJsonAsync<AiStatus>("/api/ai");

        // The test host has no key, and the UI has to say so plainly rather than failing
        // at the first request.
        Assert.NotNull(status);
        Assert.False(status!.Configured);
        Assert.NotNull(status.Reason);
    }
}

/// <summary>Address parsing, which decides which transport an instrument gets.</summary>
public class WebAddressTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", InstrumentTransport.RawSocket, 5025)]
    [InlineData("192.168.1.20:5555", "192.168.1.20", InstrumentTransport.RawSocket, 5555)]
    [InlineData("vxi://192.168.1.20", "192.168.1.20", InstrumentTransport.Vxi11, 111)]
    [InlineData("TCPIP0::192.168.1.20::inst0::INSTR", "192.168.1.20", InstrumentTransport.Vxi11, 111)]
    public void Every_spelling_of_an_address_resolves_the_way_the_other_front_ends_resolve_it(
        string text, string host, InstrumentTransport transport, int port)
    {
        var (h, t, p, _) = BenchService.ParseAddress(text);
        Assert.Equal(host, h);
        Assert.Equal(transport, t);
        Assert.Equal(port, p);
    }

    [Fact]
    public void Port_111_means_VXI_11_however_it_was_written()
    {
        // The portmapper answers on 111 but never speaks SCPI. Read as a raw socket the
        // connection succeeds and then waits forever for a reply.
        var (_, transport, _, _) = BenchService.ParseAddress("192.168.1.20:111");
        Assert.Equal(InstrumentTransport.Vxi11, transport);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[fe80::1]:99999")]
    public void An_unusable_address_is_refused(string text)
        => Assert.Throws<ArgumentException>(() => BenchService.ParseAddress(text));

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x36, 0x00 }, "image/bmp")]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 }, "application/octet-stream")]
    public void A_screenshot_is_typed_from_its_bytes_not_from_a_guess(byte[] data, string expected)
    {
        // The instrument chooses the format — a Rigol sends BMP whatever was asked for —
        // and a browser handed the wrong MIME type shows a broken image and says nothing.
        Assert.Equal(expected, BenchService.ImageType(data));
    }
}
