using System.Globalization;

using DbClone.UI.Services.Relationship;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// The camera is the reader's, not the document's: a picture drawn again for another cluster selection
/// opens where the one it replaced stood, so a chip changes what is drawn and never where the reader
/// is looking. The viewer carries it from the picture on screen to the address it serves next.
/// </summary>
public sealed class GraphCameraTests
{
    [Fact]
    public void The_document_s_answer_becomes_the_camera_it_states()
    {
        // Arrange — what the document's own script returns for a picture zoomed in and panned.
        const string statement = "{\"s\":1.75,\"x\":-120.5,\"y\":64}";

        // Act
        var read = GraphCamera.TryRead(statement, out var camera);

        // Assert
        read.Should().BeTrue();
        camera.Should().Be(new GraphCamera(1.75, -120.5, 64));
    }

    [Fact]
    public void A_statement_that_is_not_three_numbers_is_no_camera()
    {
        // Arrange — a document with no script answers null, and a payload of another shape is refused
        // whole rather than guessed at.
        string?[] statements =
        [
            null,
            string.Empty,
            "null",
            "undefined",
            "\"{\\\"s\\\":1}\"",
            "{\"s\":1,\"x\":0}",
            "{\"s\":\"1\",\"x\":0,\"y\":0}",
            "[1,0,0]",
        ];

        // Act + Assert
        foreach (var statement in statements)
        {
            GraphCamera.TryRead(statement, out _).Should().BeFalse($"'{statement}' states no camera");
        }
    }

    [Fact]
    public void The_query_states_the_numbers_the_script_reads_back()
    {
        // Arrange
        var camera = new GraphCamera(0.5, 16, -8.25);

        // Act
        var query = camera.Query();

        // Assert — the parameters extend the address's own version, and each is one plain number.
        query.Should().Be("&s=0.5&x=16&y=-8.25");
    }

    [Fact]
    public void The_query_never_writes_a_number_in_the_machine_s_own_culture()
    {
        // Arrange — a decimal comma would make '0,5' two parameters in the script's own reading.
        var camera = new GraphCamera(0.5, 16, -8.25);
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // Act
            var query = camera.Query();

            // Assert
            query.Should().Be("&s=0.5&x=16&y=-8.25");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
