using System.IO;

using DbClone.UI;
using DbClone.UI.Services.Relationship;

using FluentAssertions;

namespace UI.Tests.CatalogGraph;

/// <summary>
/// D10: a document that has to leave memory lands on one fixed path under the user's local folder. The
/// rule names no origin, no role and no moment, so ten views leave one file and never ten.
/// </summary>
public sealed class RelationshipViewFileTests
{
    [Fact]
    public void The_path_is_one_fixed_name_under_the_user_s_local_folder()
    {
        // Arrange
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolder.Name,
            "view",
            "relationship.html");

        // Assert
        RelationshipViewFile.Path.Should().Be(expected);
    }

    [Fact]
    public void Nothing_about_what_is_on_screen_reaches_the_name()
    {
        // Arrange — the same path backs every document, whichever side it describes (D10).
        var path = RelationshipViewFile.Path;

        // Assert — no database, no folder, no role and no timestamp is ever part of it.
        Path.GetFileName(path).Should().Be("relationship.html");
        Path.GetFileName(Path.GetDirectoryName(path)!).Should().Be("view");
        Path.GetFileName(path).Should().NotMatchRegex("[0-9]{4,}", "a stamped name is what D10 forbids");
    }
}
