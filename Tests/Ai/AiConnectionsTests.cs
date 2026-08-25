using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The list of AI connections: choosing between them, and telling them apart.
///
/// The rules worth holding are the ones a user would notice going wrong — a key lost because a
/// connection was renamed, a picker whose entries all read the same, and a settings file written
/// before there were several turning into an empty list.
/// </summary>
public class AiConnectionsTests
{
    private static AiConnections Three()
    {
        var book = new AiConnections();
        book.Add(AiProvider.Gemini);
        book.Add(AiProvider.Anthropic);
        book.Add(AiProvider.OpenAiCompatible);
        return book;
    }

    ///
    ///Adding one selects it: adding a connection is how you say you want to use it, and an
    ///addition that left the old one in force would be a button that appears to do nothing.
    ///
    [Fact]
    public void Adding_one_selects_it()
    {
        var book = new AiConnections();
        AiConnection first = book.Add(AiProvider.Gemini);
        Assert.Equal(first.Id, book.Selected?.Id);

        AiConnection second = book.Add(AiProvider.Anthropic);
        Assert.Equal(second.Id, book.Selected?.Id);
    }

    ///
    ///Removing the selected one moves the selection rather than leaving it pointing at nothing.
    ///
    [Fact]
    public void Removing_the_selected_one_leaves_a_selection_behind()
    {
        AiConnections book = Three();
        string was = book.SelectedId;

        Assert.True(book.Remove(was));
        Assert.NotNull(book.Selected);
        Assert.NotEqual(was, book.Selected!.Id);
    }

    ///
    ///And the last one can go: a bench with no AI connection on it is the state this starts in,
    ///so it has to be reachable again.
    ///
    [Fact]
    public void The_last_one_can_go()
    {
        var book = new AiConnections();
        AiConnection only = book.Add();

        Assert.True(book.Remove(only.Id));
        Assert.Empty(book.Items);
        Assert.Null(book.Selected);
    }

    ///
    ///A stale id falls back to what is left rather than answering "none": a connection can be
    ///deleted in one window while another still holds its id, and the answer to "which one now"
    ///is the one that is there.
    ///
    [Fact]
    public void A_stale_selection_falls_back_to_what_is_left()
    {
        AiConnections book = Three();
        book.SelectedId = "a-connection-that-was-deleted";

        Assert.NotNull(book.Selected);
        Assert.Equal(book.Items[0].Id, book.Selected!.Id);
    }

    ///
    ///An unnamed connection is called after the model it reaches — which is what a person picking
    ///between two of them is picking between.
    ///
    [Fact]
    public void An_unnamed_connection_is_called_after_its_model()
    {
        AiConnections book = Three();

        Assert.Equal(["gemini-3.6-flash", "claude-sonnet-5", "gpt-4o-mini"], book.Labels());
    }

    ///
    ///Two on the same name are separated by the provider, and two on the same provider by a
    ///number. A picker whose entries cannot be told apart is a picker that cannot be used.
    ///
    [Fact]
    public void Two_that_answer_to_the_same_thing_are_told_apart()
    {
        var book = new AiConnections();
        book.Add(AiProvider.Gemini);
        book.Add(AiProvider.Gemini);
        book.Add(AiProvider.Anthropic);
        book.Items[2].Name = "fast";
        book.Items[1].Name = "fast";

        var labels = book.Labels();
        Assert.Equal("gemini-3.6-flash", labels[0]);
        Assert.Equal("fast · Google Gemini", labels[1]);
        Assert.Equal("fast · Anthropic Claude", labels[2]);
    }

    [Fact]
    public void Two_that_are_the_same_all_the_way_down_are_numbered()
    {
        var book = new AiConnections();
        book.Add(AiProvider.Gemini);
        book.Add(AiProvider.Gemini);

        Assert.Equal(["gemini-3.6-flash #1", "gemini-3.6-flash #2"], book.Labels());
    }

    ///
    ///The settings file of anyone who had this app before there were several holds one connection.
    ///Reading it as the first entry — with an id generated then — is what stops the change costing
    ///them the connection they already had.
    ///
    [Fact]
    public void A_single_connection_becomes_the_first_of_a_list()
    {
        var single = new AiConnection { Provider = AiProvider.Anthropic, Model = "claude-opus-5" };
        AiConnections book = AiConnections.From(single);

        Assert.Single(book.Items);
        Assert.Equal("claude-opus-5", book.Selected!.EffectiveModel);
        Assert.Equal(book.Items[0].Id, book.SelectedId);
        Assert.NotEmpty(book.Items[0].Id);
    }

    [Fact]
    public void Nothing_stored_is_an_empty_list_rather_than_a_made_up_one()
    {
        AiConnections book = AiConnections.From(null);

        Assert.Empty(book.Items);
        Assert.Null(book.Selected);
    }

    ///
    ///A copy can be edited without touching the original — which is what lets the settings store
    ///swap a whole snapshot in rather than letting a request see half an edit.
    ///
    [Fact]
    public void A_clone_is_a_copy_all_the_way_down()
    {
        AiConnections book = Three();
        AiConnections copy = book.Clone();
        copy.Items[0].Model = "something-else";

        Assert.NotEqual(copy.Items[0].Model, book.Items[0].Model);
        Assert.Equal(book.SelectedId, copy.SelectedId);
    }
}
