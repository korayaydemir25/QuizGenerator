using System.Text;
using System.Text.Json;
using QuizGenerator.Models;
using QuizGenerator.Prompts;

namespace QuizGenerator.Services;

public class ClaudeQuestionGenerator : IQuestionGenerator
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    public string Name => "claude";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ClaudeQuestionGenerator(IHttpClientFactory factory, IConfiguration config)
    {
        _http = factory.CreateClient();
        _config = config;
    }

    public async Task<List<GeneratedQuestion>> GenerateAsync(
        string contentName, string contentType, string language, int count,
        IReadOnlyList<string>? existingQuestions = null, string? referenceNote = null, CancellationToken ct = default)
    {
        var prompt = PromptTemplates.BuildGenerationPrompt(contentName, contentType, language, count, existingQuestions, referenceNote);
        var text = await CallClaudeAsync(prompt, ct);

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
        var text = await CallClaudeAsync(prompt, ct);

        var result = JsonSerializer.Deserialize<ReviewResponse>(ExtractJson(text), JsonOpts);
        return result ?? new ReviewResponse();
    }

    /// <summary>
    /// Var olan, cevap sızıntısı tespit edilmiş bir soru için sadece şıkları onarır.
    /// </summary>
    public async Task<FixResult> ProposeFixAsync(QuestionEntity question, string contentName, CancellationToken ct = default)
    {
        var questionJson = JsonSerializer.Serialize(new
        {
            text = question.Text,
            option1 = question.Option1,
            option2 = question.Option2,
            option3 = question.Option3,
            option4 = question.Option4,
            correctOption = question.CorrectOption
        }, JsonOpts);

        var prompt = PromptTemplates.BuildFixPrompt(contentName, questionJson);
        var text = await CallClaudeAsync(prompt, ct);

        var parsed = JsonSerializer.Deserialize<FixResult>(ExtractJson(text), JsonOpts);
        return parsed ?? throw new InvalidOperationException("Claude düzeltme yanıtı ayrıştırılamadı.");
    }

    private async Task<string> CallClaudeAsync(string prompt, CancellationToken ct)
    {
        var apiKey = _config["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "CHANGE_ME")
            throw new InvalidOperationException("Anthropic:ApiKey ayarlanmamış (appsettings.json veya ortam değişkeni).");

        var model = _config["Anthropic:Model"] ?? "claude-sonnet-5";

        var payload = new
        {
            model,
            max_tokens = 8000,
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Claude API hatası ({resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var contentArray = doc.RootElement.GetProperty("content");

        foreach (var part in contentArray.EnumerateArray())
        {
            if (part.GetProperty("type").GetString() == "text")
                return part.GetProperty("text").GetString() ?? "";
        }

        throw new InvalidOperationException("Claude yanıtında metin bloğu bulunamadı.");
    }

    // Model bazen (talimata rağmen) ```json bloğu ile sarabiliyor - savunma amaçlı temizlik.
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
