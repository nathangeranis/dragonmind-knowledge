using Dragonmind.Knowledge.Domain.ValueObjects;

namespace Dragonmind.Knowledge.UnitTests.Domain;

/// <summary>
/// Unit tests for the <see cref="DocumentContent"/> value object.
/// Tests content validation and length limits.
/// </summary>
public class DocumentContentTests
{
    [Fact]
    public void Create_ValidContent_CreatesSuccessfully()
    {
        // Arrange
        var text = "The Checkout Service retries failed payments up to three times before escalating.";

        // Act
        var content = DocumentContent.Create(text);

        // Assert
        Assert.NotNull(content);
        Assert.Equal(text, content.Value);
    }

    [Fact]
    public void Create_MaxLengthContent_CreatesSuccessfully()
    {
        // Arrange
        var text = new string('A', DocumentContent.MaxLength);

        // Act
        var content = DocumentContent.Create(text);

        // Assert
        Assert.NotNull(content);
        Assert.Equal(text, content.Value);
        Assert.Equal(DocumentContent.MaxLength, content.Value.Length);
    }

    [Fact]
    public void Create_ExceedsMaxLength_ThrowsArgumentException()
    {
        // Arrange
        var text = new string('A', DocumentContent.MaxLength + 1);

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => DocumentContent.Create(text));
        Assert.Contains("cannot exceed", exception.Message);
        Assert.Contains(DocumentContent.MaxLength.ToString(), exception.Message);
    }

    [Fact]
    public void Create_NullOrBlankText_ThrowsArgumentException()
    {
        // Act & Assert
        // Null content throws ArgumentNullException (not ArgumentException)
        Assert.Throws<ArgumentNullException>(() => DocumentContent.Create(null!));
        // Empty/whitespace content throws ArgumentException
        Assert.Throws<ArgumentException>(() => DocumentContent.Create(""));
        Assert.Throws<ArgumentException>(() => DocumentContent.Create("   "));
    }

    [Fact]
    public void ValueEquality_SameText_AreEqual()
    {
        // Arrange
        var text = "The Payments DB is the source of truth for settled transactions.";
        var content1 = DocumentContent.Create(text);
        var content2 = DocumentContent.Create(text);

        // Act & Assert
        Assert.Equal(content1, content2);
        Assert.True(content1 == content2);
        Assert.False(content1 != content2);
    }
}
