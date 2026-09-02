using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuizGenerator.Models;
using QuizGenerator.Prompts;

namespace QuizGenerator.Services;

public class GeminiQuestionGenerator : IQuestionGenerator
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly GeminiPersona _persona;
    public string Name => _persona.Name;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // DI bu 2 parametreli kurucuyu seçer (GeminiPersona kayıtlı değil) → varsayılan "gemini" personası,
    // mevcut /api/generate davranışıyla birebir aynı. Toplu üretim tarafı 3 parametreli kurucuyu
    // elle (new ...) çağırarak persona verir.
    public GeminiQuestionGenerator(IHttpClientFactory factory, IConfiguration config)
        : this(factory, config, GeminiPersona.Default) { }

    public GeminiQuestionGenerator(IHttpClientFactory factory, IConfiguration config, GeminiPersona persona)
    {
        _http = factory.CreateClient();
        _config = config;
        _persona = persona;
    }

    public async Task<List<GeneratedQuestion>> GenerateAsync(
        string contentName, string contentType, string language, int count,
        IReadOnlyList<string>? existingQuestions = null, string? referenceNote = null, CancellationToken ct = default)
    {
        var prompt = PromptTemplates.BuildGenerationPrompt(
            contentName, contentType, language, count, existingQuestions, referenceNote, _persona.StyleHint);
        var text = await CallGeminiAsync(prompt, _persona.Temperature, ct);

        using var parsed = JsonDocument.Parse(ExtractJson(text));
        var questions = new List<GeneratedQuestion>();

        foreach (var q in parsed.RootElement.GetProperty("questions").EnumerateArray())
        {
            questions.Add(new GeneratedQuestion
            {
                Text = q.GetProperty("text").GetString() ?? "",
                Option1 = q.GetProperty("option1").GetString() ?? "",
                Option2 = q.GetProperty("option2").GetString() ?? "",
                Option3 = q.GetProperty("option3").GetString() ?? "",
                Option4 = q.GetProperty("option4").GetString() ?? "",
                CorrectOption = q.GetProperty("correctOption").GetInt32(),
                Difficulty = q.GetProperty("difficulty").GetString() ?? "easy",
                Points = q.GetProperty("points").GetInt32(),
            });
        }

        return questions;
    }

    public async Task<ReviewResponse> ReviewAsync(
        string contentName, string contentType, string language,
        List<GeneratedQuestion> candidates, string? referenceNote = null, CancellationToken ct = default)
    {
        var candidatesJson = JsonSerializer.Serialize(candidates, JsonOpts);
        var prompt = PromptTemplates.BuildReviewPrompt(contentName, contentType, language, candidatesJson, referenceNote);
        // Denetim daha düşük sıcaklıkla daha tutarlı — persona sıcaklığını kullanma.
        var text = await CallGeminiAsync(prompt, 0.35, ct);

        var result = JsonSerializer.Deserialize<ReviewResponse>(ExtractJson(text), JsonOpts);
        return result ?? new ReviewResponse();
    }

    /// <summary>
    /// Onaylanmış final soru setini başka bir dile çevirir. correctOption/difficulty/points
    /// çeviriden GELMEZ - çağıran taraf bunları kaynaktan kopyalar; burada sadece metin+şıklar çevrilir.
    /// Dönen liste kaynakla aynı sırada ve (ideal olarak) aynı uzunluktadır.
    /// </summary>
    public async Task<List<GeneratedQuestion>> TranslateAsync(
        List<GeneratedQuestion> questions, string fromLang, string toLang, string contentName, CancellationToken ct = default)
    {
        if (questions.Count == 0) return new();

        var payload = questions.Select(q => new
        {
            text = q.Text,
            option1 = q.Option1,
            option2 = q.Option2,
            option3 = q.Option3,
            option4 = q.Option4
        });
        var questionsJson = JsonSerializer.Serialize(payload, JsonOpts);
        var prompt = PromptTemplates.BuildTranslationPrompt(contentName, fromLang, toLang, questionsJson);
        var text = await CallGeminiAsync(prompt, 0.3, ct);

        using var parsed = JsonDocument.Parse(ExtractJson(text));
        var translated = new List<GeneratedQuestion>();

        var i = 0;
        foreach (var t in parsed.RootElement.GetProperty("questions").EnumerateArray())
        {
            // correctOption/difficulty/points'i modele bırakma - kaynaktan kopyala (çeviri bunları değiştiremez)
            var src = i < questions.Count ? questions[i] : null;
            translated.Add(new GeneratedQuestion
            {
                Text = t.GetProperty("text").GetString() ?? "",
                Option1 = t.GetProperty("option1").GetString() ?? "",
                Option2 = t.GetProperty("option2").GetString() ?? "",
                Option3 = t.GetProperty("option3").GetString() ?? "",
                Option4 = t.GetProperty("option4").GetString() ?? "",
                CorrectOption = src?.CorrectOption ?? 0,
                Difficulty = src?.Difficulty ?? "easy",
                Points = src?.Points ?? 0,
            });
            i++;
        }

        return translated;
    }

    private async Task<string> CallGeminiAsync(string prompt, double temperature, CancellationToken ct)
    {
        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "CHANGE_ME")
            throw new InvalidOperationException("Gemini:ApiKey ayarlanmamış (appsettings.json veya ortam değişkeni).");

        var model = _config["Gemini:Model"] ?? "gemini-3.5-flash";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var payload = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { temperature }
        };
        var payloadJson = JsonSerializer.Serialize(payload);

        // 429 (rate limit) dayanıklılığı — toplu üretimde dakikalık limit sık sık tetiklenir.
        // EmbeddingService'teki mantığın aynısı: PerDay ise beklemenin anlamı yok (DailyQuota fırlat),
        // aksi halde Google'ın retryDelay'ini bekleyip tekrar dene.
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
                if (body.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                    throw new DailyQuotaExhaustedException(
                        $"Günlük Gemini üretim kotası doldu (ücretsiz katman). Kalan içerikler ancak yarın (Pasifik saatiyle gece yarısı sıfırlanır) tamamlanabilir, ya da faturalandırma açarsan hemen devam edebilirsin. Detay: {body}");

                if (attempt == maxAttempts)
                    throw new HttpRequestException($"Gemini API hatası ({resp.StatusCode}) - {maxAttempts} denemeden sonra hâlâ rate limit: {body}");

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
                throw new HttpRequestException($"Gemini API hatası ({resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return text ?? "";
        }

        throw new HttpRequestException("Gemini API: beklenmeyen döngü sonu.");
    }

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```");
            if (firstNewline > -1 && lastFence > firstNewline)
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
        }
        // JSON'un başında/sonunda düz metin varsa ilk '{' ile son '}' arasını al.
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start > -1 && end > start)
            trimmed = trimmed[start..(end + 1)];
        return trimmed;
    }
}
