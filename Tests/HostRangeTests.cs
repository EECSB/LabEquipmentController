using System.Collections.Generic;
using System.Linq;
using System.Net;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The range field decides which addresses get probed, so every way it can be misread is a
/// way to either miss the instrument or knock on two hundred doors that are not the bench's.
/// The bare forms ("20-60") carry the most risk: they mean nothing without the interface
/// beside them, and getting the fill-in wrong scans a subnet nobody asked about.
/// </summary>
public class HostRangeTests
{
    private static readonly IPAddress Context = IPAddress.Parse("192.168.1.28");

    private static List<string> Hosts(string text, IPAddress? context = null, int max = 4096)
    {
        Assert.True(HostRange.TryParse(text, context ?? Context, out HostRange? r, out string error),
                    $"\"{text}\" should parse, but: {error}");
        return r!.Enumerate(max, out _).Select(a => a.ToString()).ToList();
    }

    private static string Error(string text, IPAddress? context = null)
    {
        Assert.False(HostRange.TryParse(text, context, out _, out string error),
                     $"\"{text}\" should not parse.");
        Assert.False(string.IsNullOrWhiteSpace(error), "a refusal has to say why");
        return error;
    }

    // ---------------------------------------------------------------------- the forms

    [Fact]
    public void One_address_is_a_range_of_one()
        => Assert.Equal(new[] { "192.168.1.50" }, Hosts("192.168.1.50"));

    [Fact]
    public void A_span_written_out_in_full_covers_both_ends()
    {
        List<string> hosts = Hosts("192.168.1.20-192.168.1.24");
        Assert.Equal(new[] { "192.168.1.20", "192.168.1.21", "192.168.1.22",
                             "192.168.1.23", "192.168.1.24" }, hosts);
    }

    /// <summary>The everyday form: the subnet is said once and the end is just an octet.</summary>
    [Fact]
    public void A_span_may_end_at_a_bare_octet()
        => Assert.Equal(Hosts("192.168.1.20-192.168.1.24"), Hosts("192.168.1.20-24"));

    /// <summary>
    /// The end octet is read against the start of the range, not against this machine's own
    /// address — "10.0.5.20-24" means 10.0.5.24 whatever subnet the interface is on.
    /// </summary>
    [Fact]
    public void A_bare_end_octet_follows_the_start_of_the_range_not_the_interface()
        => Assert.Equal(new[] { "10.0.5.20", "10.0.5.21", "10.0.5.22" }, Hosts("10.0.5.20-22"));

    [Fact]
    public void A_bare_span_is_filled_in_from_the_selected_interface()
        => Assert.Equal(new[] { "192.168.1.20", "192.168.1.21" }, Hosts("20-21"));

    [Fact]
    public void Leading_dots_are_allowed_because_people_write_them()
        => Assert.Equal(Hosts("20-21"), Hosts(".20-.21"));

    [Fact]
    public void A_partial_address_replaces_as_many_octets_as_it_gives()
        => Assert.Equal(new[] { "192.168.7.9" }, Hosts("7.9"));

    [Theory]
    [InlineData(",")]
    [InlineData(" ")]
    [InlineData("; ")]
    public void Several_ranges_can_be_listed(string separator)
    {
        List<string> hosts = Hosts($"192.168.1.20-21{separator}192.168.1.40");
        Assert.Equal(new[] { "192.168.1.20", "192.168.1.21", "192.168.1.40" }, hosts);
    }

    // ---------------------------------------------------------------------------- cidr

    [Fact]
    public void A_block_leaves_out_its_network_and_broadcast_addresses()
    {
        List<string> hosts = Hosts("192.168.1.16/29");
        Assert.Equal(6, hosts.Count);
        Assert.Equal("192.168.1.17", hosts.First());
        Assert.Equal("192.168.1.22", hosts.Last());
    }

    [Fact]
    public void A_block_is_read_from_wherever_inside_it_the_user_pointed()
        => Assert.Equal(Hosts("192.168.1.16/29"), Hosts("192.168.1.19/29"));

    /// <summary>A /31 is a point-to-point link and a /32 is one host: both ends are usable,
    /// and there is no network or broadcast address to leave out.</summary>
    [Theory]
    [InlineData("192.168.1.20/32", 1)]
    [InlineData("192.168.1.20/31", 2)]
    public void The_two_prefixes_with_no_spare_addresses_keep_both_ends(string text, int expected)
        => Assert.Equal(expected, Hosts(text).Count);

    // -------------------------------------------------------------------------- refusals

    [Fact]
    public void A_range_that_runs_backwards_is_refused_rather_than_silently_swapped()
        => Assert.Contains("backwards", Error("192.168.1.60-20"));

    [Theory]
    [InlineData("192.168.1.300")]
    [InlineData("192.168.1.20-999")]
    [InlineData("192.168.1.2.3.4")]
    [InlineData("192.168.1.20-")]
    [InlineData("not an address")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1.0/x")]
    public void Nonsense_is_refused(string text) => Error(text);

    [Fact]
    public void Nothing_typed_is_not_a_range()
    {
        Assert.False(HostRange.TryParse("", Context, out _, out _));
        Assert.False(HostRange.TryParse(null, Context, out _, out _));
        Assert.False(HostRange.TryParse("   ", Context, out _, out _));
    }

    /// <summary>
    /// Without an interface there is nothing to fill a bare form in from, and guessing which
    /// subnet was meant would scan somebody else's.
    /// </summary>
    [Fact]
    public void A_bare_form_with_no_interface_to_fill_it_in_is_refused()
        => Assert.Contains("part of an address", Error("20-60", context: null));

    [Fact]
    public void A_full_address_needs_no_interface()
        => Assert.True(HostRange.TryParse("192.168.1.20-60", null, out _, out _));

    // -------------------------------------------------------------------------- counting

    [Fact]
    public void Overlapping_ranges_are_probed_once_each()
    {
        // Probing a host twice is not just waste: these instruments accept one connection
        // at a time, and the second probe is the one that wedges them (SPEC §13).
        List<string> hosts = Hosts("192.168.1.20-25, 192.168.1.22, 192.168.1.24-26");
        Assert.Equal(new[] { "192.168.1.20", "192.168.1.21", "192.168.1.22", "192.168.1.23",
                             "192.168.1.24", "192.168.1.25", "192.168.1.26" }, hosts);
    }

    [Fact]
    public void A_range_bigger_than_the_cap_is_truncated_and_says_so()
    {
        Assert.True(HostRange.TryParse("10.0.0.0/8", Context, out HostRange? r, out _));
        List<IPAddress> hosts = r!.Enumerate(4096, out bool capped);

        Assert.Equal(4096, hosts.Count);
        Assert.True(capped);
    }

    [Fact]
    public void A_range_that_lands_exactly_on_the_cap_is_not_reported_as_truncated()
    {
        Assert.True(HostRange.TryParse("192.168.1.1-10", Context, out HostRange? r, out _));
        r!.Enumerate(10, out bool capped);
        Assert.False(capped);
    }

    /// <summary>The count is what the status line reports before a scan starts, and a
    /// mistyped prefix can cover the whole of IPv4 — which has to be a number, not an
    /// overflow.</summary>
    [Fact]
    public void The_size_of_a_range_is_counted_without_enumerating_it()
    {
        Assert.True(HostRange.TryParse("0.0.0.0/0", Context, out HostRange? r, out _));
        Assert.Equal(4294967294L, r!.Count);
    }

    [Fact]
    public void The_last_address_of_ipv4_terminates_rather_than_wrapping()
    {
        Assert.True(HostRange.TryParse("255.255.255.254-255.255.255.255", Context,
                                       out HostRange? r, out _));
        Assert.Equal(2, r!.Enumerate(4096, out _).Count);
    }

    [Fact]
    public void A_range_reads_back_the_way_it_would_be_written()
    {
        Assert.True(HostRange.TryParse("192.168.1.20-24, 192.168.1.40", Context,
                                       out HostRange? r, out _));
        Assert.Equal("192.168.1.20-192.168.1.24, 192.168.1.40", r!.ToString());
    }
}
