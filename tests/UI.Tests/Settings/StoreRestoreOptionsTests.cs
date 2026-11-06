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
        options.SnapshotConsistency.Should().Be("consistent");
        options.UnsupportedFeaturePolicy.Should().Be("fail");
        options.PackAfterStore.Should().BeTrue();
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
