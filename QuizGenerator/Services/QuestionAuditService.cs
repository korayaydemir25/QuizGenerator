using System.Text.RegularExpressions;

namespace QuizGenerator.Services;

public record AuditFinding(bool Flagged, string? Reason);

/// <summary>
/// AI'ye hiç ihtiyaç duymadan, saf C# kurallarıyla "cevap sızıntısı" tespiti yapar.
/// Hem yeni üretilen sorularda (AI onayına ek savunma katmanı) hem var olan sorularda
/// (tek başına tarama - kredi harcamaz) kullanılır.
/// </summary>
public static class QuestionAuditService
{
    private static readonly Regex ParenthesisRegex = new(@"[\(\)]", RegexOptions.Compiled);

    public static AuditFinding Check(string o1, string o2, string o3, string o4)
    {
        var options = new[] { o1 ?? "", o2 ?? "", o3 ?? "", o4 ?? "" };
        var reasons = new List<string>();

        // 1. Parantez kontrolü - kural gereği kesinlikle yasak
        if (options.Any(o => ParenthesisRegex.IsMatch(o)))
            reasons.Add("parantez_icerigi");

        // 2. Karakter uzunluğu sapması - bir şık diğerlerinden bariz uzun/kısa mı
        var lengths = options.Select(o => o.Trim().Length).ToArray();
        var avgLen = lengths.Average();
        var maxLen = lengths.Max();
        var minLen = lengths.Min();
        if (avgLen > 0 && maxLen > avgLen * 1.8 && maxLen - minLen > 12)
            reasons.Add("uzunluk_dengesizligi");

        // 3. Kelime sayısı sapması - üslup paralelliği bozulmuş mu
        // (3 şık tek kelime, 1 şık tam cümle gibi durumları yakalar)
        var wordCounts = options
            .Select(o => o.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
            .ToArray();
        if (wordCounts.Max() - wordCounts.Min() >= 4)
            reasons.Add("uslup_uyumsuzlugu");

        return reasons.Count > 0
            ? new AuditFinding(true, string.Join(", ", reasons))
            : new AuditFinding(false, null);
    }
}
