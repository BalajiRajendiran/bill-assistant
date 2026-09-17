using Microsoft.Extensions.AI;

namespace BillAssistant.Core.Tests.Fakes;

/// <summary>
/// Deterministic stand-in for a real embedding model.
/// </summary>
/// <remarks>
/// Hashes each input into a small fixed-size vector. Similar strings do not produce similar vectors,
/// which is fine: these tests check that the right values are stored, passed and returned, not that
/// retrieval is semantically good - that is the model's job, not this code's.
/// </remarks>
public sealed class FakeEmbeddingGenerator(int dimensions = 8) : IEmbeddingGenerator<string, Embedding<float>>
{
    public int Dimensions { get; } = dimensions;

    public List<string> ReceivedInputs { get; } = [];

    public int BatchCount { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values.ToList();
        ReceivedInputs.AddRange(inputs);
        BatchCount++;

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            inputs.Select(v => new Embedding<float>(Vector(v, Dimensions)))));
    }

    public static ReadOnlyMemory<float> Vector(string value, int dimensions)
    {
        var vector = new float[dimensions];
        var hash = 17;

        foreach (var c in value)
        {
            hash = (hash * 31) + c;
            vector[Math.Abs(hash) % dimensions] += 1f;
        }

        // Normalise so cosine similarity behaves.
        var length = MathF.Sqrt(vector.Sum(v => v * v));
        if (length > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= length;
            }
        }

        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
