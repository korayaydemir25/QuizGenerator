using System.Text.Json;
using QuizGenerator.Models;

namespace QuizGenerator.Services;

/// <summary>
/// Manuel düzeltme köprüsü: claude.ai'den kopyala-yapıştır ile alınan cevabı FixResult'a
/// ayrıştırır. API çağrısı YAPMAZ, sadece yapıştırılan metni parse eder.
/// </summary>
public static class ManualFixParser
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public static FixResult Parse(string raw)
    {
        var result = JsonSerializer.Deserialize<FixResult>(ExtractJson(raw), JsonOpts);
        return result ?? throw new InvalidOperationException("Yapıştırılan metinden geçerli bir JSON ayrıştırılamadı - Claude'un TAM cevabını yapıştırdığından emin ol.");
    }

    /// <summary>Toplu düzeltme akışı için: bir JSON DİZİSİNİ (birden fazla soru) ayrıştırır.</summary>
    public static List<BatchFixItem> ParseBatch(string raw)
    {
        var result = JsonSerializer.Deserialize<List<BatchFixItem>>(ExtractJson(raw), JsonOpts);
        return result ?? throw new InvalidOperationException("Yapıştırılan metinden geçerli bir JSON dizisi ayrıştırılamadı - Claude'un TAM cevabını yapıştırdığından emin ol.");
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
        return trimmed;
    }
}
