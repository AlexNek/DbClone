using System.IO;

using DbClone.UI.Models;

using FluentAssertions;

namespace UI.Tests;

/// <summary>
/// The saved-connection file is written with Windows DPAPI and read back on the next start, so the
/// tests drive the real store over a temporary file: nothing here touches the user's own profile.
/// </summary>
public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "dbclone-connections-" + Guid.NewGuid().ToString("n"));

    private string StorePath => Path.Combine(_folder, "connections.json");

    [Fact]
    public void SaveThenReload_RoundTripsEveryFieldAndDecryptsThePassword()
    {
        // Arrange — a password built from the characters that break naive quoting and JSON handling.
        var connection = Connection(password: "p@ss#1! & space");
        new ConnectionStore(StorePath).Save(connection);

        // Act — a second store reads the file the way the next application start does.
        var reloaded = new ConnectionStore(StorePath).GetById(connection.Id);

        // Assert
        reloaded.Should().NotBeNull();
        reloaded!.Host.Should().Be("test.example.com");
        reloaded.Port.Should().Be("5432");
        reloaded.DatabaseName.Should().Be("testdb");
        reloaded.Username.Should().Be("test_user");
        reloaded.SslMode.Should().Be("Prefer");
        reloaded.Engine.Should().Be("test.engine");
        reloaded.Password.Should().Be("p@ss#1! & space");
    }

    [Fact]
    public void SavedFile_HoldsThePasswordOnlyAsCipherText()
    {
        // Arrange
        var connection = Connection(password: "p@ss#1! & space");
        new ConnectionStore(StorePath).Save(connection);

        // Act
        var json = File.ReadAllText(StorePath);

        // Assert
        json.Should().NotContain(connection.Password, "the password must never reach the file");
        json.Should().Contain("EncryptedPassword");
    }

    [Fact]
    public void Save_ReplacesTheEntryWithTheSameId()
    {
        // Arrange
        var store = new ConnectionStore(StorePath);
        var connection = Connection(password: "first");
        store.Save(connection);

        // Act
        var updated = connection.Clone();
        updated.Password = "second";
        store.Save(updated);

        var entries = new ConnectionStore(StorePath).GetAll();

        // Assert
        entries.Should().ContainSingle();
        entries[0].Password.Should().Be("second");
    }

    [Fact]
    public void Delete_RemovesTheEntryFromMemoryAndFromTheFile()
    {
        // Arrange
        var store = new ConnectionStore(StorePath);
        var connection = Connection(password: "secret");
        store.Save(connection);

        // Act
        store.Delete(connection.Id);

        // Assert
        store.GetAll().Should().BeEmpty();
        new ConnectionStore(StorePath).GetAll().Should().BeEmpty();
    }

    [Fact]
    public void CorruptFile_IsQuarantinedAndTheStoreStartsEmpty()
    {
        // Arrange — a half-written or unreadable file must not stop the application from starting.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(StorePath, "{ this is not a connection list");

        // Act
        var store = new ConnectionStore(StorePath);

        // Assert
        store.GetAll().Should().BeEmpty();
        Directory.EnumerateFiles(_folder, "connections.json.corrupt-*.bak").Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static SavedConnection Connection(string password) =>
        new()
            {
                Id = "test-connection",
                Name = "Test database",
                Host = "test.example.com",
                Port = "5432",
                DatabaseName = "testdb",
                Username = "test_user",
                Password = password,
                SslMode = "Prefer",
                Engine = "test.engine",
                Folder = "Local"
            };
}
