using QuizGenerator.Models;

namespace QuizGenerator.Services;

public interface IQuestionGenerator
{
    string Name { get; } // "claude" | "gemini"

    /// <summary>
    /// existingQuestions: bu içerik+dil için zaten var olan soru metinleri (varsa) -
    /// AI'nin bunları tekrar etmemesi/benzerini üretmemesi için prompt'a eklenir.
    /// Bu, tekrar/benzerlik önlemenin ÖNLEYİCİ katmanıdır; asıl garanti embedding
    /// karşılaştırmasıyla CrossReviewOrchestrator'da sağlanır.
    /// </summary>
    Task<List<GeneratedQuestion>> GenerateAsync(
        string contentName, string contentType, string language, int count,
        IReadOnlyList<string>? existingQuestions = null, string? referenceNote = null, CancellationToken ct = default);

    /// <summary>
    /// candidates parametresi bu AI'nin DEĞİL, KARŞI TARAFIN ürettiği sorulardır.
    /// Her generator kendi ürettiğini asla incelemez.
    /// referenceNote: doğrulanmış karakter/bilgi notu (varsa) - hakem isim denetimini buna göre yapar.
    /// </summary>
    Task<ReviewResponse> ReviewAsync(
        string contentName, string contentType, string language,
        List<GeneratedQuestion> candidates, string? referenceNote = null, CancellationToken ct = default);
}
