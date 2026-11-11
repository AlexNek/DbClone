using System.Text.Json;

using DbClone.UI.Settings;

using FluentAssertions;

namespace UI.Tests.Settings;

/// <summary>
/// Unit tests for <see cref="StoreRestoreOptions"/> — default values and
/// whole-object replacement on <see cref="UserSettings"/>.
/// </summary>
public class StoreRestoreOptionsTests
{
    [Fact]
    public void Defaults_are_conservative()
    {
        var options = new StoreRestoreOptions();

        options.ChunkBytes.Should().Be(8_388_608);
        options.RowsPerChunk.Should().Be(50_000);
        options.IncludeData.Should().BeTrue();
        options.WriteSummaryMarkdown.Should().BeTrue();
        options.SnapshotConsistency.Should().Be("consistent");
        options.UnsupportedFeaturePolicy.Should().Be("fail");
        options.CatalogSubfolderPattern.Should().Be("{Connection}_{timestamp}");
    }

    [Fact]
    public void SqlScripts_defaults_on_because_handing_the_catalog_to_any_client_is_the_point()
    {
        // The product option defaults off — the pass is asked for — while the user-facing preference
        // defaults on: a user who never opens the settings still gets the files (FR10).
        new StoreRestoreOptions().SqlScripts.Should().BeTrue();
    }

    [Fact]
    public void SqlScripts_survives_the_round_trip_and_an_older_file_leaves_it_on()
    {
        // Arrange — a settings file written before the option existed.
        const string olderFile = """{ "StoreRestore": { "ChunkBytes": 8388608 } }""";

        // Act
        var fromOlderFile = JsonSerializer.Deserialize<UserSettings>(olderFile);

        // Assert — an upgrade must not silently turn the new files off for a user who already has settings.
        fromOlderFile!.StoreRestore.SqlScripts.Should().BeTrue();

        // Act — and an explicit choice travels both ways.
        var saved = JsonSerializer.Serialize(new StoreRestoreOptions { SqlScripts = false });
        var reloaded = JsonSerializer.Deserialize<StoreRestoreOptions>(saved);

        // Assert
        reloaded!.SqlScripts.Should().BeFalse();
    }

    [Fact]
    public void DefaultCatalogDirectory_is_under_MyDocuments()
    {
        var options = new StoreRestoreOptions();

        options.DefaultCatalogDirectory.Should().Contain("DbClone Catalogs");
    }

    [Fact]
    public void UserSettings_StoreRestore_property_triggers_PropertyChanged()
    {
        var settings = new UserSettings();
        var raised = new List<string>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        settings.StoreRestore = new StoreRestoreOptions { ChunkBytes = 16_777_216 };

        raised.Should().Contain(nameof(UserSettings.StoreRestore));
    }

    [Fact]
    public void UserSettings_StoreRestore_replacement_is_independent()
    {
        var settings = new UserSettings();
        var original = settings.StoreRestore;

        settings.StoreRestore = new StoreRestoreOptions { ChunkBytes = 1 };

        settings.StoreRestore.ChunkBytes.Should().Be(1);
        original.ChunkBytes.Should().Be(8_388_608); // unchanged
    }
}
