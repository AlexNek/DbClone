using DbClone.Application.Models;
using DbClone.Application.TableFilter;

using FluentAssertions;

namespace Application.Tests;

public class TableSelectionPresetNameValidatorTests
{
    private readonly TableSelectionPresetNameValidator _sut = new();

    [Fact]
    public void Validate_ValidName_ReturnsNull()
    {
        var result = _sut.Validate("Core Tables", []);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyName_IsRejected(string name)
    {
        var result = _sut.Validate(name, []);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Validate_NameExceedingMaxLength_IsRejected()
    {
        var name = new string('x', TableSelectionPresetNameValidator.MaxLength + 1);

        var result = _sut.Validate(name, []);

        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData("All Tables")]
    [InlineData("all tables")]
    public void Validate_ReservedName_IsRejected(string name)
    {
        var result = _sut.Validate(name, []);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Validate_CaseInsensitiveDuplicate_IsRejected()
    {
        var existing = new[] { TableSelectionPreset.Create("Core Tables", []) };

        var result = _sut.Validate("core tables", existing);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Validate_OwnNameOnRename_IsAllowed()
    {
        var existing = new[] { TableSelectionPreset.Create("Core Tables", []) };

        var result = _sut.Validate(
            "Core Tables",
            existing,
            excludePresetId: existing[0].Id);

        result.Should().BeNull();
    }

    [Fact]
    public void Validate_TrimsWhitespace()
    {
        var existing = new[] { TableSelectionPreset.Create("Core Tables", []) };

        var result = _sut.Validate("  Other Name  ", existing);

        result.Should().BeNull();
    }
}
