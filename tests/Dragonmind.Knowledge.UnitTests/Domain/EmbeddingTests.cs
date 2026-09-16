using Dragonmind.Knowledge.Domain.ValueObjects;

namespace Dragonmind.Knowledge.UnitTests.Domain;

/// <summary>
/// Unit tests for the <see cref="Embedding"/> value object.
/// Tests vector validation and dimension handling.
/// </summary>
public class EmbeddingTests
{
    [Fact]
    public void Create_ValidVector_CreatesSuccessfully()
    {
        // Arrange
        var vector = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };

        // Act
        var embedding = Embedding.Create(vector);

        // Assert
        Assert.NotNull(embedding);
        Assert.Equal(vector, embedding.Vector);
        Assert.Equal(5, embedding.Dimensions);
    }

    [Fact]
    public void Create_EmptyVector_ThrowsArgumentException()
    {
        // Arrange
        var vector = Array.Empty<float>();

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => Embedding.Create(vector));
        Assert.Contains("Embedding must have at least", exception.Message);
    }

    [Fact]
    public void Create_NullVector_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => Embedding.Create(null!));
    }

    [Fact]
    public void ValueEquality_SameVector_AreEqual()
    {
        // Arrange
        var vector = new float[] { 0.1f, 0.2f, 0.3f };
        var embedding1 = Embedding.Create(vector);
        var embedding2 = Embedding.Create(vector);

        // Act & Assert
        Assert.Equal(embedding1, embedding2);
        Assert.True(embedding1 == embedding2);
        Assert.False(embedding1 != embedding2);
    }
}
