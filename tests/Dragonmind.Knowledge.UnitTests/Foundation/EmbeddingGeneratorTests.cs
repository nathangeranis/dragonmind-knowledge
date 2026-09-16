using Dragonmind.Core.AI;
using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Infrastructure.Embeddings;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Moq;

using DomainEmbedding = Dragonmind.Knowledge.Domain.ValueObjects.Embedding;
using IDragonmindEmbeddingGenerator = Dragonmind.Core.AI.IEmbeddingGenerator;

namespace Dragonmind.Knowledge.UnitTests.Foundation;

/// <summary>
/// Unit tests for <see cref="ExtensionsAIEmbeddingGenerator"/> (the Microsoft.Extensions.AI-backed
/// adapter with layered dimensionality enforcement) and the <see cref="EmbeddingService"/> adapter.
/// </summary>
public class EmbeddingGeneratorTests
{
    // -----------------------------------------------------------------------
    // ExtensionsAIEmbeddingGenerator tests
    // -----------------------------------------------------------------------

    private static ExtensionsAIEmbeddingGenerator CreateGenerator(
        Mock<IEmbeddingGenerator<string, Embedding<float>>> mockInner,
        int dimensions)
    {
        return new ExtensionsAIEmbeddingGenerator(
            mockInner.Object,
            dimensions,
            new Mock<ILogger<ExtensionsAIEmbeddingGenerator>>().Object);
    }

    private static void SetupInner(
        Mock<IEmbeddingGenerator<string, Embedding<float>>> mockInner,
        float[] returnedVector)
    {
        mockInner
            .Setup(g => g.GenerateAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<EmbeddingGenerationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>(
                new[] { new Embedding<float>(returnedVector) }));
    }

    private static double L2Norm(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector) sum += (double)v * v;
        return Math.Sqrt(sum);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_NullText_ThrowsArgumentNullException()
    {
        // Arrange
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        var generator = CreateGenerator(mockInner, 5);

        // Act & Assert — ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => generator.GenerateEmbeddingAsync(null!));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WhitespaceText_ThrowsArgumentException()
    {
        // Arrange
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        var generator = CreateGenerator(mockInner, 5);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => generator.GenerateEmbeddingAsync("   "));
    }

    [Fact]
    public void Constructor_NonPositiveDimensions_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateGenerator(mockInner, 0));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ExpectedWidthUnitVector_ReturnsUnchanged()
    {
        // Arrange — already-normalized vector at the expected width passes through as-is
        var unitVector = new float[] { 1f, 0f, 0f, 0f, 0f };
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        SetupInner(mockInner, unitVector);
        var generator = CreateGenerator(mockInner, 5);

        // Act
        var result = await generator.GenerateEmbeddingAsync("The Checkout Service escalates after three retries.");

        // Assert
        Assert.Equal(unitVector, result);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_PassesConfiguredDimensionsToProvider()
    {
        // Arrange
        EmbeddingGenerationOptions? capturedOptions = null;
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        mockInner
            .Setup(g => g.GenerateAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<EmbeddingGenerationOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, EmbeddingGenerationOptions?, CancellationToken>(
                (_, options, _) => capturedOptions = options)
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>(
                new[] { new Embedding<float>(new float[] { 1f, 0f, 0f }) }));
        var generator = CreateGenerator(mockInner, 3);

        // Act
        await generator.GenerateEmbeddingAsync("Test text.");

        // Assert — the provider's "dimensions" parameter is requested from the provider
        Assert.NotNull(capturedOptions);
        Assert.Equal(3, capturedOptions!.Dimensions);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_NonNormalizedVector_IsRenormalized()
    {
        // Arrange — provider returns a non-unit vector; adapter must L2-normalize
        var rawVector = new float[] { 3f, 4f, 0f };
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        SetupInner(mockInner, rawVector);
        var generator = CreateGenerator(mockInner, 3);

        // Act
        var result = await generator.GenerateEmbeddingAsync("Test text.");

        // Assert — unit norm, direction preserved (3-4-5 triangle → 0.6, 0.8)
        Assert.Equal(1.0, L2Norm(result), precision: 6);
        Assert.Equal(0.6f, result[0], precision: 6);
        Assert.Equal(0.8f, result[1], precision: 6);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ProviderIgnoresDimensions_TruncatesAndRenormalizes()
    {
        // Arrange — provider returns 6 dims when 3 were requested (some providers do not honour
        // the "dimensions" parameter). MRL truncation keeps the prefix, then renormalizes.
        var oversized = new float[] { 3f, 4f, 0f, 9f, 9f, 9f };
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        SetupInner(mockInner, oversized);
        var generator = CreateGenerator(mockInner, 3);

        // Act
        var result = await generator.GenerateEmbeddingAsync("Test text.");

        // Assert — exactly 3 dims, prefix direction preserved, unit norm
        Assert.Equal(3, result.Length);
        Assert.Equal(1.0, L2Norm(result), precision: 6);
        Assert.Equal(0.6f, result[0], precision: 6);
        Assert.Equal(0.8f, result[1], precision: 6);
        Assert.Equal(0f, result[2], precision: 6);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ShorterVectorThanExpected_ThrowsInvalidOperationException()
    {
        // Arrange — a too-short vector cannot be repaired and must never reach Postgres
        var tooShort = new float[] { 0.1f, 0.2f };
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        SetupInner(mockInner, tooShort);
        var generator = CreateGenerator(mockInner, 5);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateEmbeddingAsync("Test text."));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_EmptyProviderResult_ThrowsInvalidOperationException()
    {
        // Arrange
        var mockInner = new Mock<IEmbeddingGenerator<string, Embedding<float>>>();
        mockInner
            .Setup(g => g.GenerateAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<EmbeddingGenerationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>());
        var generator = CreateGenerator(mockInner, 5);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateEmbeddingAsync("Test text."));
    }

    // -----------------------------------------------------------------------
    // EmbeddingService adapter tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EmbeddingService_WithGenerator_DelegatesToGenerator()
    {
        // Arrange
        var expectedVector = new float[] { 0.5f, 0.6f, 0.7f };

        var mockGenerator = new Mock<IDragonmindEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateEmbeddingAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedVector);

        var service = new EmbeddingService(mockGenerator.Object);

        // Act
        var embedding = await service.GenerateEmbeddingAsync("The Payments DB replicates every five seconds.");

        // Assert
        Assert.NotNull(embedding);
        Assert.Equal(expectedVector.Length, embedding.Dimensions);

        mockGenerator.Verify(
            g => g.GenerateEmbeddingAsync(
                "The Payments DB replicates every five seconds.",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EmbeddingService_WithGenerator_NullText_ThrowsArgumentNullException()
    {
        // Arrange
        var mockGenerator = new Mock<IDragonmindEmbeddingGenerator>();
        var service = new EmbeddingService(mockGenerator.Object);

        // Act & Assert — ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.GenerateEmbeddingAsync(null!));

        // The generator should never be called with invalid input
        mockGenerator.Verify(
            g => g.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EmbeddingService_WithGenerator_ReturnsEmbeddingValueObject()
    {
        // Arrange
        var vector = new float[] { 0.1f, 0.2f, 0.3f };

        var mockGenerator = new Mock<IDragonmindEmbeddingGenerator>();
        mockGenerator
            .Setup(g => g.GenerateEmbeddingAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(vector);

        var service = new EmbeddingService(mockGenerator.Object);

        // Act
        var embedding = await service.GenerateEmbeddingAsync("Test text.");

        // Assert — verify the result is a proper Embedding value object
        Assert.NotNull(embedding);
        Assert.IsType<DomainEmbedding>(embedding);
        Assert.Equal(3, embedding.Dimensions);
        Assert.Equal(vector, embedding.Vector);
    }
}
