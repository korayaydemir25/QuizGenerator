using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuizGenerator.Services;

/// <summary>Ücretsiz katmanın GÜNLÜK kotası (günde ~1000 istek) tükendiğinde fırlatılır.
/// Bu, dakikalık limitten farklı - bekleyip tekrar denemenin bir anlamı yok, kota ancak
/// ertesi gün (Pasifik saatiyle gece yarısı) sıfırlanıyor.</summary>
public class DailyQuotaExhaustedException : Exception
{
    public DailyQuotaExhaustedException(string message) : base(message) { }
}

/// <summary>
/// Gemini'nin embedding modeliyle metinden "anlam vektörü" çıkarır.
/// "Benzer kelimelerle aynı anlama gelen sorular" tespiti bu servise dayanır -
/// düz metin karşılaştırması (aynı kelimeler) değil, anlamsal benzerlik ölçer.
///
/// Ücretsiz katmanın dakikada ~100 istek gibi sert bir sınırı var (token limiti ayrı,
/// çok daha cömert - asıl darboğaz bu istek sayısı sınırı). Bu yüzden 429 (TooManyRequests)
/// alındığında Google'ın döndürdüğü "retryDelay" süresini bekleyip otomatik tekrar dener.
/// </summary>
public class EmbeddingService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;

    public EmbeddingService(IHttpClientFactory factory, IConfiguration config)
    {
        _http = factory.CreateClient();
        _config = config;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "CHANGE_ME")
            throw new InvalidOperationException("Gemini:ApiKey ayarlanmamış.");

        var model = _config["Gemini:EmbeddingModel"] ?? "gemini-embedding-001";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:embedContent?key={apiKey}";

        var payload = new { content = new { parts = new[] { new { text } } } };
        var payloadJson = JsonSerializer.Serialize(payload);

        const int maxAttempts = 6;
        var fallbackDelaySeconds = 5.0;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Günlük kota mı, dakikalık mı? Google'ın quotaId alanında "PerDay" geçiyorsa
                // beklemenin anlamı yok - kota ancak ertesi gün sıfırlanıyor.
                if (body.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                    throw new DailyQuotaExhaustedException(
                        $"Günlük embedding kotası doldu (ücretsiz katman: günde ~1000 istek). Kalan kısım ancak yarın (Pasifik saatiyle gece yarısı sıfırlanır) tamamlanabilir, ya da faturalandırma açarsan hemen devam edebilirsin. Detay: {body}");

                if (attempt == maxAttempts)
                    throw new HttpRequestException($"Gemini embedding API hatası ({resp.StatusCode}) - {maxAttempts} denemeden sonra hâlâ rate limit: {body}");

                var wait = TimeSpan.FromSeconds(fallbackDelaySeconds);
                var match = Regex.Match(body, "\"retryDelay\"\\s*:\\s*\"(\\d+)s\"");
                if (match.Success && double.TryParse(match.Groups[1].Value, out var retrySeconds))
                    wait = TimeSpan.FromSeconds(retrySeconds);

                wait += TimeSpan.FromSeconds(Random.Shared.NextDouble() * 3);

                await Task.Delay(wait, ct);
                fallbackDelaySeconds = Math.Min(fallbackDelaySeconds * 1.7, 60);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Gemini embedding API hatası ({resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var values = doc.RootElement.GetProperty("embedding").GetProperty("values");

            var result = new float[values.GetArrayLength()];
            int i = 0;
            foreach (var v in values.EnumerateArray())
                result[i++] = v.GetSingle();

            return result;
        }

        throw new HttpRequestException("Gemini embedding API: beklenmeyen döngü sonu.");
    }

    public static string Serialize(float[] embedding) => JsonSerializer.Serialize(embedding);

    public static float[]? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<float[]>(json); }
        catch { return null; }
    }

    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}