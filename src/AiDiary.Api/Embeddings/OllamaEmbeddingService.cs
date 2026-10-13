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
