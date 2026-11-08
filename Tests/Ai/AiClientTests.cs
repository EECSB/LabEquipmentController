using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// What <see cref="AiClient"/> says before it sends anything. Core serves both builds, so its
/// words have to be true in both.
/// </summary>
public class AiClientTests
{
    [Fact]
    public async Task A_missing_key_names_the_box_both_builds_share_and_no_menu()
    {
        var ex = await Assert.ThrowsAsync<AiException>(() => new AiClient().CompleteAsync(
            new AiConnection(), apiKey: "", "instruction", AiPayload.FromText("text"),
            JsonNode.Parse("""{"type":"object"}""")!));

        // The desktop's form and the web's dialog are both titled AI Connection.
        Assert.Contains("AI Connection", ex.Message);

        // Reached from Tools on the desktop and from the gear on the web, so a menu path here is
        // wrong in one build. It said Help ▸ AI Connection, which was wrong in both.
        Assert.DoesNotContain("▸", ex.Message);
        Assert.DoesNotContain("Help", ex.Message);
    }
}
