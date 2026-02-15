namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Interface for generating embeddings from text.
///     Implementations may use ONNX models, Ollama, or other backends.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    ///     Generate a float embedding vector from input text.
    ///     Returns null if the provider is not available.
    /// </summary>
    float[]? GenerateEmbedding(string text);

    /// <summary>
    ///     The dimension of the embedding vectors produced by this provider.
    /// </summary>
    int Dimension { get; }

    /// <summary>
    ///     Whether this provider is ready to generate embeddings
    ///     (model loaded, dependencies available).
    /// </summary>
    bool IsAvailable { get; }
}
