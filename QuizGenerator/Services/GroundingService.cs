using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuizGenerator.Prompts;

namespace QuizGenerator.Services;

/// <summary>
/// İçerik adını GERÇEKTEN web'de aratıp (Gemini'nin Google Search grounding aracı) doğrulanmış
/// bir karakter/ilişki notu çıkarır. Toplu üretimde her içerik için bir kez çağrılır ve sonucu
/// üretim prompt'una KESİN KAYNAK (referenceNote) olarak enjekte edilir — böylece hafızadan isim
/// uydurma (bu projenin bir numaralı kalite sorunu) grounding ile engellenir.
///
/// Grounding başarısız olursa (araç desteklenmiyor, kota, ağ) null döner ve üretim grounding'siz
/// devam eder — yani en kötü ihtimalde eski davranışa düşer, iş durmaz.
/// </summary>
public class GroundingService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<GroundingService> _logger;

    // Aynı içerik için tekrar tekrar aramamak adına süreç-içi (in-memory) önbellek.
    private static readonly ConcurrentDictionary<string, GroundingResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GroundingService(IHttpClientFactory factory, IConfiguration config, ILogger<GroundingService> logger)
    {
        _http = factory.CreateClient();
        _config = config;
        _logger = logger;
    }

    /// <param name="contentTypeHint">"movie"/"series" — sadece prompt'a ipucu, kesin değil.</param>
    public async Task<GroundingResult> GetGroundingAsync(string contentName, string contentTypeHint, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(contentName, out var cached))
            return cached;

        var result = await FetchAsync(contentName, contentTypeHint, ct);
        _cache[contentName] = result;
        return result;
    }

    private async Task<GroundingResult> FetchAsync(string contentName, string contentTypeHint, CancellationToken ct)
    {
        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "CHANGE_ME")
            return GroundingResult.Empty("Gemini:ApiKey ayarlı değil, grounding atlandı.");

        var model = _config["Gemini:Model"] ?? "gemini-3.5-flash";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var prompt = PromptTemplates.BuildGroundingPrompt(contentName, contentTypeHint);
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            tools = new[] { new { google_search = new { } } },
            generationConfig = new { temperature = 0.2 }
        };
        var payloadJson = JsonSerializer.Serialize(payload);

        const int maxAttempts = 4;
        var fallbackDelaySeconds = 5.0;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };
                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (body.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                        throw new DailyQuotaExhaustedException(
                            $"Günlük Gemini kotası doldu (grounding araması sırasında). Detay: {body}");

                    if (attempt == maxAttempts)
                        return GroundingResult.Empty($"Grounding rate limit ({maxAttempts} deneme): {body}");

                    var wait = TimeSpan.FromSeconds(fallbackDelaySeconds);
                    var m = Regex.Match(body, "\"retryDelay\"\\s*:\\s*\"(\\d+)s\"");
                    if (m.Success && double.TryParse(m.Groups[1].Value, out var rs)) wait = TimeSpan.FromSeconds(rs);
                    wait += TimeSpan.FromSeconds(Random.Shared.NextDouble() * 3);
                    await Task.Delay(wait, ct);
                    fallbackDelaySeconds = Math.Min(fallbackDelaySeconds * 1.7, 60);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    // Model google_search aracını desteklemiyorsa 400 döner — grounding'siz devam et.
                    _logger.LogWarning("Grounding başarısız ({Status}) '{Content}': {Body}", resp.StatusCode, contentName, Trunc(body));
                    return GroundingResult.Empty($"Grounding HTTP {(int)resp.StatusCode}: {Trunc(body)}");
                }

                var text = ExtractText(body);
                if (string.IsNullOrWhiteSpace(text) || text.Contains("BULUNAMADI", StringComparison.OrdinalIgnoreCase))
                    return GroundingResult.Empty("Grounding güvenilir bilgi bulamadı (BULUNAMADI).");

                var (type, note) = ParseTypeAndNote(text);
                return new GroundingResult(note, type, true, null);
            }
            catch (DailyQuotaExhaustedException)
            {
                throw; // toplu iş bunu yakalayıp işi zarifçe durdurur
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Grounding istisna '{Content}'", contentName);
                return GroundingResult.Empty($"Grounding hata: {ex.Message}");
            }
        }

        return GroundingResult.Empty("Grounding: beklenmeyen döngü sonu.");
    }

    private static string ExtractText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
            return "";
        var sb = new StringBuilder();
        foreach (var part in cands[0].GetProperty("content").GetProperty("parts").EnumerateArray())
        {
            if (part.TryGetProperty("text", out var t))
                sb.AppendLine(t.GetString());
        }
        return sb.ToString().Trim();
    }

    private static (string? Type, string Note) ParseTypeAndNote(string text)
    {
        string? type = null;
        var lines = text.Split('\n');
        var noteLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (type is null && trimmed.StartsWith("TİP:", StringComparison.OrdinalIgnoreCase))
            {
                var val = trimmed[4..].Trim().ToLowerInvariant();
                if (val.Contains("dizi")) type = "series";
                else if (val.Contains("film")) type = "movie";
                continue; // TİP satırını nota koyma
            }
            noteLines.Add(line);
        }

        return (type, string.Join("\n", noteLines).Trim());
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

/// <param name="Note">Üretim prompt'una enjekte edilecek karakter/bilgi notu (grounding başarısızsa boş).</param>
/// <param name="DetectedType">"movie"/"series" tespit edilebildiyse; yoksa null.</param>
/// <param name="Succeeded">Kullanılabilir bir not elde edildi mi.</param>
/// <param name="FailureReason">Başarısızsa kısa sebep (loga/uyarıya yazılır).</param>
public record GroundingResult(string Note, string? DetectedType, bool Succeeded, string? FailureReason)
{
    public static GroundingResult Empty(string reason) => new("", null, false, reason);
}
