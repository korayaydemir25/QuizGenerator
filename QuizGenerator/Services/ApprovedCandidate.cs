using QuizGenerator.Models;

namespace QuizGenerator.Services;

/// <summary>
/// Çapraz denetim + heuristik + benzerlik filtresinden geçmiş bir aday soru.
/// Hem otomatik (CrossReviewOrchestrator) hem manuel (ManualGenerationSessionService)
/// akış bunu kullanır.
/// </summary>
public class ApprovedCandidate
{
    public required GeneratedQuestion Q { get; init; }
    public required string GeneratedBy { get; init; }
    public required string ReviewedBy { get; init; }
    public required float[] Embedding { get; init; }
}

/// <summary>
/// Benzerlik "şüpheli bandında" (reviewThreshold ≤ benzerlik &lt; dupThreshold) kalan aday.
/// Otomatik elenmez de otomatik kaydedilmez; kullanıcı onayına sunulur. Onaylanırsa
/// ApprovedCandidate gibi kaydedilir. Sadece manuel akışta kullanılır.
/// </summary>
public class SuspiciousCandidate
{
    public required ApprovedCandidate Candidate { get; init; }
    public required string SimilarToText { get; init; } // benzediği (mevcut ya da bu turda kabul edilmiş) soru
    public required double Similarity { get; init; }
}
