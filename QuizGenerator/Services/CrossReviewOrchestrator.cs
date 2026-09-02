using Microsoft.EntityFrameworkCore;
using QuizGenerator.Data;
using QuizGenerator.Models;

namespace QuizGenerator.Services;

/// <summary>
/// TAM OTOMATİK akış: Claude ve Gemini İKİSİ DE API üzerinden çağrılır, birden çok tur
/// (yeniden deneme) desteklenir. Ortak onay/heuristik/benzerlik/seçim mantığı
/// ReviewPipeline'da - bkz. o dosya.
///
/// Bunun manuel (kopyala-yapıştır) versiyonu için Services/ManualGenerationSessionService.cs'e bak -
/// Claude API çağrısı yerine kullanıcıdan claude.ai üzerinden alınan cevabı bekler.
/// </summary>
public class CrossReviewOrchestrator
{
    private readonly ClaudeQuestionGenerator _claude;
    private readonly GeminiQuestionGenerator _gemini;
    private readonly EmbeddingService _embeddings;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<CrossReviewOrchestrator> _logger;

    public CrossReviewOrchestrator(
        ClaudeQuestionGenerator claude,
        GeminiQuestionGenerator gemini,
        EmbeddingService embeddings,
        AppDbContext db,
        IConfiguration config,
        ILogger<CrossReviewOrchestrator> logger)
    {
        _claude = claude;
        _gemini = gemini;
        _embeddings = embeddings;
        _db = db;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Mevcut /api/generate akışı: DI ile gelen Claude + Gemini ikilisiyle üretir.
    /// (Anthropic:ApiKey ayarlı değilse Claude tarafı boş döner; SafeGenerate hatayı yutar.)
    /// </summary>
    public Task<LanguageResult> GenerateForLanguageAsync(
        ContentEntity content, string contentType, string language, int requestedCount,
        bool replaceExisting, string? referenceNote = null, CancellationToken ct = default)
        => GenerateForLanguageAsync(_claude, _gemini, content, contentType, language, requestedCount, replaceExisting, referenceNote, ct);

    /// <summary>
    /// Genelleştirilmiş çekirdek: iki üreticiyi (genA/genB) alır — biri üretir, DİĞERİ denetler
    /// ve tersi. Toplu üretim bunu iki Gemini personasıyla çağırır (Claude API'siz çeşitlilik).
    /// generatedBy/reviewedBy etiketleri generator.Name'den gelir (DB'de AiModel olarak saklanır).
    /// </summary>
    public async Task<LanguageResult> GenerateForLanguageAsync(
        IQuestionGenerator genA, IQuestionGenerator genB,
        ContentEntity content, string contentType, string language, int requestedCount,
        bool replaceExisting, string? referenceNote = null, CancellationToken ct = default,
        bool markApproved = true)
    {
        var warnings = new List<string>();
        var bufferMultiplier = _config.GetValue("QuizGeneration:CandidateBufferMultiplier", 1.2);
        var maxRounds = _config.GetValue("QuizGeneration:MaxRegenerationRounds", 3);
        var dupThreshold = _config.GetValue("QuizGeneration:DuplicateSimilarityThreshold", 0.86);
        var reviewThreshold = _config.GetValue("QuizGeneration:ReviewSimilarityThreshold", 0.80);
        var promptExampleCount = _config.GetValue("QuizGeneration:ExistingQuestionsInPrompt", 60);

        var seenEmbeddings = new List<(string Text, float[] Embedding)>();
        List<QuestionEntity> existingQuestions = new();

        if (replaceExisting)
        {
            var old = await _db.Questions
                .Where(q => q.ContentId == content.Id && q.Language == language && q.IsAiGenerated)
                .ToListAsync(ct);
            if (old.Count > 0)
            {
                _db.Questions.RemoveRange(old);
                warnings.Add($"ReplaceExisting=true: {old.Count} eski AI-üretimi soru silindi (istatistikleri de gitti).");
            }
        }
        else
        {
            existingQuestions = await _db.Questions
                .Where(q => q.ContentId == content.Id && q.Language == language)
                .OrderByDescending(q => q.CreatedAt)
                .ToListAsync(ct);

            foreach (var q in existingQuestions)
            {
                var emb = EmbeddingService.Deserialize(q.EmbeddingJson);
                if (emb is null)
                {
                    try
                    {
                        emb = await _embeddings.EmbedAsync(q.Text, ct);
                        q.EmbeddingJson = EmbeddingService.Serialize(emb);
                    }
                    catch (DailyQuotaExhaustedException) { throw; }
                    catch { continue; }
                }
                seenEmbeddings.Add((q.Text, emb));
            }
        }

        var existingTextsForPrompt = existingQuestions.Take(promptExampleCount).Select(q => q.Text).ToList();

        var approved = new List<ApprovedCandidate>();
        var round = 0;

        while (approved.Count < requestedCount && round < maxRounds)
        {
            round++;
            var remaining = requestedCount - approved.Count;
            var batchCount = Math.Max(4, (int)Math.Ceiling(remaining * bufferMultiplier / 2.0));

            _logger.LogInformation(
                "Tur {Round}: {Content}/{Lang} için her taraftan {Batch} soru isteniyor (eksik: {Remaining})",
                round, content.Name, language, batchCount, remaining);

            var taskA = SafeGenerate(genA, content.Name, contentType, language, batchCount, existingTextsForPrompt, referenceNote, ct);
            var taskB = SafeGenerate(genB, content.Name, contentType, language, batchCount, existingTextsForPrompt, referenceNote, ct);
            await Task.WhenAll(taskA, taskB);

            var batchA = taskA.Result;
            var batchB = taskB.Result;

            if (batchA.Count == 0) warnings.Add($"Tur {round}: {genA.Name} üretimi başarısız oldu veya boş döndü.");
            if (batchB.Count == 0) warnings.Add($"Tur {round}: {genB.Name} üretimi başarısız oldu veya boş döndü.");

            // Her üreticinin ürettiğini DİĞERİ denetler (kendi ürettiğini asla incelemez).
            var bReviewsA = batchA.Count > 0
                ? await SafeReview(genB, content.Name, contentType, language, batchA, referenceNote, ct)
                : new ReviewResponse();
            var aReviewsB = batchB.Count > 0
                ? await SafeReview(genA, content.Name, contentType, language, batchB, referenceNote, ct)
                : new ReviewResponse();

            await ReviewPipeline.FilterApprovedAsync(batchA, bReviewsA, genA.Name, genB.Name, approved, seenEmbeddings, _embeddings, dupThreshold, reviewThreshold, null, warnings, ct);
            await ReviewPipeline.FilterApprovedAsync(batchB, aReviewsB, genB.Name, genA.Name, approved, seenEmbeddings, _embeddings, dupThreshold, reviewThreshold, null, warnings, ct);
        }

        if (approved.Count < requestedCount)
            warnings.Add($"{requestedCount} soru istendi, {maxRounds} tur sonunda sadece {approved.Count} tanesi çapraz denetim + benzerlik filtresinden geçebildi.");

        var finalSelection = ReviewPipeline.SelectBalanced(approved, requestedCount, warnings);

        var savedEntities = new List<QuestionEntity>();
        foreach (var c in finalSelection)
        {
            var entity = new QuestionEntity
            {
                Text = c.Q.Text,
                Option1 = c.Q.Option1,
                Option2 = c.Q.Option2,
                Option3 = c.Q.Option3,
                Option4 = c.Q.Option4,
                CorrectOption = c.Q.CorrectOption,
                Difficulty = c.Q.Difficulty,
                Points = c.Q.Points,
                Language = language,
                MovieOrShowName = content.Name,
                ContentType = contentType,
                CategoryId = content.CategoryId,
                ContentId = content.Id,
                IsApproved = markApproved, // toplu üretim taslak modunda false → oyunculara düşmeden önce gözden geçirilir
                IsAiGenerated = true,
                AiModel = c.GeneratedBy,
                TimesPlayed = 0,
                TimesCorrect = 0,
                IsFlagged = false,
                EmbeddingJson = EmbeddingService.Serialize(c.Embedding),
                CreatedAt = DateTime.UtcNow,
            };
            _db.Questions.Add(entity);
            savedEntities.Add(entity);
        }

        await _db.SaveChangesAsync(ct);

        var resultQuestions = savedEntities.Zip(finalSelection, (entity, c) => new GeneratedQuestionResult(
            entity.Id, entity.Text, entity.Option1, entity.Option2, entity.Option3, entity.Option4,
            entity.CorrectOption, entity.Difficulty, entity.Points, c.GeneratedBy, c.ReviewedBy
        )).ToList();

        return new LanguageResult(language, requestedCount, resultQuestions.Count, resultQuestions, warnings);
    }

        private async Task<List<GeneratedQuestion>> SafeGenerate(
        IQuestionGenerator gen, string contentName, string contentType, string language, int count,
        IReadOnlyList<string> existingQuestions, string? referenceNote, CancellationToken ct)
    {
        try
        {
            return await gen.GenerateAsync(contentName, contentType, language, count, existingQuestions, referenceNote, ct);
        }
        catch (DailyQuotaExhaustedException)
        {
            throw; // toplu iş bunu yakalayıp işi zarifçe durdurur
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Generator} üretimi başarısız oldu ({Content}/{Lang})", gen.Name, contentName, language);
            return new List<GeneratedQuestion>();
        }
    }

    private async Task<ReviewResponse> SafeReview(
        IQuestionGenerator reviewer, string contentName, string contentType, string language,
        List<GeneratedQuestion> candidates, string? referenceNote, CancellationToken ct)
    {
        try
        {
            return await reviewer.ReviewAsync(contentName, contentType, language, candidates, referenceNote, ct);
        }
        catch (DailyQuotaExhaustedException)
        {
            throw; // toplu iş bunu yakalayıp işi zarifçe durdurur
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Reviewer} incelemesi başarısız oldu ({Content}/{Lang})", reviewer.Name, contentName, language);
            return new ReviewResponse();
        }
    }
}
