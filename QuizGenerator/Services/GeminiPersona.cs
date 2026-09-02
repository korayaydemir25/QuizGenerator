namespace QuizGenerator.Services;

/// <summary>
/// Bir Gemini "personası": aynı Gemini modelini FARKLI sıcaklık ve FARKLI odak/üslupla
/// çalıştırmak için. Claude API'sine para vermeden "iki bağımsız üretici" etkisi elde
/// etmenin yolu — iki persona bilerek farklı açılardan soru üretir, embedding tekrar-filtresi
/// çakışanları eler. Böylece çapraz-denetim + çeşitlilik Claude olmadan da korunur.
///
/// StyleHint SADECE üretimde prompt'un başına eklenir (denetimde/çeviride kullanılmaz).
/// Name, DB'deki Questions.AiModel alanına yazılır (üreten tarafı ayırt etmek için).
/// </summary>
public record GeminiPersona(string Name, double Temperature, string? StyleHint)
{
    /// <summary>DI ile enjekte edilen varsayılan "gemini" — mevcut /api/generate davranışıyla birebir aynı.</summary>
    public static readonly GeminiPersona Default = new("gemini", 0.9, null);

    /// <summary>
    /// Toplu üretimde kullanılan iki persona. Bilerek farklı eksenlere ağırlık verir ki
    /// ikisi aynı soruları üretmesin (kullanıcının asıl derdi: "sorular aynı olmamalı").
    /// </summary>
    public static readonly GeminiPersona BulkA = new(
        "gemini-a", 0.95,
        "Bu turda ŞU eksenlere ağırlık ver: olaylar ve olay örgüsü (dönüm noktaları), ikonik mekanlar, " +
        "özel eşyalar/semboller ve akılda kalan replikler. 'Kim kimin nesi' tarzı düz ilişki sorularını AZALT. " +
        "Soru kalıplarını olabildiğince çeşitlendir, betimleyici/orta uzunlukta sorulara yer ver.");

    public static readonly GeminiPersona BulkB = new(
        "gemini-b", 0.8,
        "Bu turda ŞU eksenlere ağırlık ver: karakterler arası ilişkiler ve bağlar, karakter motivasyonları ve " +
        "temalar, kritik kararların sonuçları. Yüzeysel 'soyadı ne' ezberinden kaçın; kısa ve net sorular tercih et " +
        "ama hepsi aynı kalıba düşmesin. Diğer bir üreticinin olaylara/mekanlara odaklandığını varsay, sen farklı açıdan sor.");
}
