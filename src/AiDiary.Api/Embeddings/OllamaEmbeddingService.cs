using System.Net.Http.Json;
using AiDiary.Api.Configuration;

namespace AiDiary.Api.Embeddings;

public sealed class OllamaEmbeddingService(HttpClient http, DiaryOptions options)
{
    public const int Dimensions = 1024;

    private sealed record EmbedRequest(string Model, string[] Input, bool Truncate);

    private sealed record EmbedResponse(double[][]? Embeddings);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new EmbedRequest(options.EmbedModel, [text], Truncate: true);
        var response = await http.PostAsJsonAsync(
            $"{options.OllamaUrl.TrimEnd('/')}/api/embed", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        var vector = result?.Embeddings?.FirstOrDefault()
            ?? throw new InvalidOperationException("Ollama returned no embedding.");

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
