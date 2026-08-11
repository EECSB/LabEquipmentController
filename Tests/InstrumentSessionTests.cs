using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class CommandHistoryTests
{
    [Fact]
    public void Empty_history_recalls_nothing()
    {
        var h = new CommandHistory();
        Assert.Equal("", h.Recall(-1));
        Assert.Equal("", h.Recall(+1));
    }

    [Fact]
    public void Up_recalls_the_most_recent_command()
    {
        var h = new CommandHistory();
        h.Add("*IDN?");
        h.Add(":RUN");
        Assert.Equal(":RUN", h.Recall(-1));
    }

    [Fact]
    public void Up_repeatedly_walks_back_through_history()
    {
        var h = new CommandHistory();
        h.Add("one");
        h.Add("two");
        h.Add("three");
        Assert.Equal("three", h.Recall(-1));
        Assert.Equal("two", h.Recall(-1));
        Assert.Equal("one", h.Recall(-1));
    }

    [Fact]
    public void Up_stops_at_the_oldest_command()
    {
        var h = new CommandHistory();
        h.Add("one");
        h.Recall(-1);
        Assert.Equal("one", h.Recall(-1));
        Assert.Equal("one", h.Recall(-1));
    }

    [Fact]
    public void Down_past_the_newest_command_clears_the_input()
    {
        var h = new CommandHistory();
        h.Add("one");
        h.Add("two");
        Assert.Equal("two", h.Recall(-1));
        Assert.Equal("", h.Recall(+1));
    }

    [Fact]
    public void Adding_a_command_resets_the_recall_position()
    {
        var h = new CommandHistory();
        h.Add("one");
        h.Add("two");
        h.Recall(-1);
        h.Recall(-1);          // walked back to "one"
        h.Add("three");        // typing a new command starts again from the end
        Assert.Equal("three", h.Recall(-1));
    }

    [Fact]
    public void Blank_commands_are_not_recorded()
    {
        var h = new CommandHistory();
        h.Add("");
        h.Add("   ");
        Assert.Equal(0, h.Count);
    }
}

public class InstrumentSessionTests
{
    private static InstrumentSession Session(string identity, string host = "192.168.1.17")
    {
        var client = new FakeInstrumentClient { Host = host, Description = "VXI-11 (inst0)" };
        return new InstrumentSession(client, identity, InstrumentProfile.ForIdentity(identity),
                                     timeoutMs: 3000);
    }

    [Fact]
    public void Title_names_the_model_and_address()
    {
        var s = Session("RIGOL TECHNOLOGIES,DS2202A,DS2A1234,00.03.05");
        Assert.Equal("DS2202A (192.168.1.17)", s.Title);
    }

    [Fact]
    public void Title_falls_back_to_the_address_when_the_instrument_never_identified()
    {
        var s = Session("");
        Assert.Equal("Instrument (192.168.1.17)", s.Title);
    }

    [Fact]
    public void Description_carries_address_transport_type_and_identity()
    {
        var s = Session("Siglent Technologies,SDM3065X,SDM1234,1.01");
        Assert.Contains("192.168.1.17", s.Description);
        Assert.Contains("VXI-11 (inst0)", s.Description);
        Assert.Contains("Multimeter (SDM3065X)", s.Description);
        Assert.Contains("SDM3065X", s.Description);
    }

    [Fact]
    public void Profile_follows_the_identity()
    {
        var s = Session("Siglent Technologies,SDG2042X,SDG1234,1.01");
        Assert.Equal(InstrumentFamily.SiglentGenerator,
                     InstrumentProfile.FamilyForIdentity(s.Identity));
        Assert.Contains(s.Profile.Commands, c => c.Command == "C1:OUTP ON");
    }

    [Fact]
    public async Task Closing_hands_the_instrument_back_to_its_front_panel()
    {
        var client = new FakeInstrumentClient();
        var s = new InstrumentSession(client, "", InstrumentProfile.ForIdentity(null), 3000);

        await s.CloseAsync();

        Assert.True(client.ReturnedToLocal);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Closing_still_drops_the_link_when_the_instrument_has_gone_away()
    {
        // An instrument that has been switched off must not stop its session from closing.
        var client = new FakeInstrumentClient { FailReturnToLocal = true };
        var s = new InstrumentSession(client, "", InstrumentProfile.ForIdentity(null), 3000);

        await s.CloseAsync();

        Assert.False(client.ReturnedToLocal);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Each_session_keeps_its_own_history()
    {
        var a = Session("RIGOL TECHNOLOGIES,DS2202A,x,y", "192.168.1.17");
        var b = Session("Siglent Technologies,SDG2042X,x,y", "192.168.1.19");

        a.History.Add(":RUN");

        Assert.Equal(1, a.History.Count);
        Assert.Equal(0, b.History.Count);
    }
}

public class SessionRegistryTests
{
    private static InstrumentSession At(string host)
    {
        var client = new FakeInstrumentClient { Host = host };
        return new InstrumentSession(client, "", InstrumentProfile.ForIdentity(null), 3000);
    }

    [Fact]
    public void Finds_an_open_session_by_address()
    {
        var reg = new SessionRegistry();
        InstrumentSession rigol = At("192.168.1.17");
        reg.Add(rigol);
        reg.Add(At("192.168.1.19"));

        Assert.Same(rigol, reg.FindByHost("192.168.1.17"));
        Assert.Equal(2, reg.Count);
    }

    [Fact]
    public void Lookup_ignores_hostname_case()
    {
        var reg = new SessionRegistry();
        InstrumentSession s = At("Scope.lab.local");
        reg.Add(s);

        Assert.Same(s, reg.FindByHost("scope.LAB.local"));
    }

    [Theory]
    [InlineData("192.168.1.99")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_missing_addresses_find_nothing(string? host)
    {
        var reg = new SessionRegistry();
        reg.Add(At("192.168.1.17"));

        Assert.Null(reg.FindByHost(host));
    }

    [Fact]
    public void A_removed_session_is_no_longer_found()
    {
        // Reconnecting to an address must be possible once its console has been closed.
        var reg = new SessionRegistry();
        InstrumentSession s = At("192.168.1.17");
        reg.Add(s);

        Assert.True(reg.Remove(s));
        Assert.Null(reg.FindByHost("192.168.1.17"));
        Assert.Equal(0, reg.Count);
    }
}
