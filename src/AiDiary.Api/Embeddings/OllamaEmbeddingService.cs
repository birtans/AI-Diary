using System.Text.Json.Serialization;
using AiDiary.Api.Configuration;

namespace AiDiary.Api.Embeddings;

public sealed class OllamaEmbeddingService(HttpClient http, DiaryOptions options)
{
    public const int Dimensions = 1024;

    /// <summary>bge-m3's full context. Ollama's default num_ctx is smaller and would truncate silently.</summary>
    public const int ContextTokens = 8192;

    private sealed record EmbedOptions([property: JsonPropertyName("num_ctx")] int NumCtx);

    private sealed record EmbedRequest(string Model, string[] Input, bool Truncate, EmbedOptions Options);

    private sealed record EmbedResponse(double[][]? Embeddings);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
        (await EmbedManyAsync([text], ct))[0];

    /// <summary>Embeds several inputs in one request; results are in input order.</summary>
    public async Task<float[][]> EmbedManyAsync(string[] texts, CancellationToken ct = default)
    {
        var request = new EmbedRequest(options.EmbedModel, texts, Truncate: true, new EmbedOptions(ContextTokens));
        var response = await http.PostAsJsonAsync(
            $"{options.OllamaUrl.TrimEnd('/')}/api/embed", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Ollama explains itself in the body, e.g. a 404 is almost always
            // `model "bge-m3" not found, try pulling it first`.
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Ollama /api/embed returned {(int)response.StatusCode} for model {options.EmbedModel}: {detail}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        var vectors = result?.Embeddings;
        if (vectors is null || vectors.Length != texts.Length)
        {
            throw new InvalidOperationException(
                $"Ollama returned {vectors?.Length ?? 0} embeddings for {texts.Length} inputs.");
        }
        return vectors.Select(Normalise).ToArray();
    }

    private float[] Normalise(double[] vector)
    {
        if (vector.Length != Dimensions)
        {
            throw new InvalidOperationException(
                $"Embedding model returned {vector.Length} dimensions; expected {Dimensions} for {options.EmbedModel}.");
        }

        // bge-m3 vectors are used with cosine similarity; normalise to unit length.
        var norm = Math.Sqrt(vector.Sum(x => x * x));
        if (norm <= 0)
        {
            return vector.Select(x => (float)x).ToArray();
        }
        return vector.Select(x => (float)(x / norm)).ToArray();
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await http.GetAsync($"{options.OllamaUrl.TrimEnd('/')}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
