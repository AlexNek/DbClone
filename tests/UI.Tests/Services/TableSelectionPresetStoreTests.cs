using DbClone.Application.Models;
using DbClone.UI.Services;

using FluentAssertions;

namespace UI.Tests.Services;

public sealed class TableSelectionPresetStoreTests : IDisposable
{
    private static readonly DatabaseIdentifier Database = new("profile-1", "testdb");

    private readonly string _storePath = Path.Combine(
        Path.GetTempPath(),
        "dbclone-tests",
        Guid.NewGuid().ToString("N"),
        "table-selection-presets.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task MissingFile_LoadsEmpty()
    {
        var store = new TableSelectionPresetStore(_storePath);

        var presets = await store.LoadPresetsAsync(Database);

        presets.Should().BeEmpty();
        (await store.GetLastUsedPresetIdAsync(Database)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAndReload_RoundTripsPresets()
    {
        var preset = TableSelectionPreset.Create(
            "Core Tables",
            [new TableId("public", "audit_log"), new TableId("analytics", "events")]);
        var store = new TableSelectionPresetStore(_storePath);
        await store.SavePresetAsync(Database, preset);

        var reloaded = new TableSelectionPresetStore(_storePath);
        var presets = await reloaded.LoadPresetsAsync(Database);

        var stored = presets.Should().ContainSingle().Which;
        stored.Id.Should().Be(preset.Id);
        stored.Name.Should().Be(preset.Name);
        stored.ExcludedTables.Should().BeEquivalentTo(preset.ExcludedTables);
    }

    [Fact]
    public async Task Save_WithSameId_UpdatesInsteadOfDuplicating()
    {
        var store = new TableSelectionPresetStore(_storePath);
        var preset = TableSelectionPreset.Create("Core Tables", [new TableId("public", "audit_log")]);
        await store.SavePresetAsync(Database, preset);
        await store.SavePresetAsync(Database, preset with { Name = "Renamed" });

        var presets = await store.LoadPresetsAsync(Database);

        presets.Should().ContainSingle().Which.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Delete_RemovesPresetAndClearsLastUsed()
    {
        var store = new TableSelectionPresetStore(_storePath);
        var preset = TableSelectionPreset.Create("Core Tables", []);
        await store.SavePresetAsync(Database, preset);
        await store.SetLastUsedPresetIdAsync(Database, preset.Id);

        await store.DeletePresetAsync(Database, preset.Id);

        (await store.LoadPresetsAsync(Database)).Should().BeEmpty();
        (await store.GetLastUsedPresetIdAsync(Database)).Should().BeNull();
    }

    [Fact]
    public async Task Rename_UpdatesName()
    {
        var store = new TableSelectionPresetStore(_storePath);
        var preset = TableSelectionPreset.Create("Core Tables", []);
        await store.SavePresetAsync(Database, preset);

        await store.RenamePresetAsync(Database, preset.Id, "Small Tables");

        var stored = await store.GetPresetAsync(Database, preset.Id);
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("Small Tables");
    }

    [Fact]
    public async Task LastUsed_IsPerDatabase()
    {
        var store = new TableSelectionPresetStore(_storePath);
        var otherDatabase = new DatabaseIdentifier("profile-2", "testdb");
        var preset = TableSelectionPreset.Create("Core Tables", []);
        await store.SavePresetAsync(Database, preset);
        await store.SetLastUsedPresetIdAsync(Database, preset.Id);

        (await store.GetLastUsedPresetIdAsync(Database)).Should().Be(preset.Id);
        (await store.GetLastUsedPresetIdAsync(otherDatabase)).Should().BeNull();
    }

    [Fact]
    public async Task DatabaseName_MatchesCaseInsensitive_ProfileIdDoesNot()
    {
        var store = new TableSelectionPresetStore(_storePath);
        var preset = TableSelectionPreset.Create("Core Tables", []);
        await store.SavePresetAsync(Database, preset);

        var sameDbOtherCase = new DatabaseIdentifier("profile-1", "TESTDB");
        var otherProfile = new DatabaseIdentifier("PROFILE-1", "testdb");

        (await store.LoadPresetsAsync(sameDbOtherCase)).Should().ContainSingle();
        (await store.LoadPresetsAsync(otherProfile)).Should().BeEmpty();
    }

    [Fact]
    public async Task CorruptFile_FallsBackToEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        await File.WriteAllTextAsync(_storePath, "{ this is not valid JSON ]");

        var store = new TableSelectionPresetStore(_storePath);

        (await store.LoadPresetsAsync(Database)).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadPresets_OrdersByName()
    {
        var store = new TableSelectionPresetStore(_storePath);
        await store.SavePresetAsync(Database, TableSelectionPreset.Create("zeta", []));
        await store.SavePresetAsync(Database, TableSelectionPreset.Create("Alpha", []));

        var presets = await store.LoadPresetsAsync(Database);

        presets.Select(p => p.Name).Should().ContainInOrder("Alpha", "zeta");
    }
}
