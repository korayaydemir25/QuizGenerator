using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuizGenerator.Data;
using QuizGenerator.Models;
using QuizGenerator.Prompts;

namespace QuizGenerator.Services;

/// <summary>
/// MANUEL akış: Gemini tarafı yine API ile otomatik çalışır (ücretsiz katman yeterli).
/// Claude tarafı API ÜZERİNDEN ÇAĞRILMAZ - kullanıcı, sistemin hazırladığı prompt'u
/// kendi claude.ai (Pro) hesabında çalıştırır, cevabı geri yapıştırır. Böylece Claude
/// API'sine hiç ödeme yapılmadan iki-AI çapraz denetim korunur.
///
/// Otomatik akıştan farkı: TEK TUR çalışır (yeniden deneme döngüsü yok, çünkü her tur
/// kullanıcıdan 2 manuel adım isterdi) - bunun yerine daha büyük bir tampon (buffer) ile
/// tek seferde yeterli aday üretilmeye çalışılır. Hedefe tam ulaşılamazsa (ReviewPipeline
/// zaten bunu warning olarak bildirir) kullanıcı isterse tekrar üretim başlatabilir.
///
/// Oturum durumu bilerek DB'de değil, bellekte (statik ConcurrentDictionary) tutuluyor -
/// bu tek kullanıcılı, lokal bir araç; uygulama yeniden başlarsa yarım kalan oturum
/// kaybolur, kullanıcı baştan başlar. Kalıcı bir sorun değil.
/// </summary>
public class ManualGenerationSessionService
{
    private static readonly ConcurrentDictionary<Guid, ManualSession> Sessions = new();
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly GeminiQuestionGenerator _gemini;
    private readonly EmbeddingService _embeddings;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public ManualGenerationSessionService(
        GeminiQuestionGenerator gemini, EmbeddingService embeddings, AppDbContext db, IConfiguration config)
    {
        _gemini = gemini;
        _embeddings = embeddings;
        _db = db;
        _config = config;
    }

    private class ManualSession
    {
        public required ContentEntity Content { get; init; }
        public required string ContentType { get; init; }
        public required string Language { get; init; }            // birincil dil (üretim + denetim bu dilde)
        public required IReadOnlyList<string> Languages { get; init; } // tüm hedef diller; birincil dışındakiler çevrilir
        public required int RequestedCount { get; init; }
        public string? ReferenceNote { get; init; }               // doğrulanmış karakter/bilgi notu (varsa)
        public List<GeneratedQuestion> GeminiBatch { get; set; } = new();
        public List<GeneratedQuestion> ClaudeBatch { get; set; } = new();
        public ReviewResponse GeminiReviewOfClaudeBatch { get; set; } = new();
        public List<(string Text, float[] Embedding)> SeenEmbeddings { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        // Şüpheli-benzerlik onay adımı arasında saklanır:
        public List<ApprovedCandidate> AutoApproved { get; set; } = new();
        public List<SuspiciousCandidate> Suspicious { get; set; } = new();
    }

    /// <summary>Adım 1: Gemini'yi otomatik çalıştırır, Claude için üretim prompt'unu hazırlar.
    /// languages birden fazlaysa üretim/denetim BİRİNCİL dilde (languages[0]) yapılır; diğer diller
    /// son adımda otomatik çevrilir.</summary>
    public async Task<ManualStepResponse> StartAsync(
        ContentEntity content, string contentType, IReadOnlyList<string> languages, int requestedCount,
        string? referenceNote, CancellationToken ct)
    {
        if (languages is null || languages.Count == 0)
            throw new InvalidOperationException("En az bir dil belirtilmeli.");
        var language = languages[0]; // birincil dil
        // Tek tur olduğu için tampon (buffer) otomatik moddan biraz daha büyük tutulur
        // (token maliyetini düşürmek için 1.5 -> 1.25'e çekildi; yeni prompt daha az ret ürettiği için yeterli)
        var bufferMultiplier = _config.GetValue("QuizGeneration:CandidateBufferMultiplier", 1.2) * 1.25;
        var promptExampleCount = _config.GetValue("QuizGeneration:ExistingQuestionsInPrompt", 60);
        var batchCount = Math.Max(requestedCount, (int)Math.Ceiling(requestedCount * bufferMultiplier));

        var existingQuestions = await _db.Questions
            .Where(q => q.ContentId == content.Id && q.Language == language)
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync(ct);

        var seenEmbeddings = new List<(string Text, float[] Embedding)>();
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
                catch { continue; }
            }
            seenEmbeddings.Add((q.Text, emb));
        }
        await _db.SaveChangesAsync(ct); // backfill edilen embedding'ler varsa kaydedilsin

        var existingTextsForPrompt = existingQuestions.Take(promptExampleCount).Select(q => q.Text).ToList();

        List<GeneratedQuestion> geminiBatch;
        try
        {
            geminiBatch = await _gemini.GenerateAsync(content.Name, contentType, language, batchCount, existingTextsForPrompt, referenceNote, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Gemini üretimi başarısız oldu: {ex.Message}");
        }

        var sessionId = Guid.NewGuid();
        Sessions[sessionId] = new ManualSession
        {
            Content = content,
            ContentType = contentType,
            Language = language,
            Languages = languages,
            RequestedCount = requestedCount,
            ReferenceNote = referenceNote,
            GeminiBatch = geminiBatch,
            SeenEmbeddings = seenEmbeddings,
        };

        var prompt = PromptTemplates.BuildGenerationPrompt(content.Name, contentType, language, batchCount, existingTextsForPrompt, referenceNote);

        return new ManualStepResponse(
            sessionId,
            "paste-claude-generation",
            prompt,
            "Bu prompt'u kopyala, claude.ai'de YENİ bir sohbette çalıştır. Claude'un verdiği cevabı -sadece JSON kısmını, başka açıklama eklemeden- aşağıya yapıştır."
        );
    }

    /// <summary>Adım 2: Kullanıcının yapıştırdığı Claude üretimini işler, Gemini'yi otomatik onun hakemi yapar, Claude'un bu kez Gemini'yi incelemesi için prompt hazırlar.</summary>
    public async Task<ManualStepResponse> SubmitGenerationAsync(Guid sessionId, string claudeResponseText, CancellationToken ct)
    {
        var session = GetSession(sessionId);

        List<GeneratedQuestion> claudeBatch;
        try
        {
            claudeBatch = ParseGeneratedQuestions(claudeResponseText);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Yapıştırılan cevap JSON olarak ayrıştırılamadı. Claude'un cevabındaki JSON'u eksiksiz yapıştır (baştaki/sondaki açıklama metni sorun değil, biz temizliyoruz; ama her sorunun text, option1-4, correctOption, difficulty, points alanları olmalı). Ayrıntı: " + ex.Message);
        }
        if (claudeBatch.Count == 0)
            throw new InvalidOperationException("Yapıştırılan metinden hiç soru ayrıştırılamadı - Claude'un TAM JSON cevabını yapıştırdığından emin ol.");
        session.ClaudeBatch = claudeBatch;

        ReviewResponse geminiReview;
        try
        {
            geminiReview = await _gemini.ReviewAsync(session.Content.Name, session.ContentType, session.Language, claudeBatch, session.ReferenceNote, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Gemini'nin Claude'u incelemesi başarısız oldu: {ex.Message}");
        }
        session.GeminiReviewOfClaudeBatch = geminiReview;

        var candidatesJson = JsonSerializer.Serialize(session.GeminiBatch, JsonOpts);
        var reviewPrompt = PromptTemplates.BuildReviewPrompt(session.Content.Name, session.ContentType, session.Language, candidatesJson, session.ReferenceNote);

        return new ManualStepResponse(
            sessionId,
            "paste-claude-review",
            reviewPrompt,
            "Şimdi bu inceleme prompt'unu YENİ bir claude.ai sohbetinde çalıştır (Gemini'nin ürettiği soruları denetleyecek). Cevabını -sadece JSON kısmını- aşağıya yapıştır."
        );
    }

    /// <summary>Adım 3: Kullanıcının yapıştırdığı Claude incelemesini işler, filtreleri uygular.
    /// Benzerlik "şüpheli bandındaki" (reviewThreshold ≤ benzerlik &lt; dupThreshold) adaylar varsa
    /// DB'ye YAZMAZ; onları kullanıcı onayına döndürür (NeedsReview=true) ve oturumu açık bırakır.
    /// Şüpheli yoksa doğrudan final seti kaydeder.</summary>
    public async Task<ManualReviewOutcome> SubmitReviewAsync(Guid sessionId, string claudeResponseText, CancellationToken ct)
    {
        var session = GetSession(sessionId);
        var dupThreshold = _config.GetValue("QuizGeneration:DuplicateSimilarityThreshold", 0.86);
        var reviewThreshold = _config.GetValue("QuizGeneration:ReviewSimilarityThreshold", 0.80);

        var claudeReviewOfGemini = ParseReviewResponse(claudeResponseText);

        var approved = new List<ApprovedCandidate>();
        var suspicious = new List<SuspiciousCandidate>();

        // Claude'un ürettiklerini -Gemini'nin verdiği hakem kararıyla- değerlendir
        await ReviewPipeline.FilterApprovedAsync(
            session.ClaudeBatch, session.GeminiReviewOfClaudeBatch, "claude", "gemini",
            approved, session.SeenEmbeddings, _embeddings, dupThreshold, reviewThreshold, suspicious, session.Warnings, ct);

        // Gemini'nin ürettiklerini -kullanıcının az önce yapıştırdığı Claude kararıyla- değerlendir
        await ReviewPipeline.FilterApprovedAsync(
            session.GeminiBatch, claudeReviewOfGemini, "gemini", "claude",
            approved, session.SeenEmbeddings, _embeddings, dupThreshold, reviewThreshold, suspicious, session.Warnings, ct);

        // Şüpheli varsa: kaydetme, kullanıcıya sor (oturumu AÇIK bırak - karar SubmitSimilarityDecisionsAsync'e gelecek).
        if (suspicious.Count > 0)
        {
            session.AutoApproved = approved;
            session.Suspicious = suspicious;

            var dtos = suspicious.Select((s, i) => new SuspiciousQuestionDto(
                i, s.Candidate.GeneratedBy, Math.Round(s.Similarity, 3), s.SimilarToText,
                s.Candidate.Q.Text, s.Candidate.Q.Option1, s.Candidate.Q.Option2, s.Candidate.Q.Option3, s.Candidate.Q.Option4,
                s.Candidate.Q.CorrectOption, s.Candidate.Q.Difficulty, s.Candidate.Q.Points
            )).ToList();

            return new ManualReviewOutcome(true, null, dtos);
        }

        // Şüpheli yok: doğrudan bitir.
        var finalSelectionDirect = ReviewPipeline.SelectBalanced(approved, session.RequestedCount, session.Warnings);
        var directResult = await FinalizeAndSaveAsync(session, finalSelectionDirect, ct);
        Sessions.TryRemove(sessionId, out _);
        return new ManualReviewOutcome(false, directResult, null);
    }

    /// <summary>Adım 3b (yalnızca şüpheli varsa): Kullanıcının tutmaya karar verdiği şüpheli adayları
    /// (keepIndices) otomatik onaylananlara ekler, final seti kaydeder.</summary>
    public async Task<LanguageResult> SubmitSimilarityDecisionsAsync(Guid sessionId, IReadOnlyList<int>? keepIndices, CancellationToken ct)
    {
        var session = GetSession(sessionId);
        var keep = new HashSet<int>(keepIndices ?? Array.Empty<int>());

        var pool = new List<ApprovedCandidate>(session.AutoApproved);
        for (int i = 0; i < session.Suspicious.Count; i++)
        {
            if (keep.Contains(i))
                pool.Add(session.Suspicious[i].Candidate);
            else
                session.Warnings.Add($"Şüpheli soru kullanıcı kararıyla elendi (benzerlik %{session.Suspicious[i].Similarity * 100:F0}): \"{Truncate(session.Suspicious[i].Candidate.Q.Text)}\"");
        }

        var finalSelection = ReviewPipeline.SelectBalanced(pool, session.RequestedCount, session.Warnings);
        var result = await FinalizeAndSaveAsync(session, finalSelection, ct);
        Sessions.TryRemove(sessionId, out _);
        return result;
    }

    /// <summary>Final seçimi DB'ye yazar (birincil dil), diğer dil(ler)i otomatik çevirip kaydeder,
    /// LanguageResult döndürür. Oturumu KALDIRMAZ - çağıran taraf halleder.</summary>
    private async Task<LanguageResult> FinalizeAndSaveAsync(ManualSession session, List<ApprovedCandidate> finalSelection, CancellationToken ct)
    {
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
                Language = session.Language,
                MovieOrShowName = session.Content.Name,
                ContentType = session.ContentType,
                CategoryId = session.Content.CategoryId,
                ContentId = session.Content.Id,
                IsApproved = true,
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

        // --- Aynı sorular diğer dil(ler)de: final seti otomatik çevir ve kaydet ---
        // Anlam/şık sırası/doğru cevap korunur (correctOption vb. kaynaktan kopyalanır).
        var extraLanguages = session.Languages.Where(l => l != session.Language).Distinct().ToList();
        if (extraLanguages.Count > 0 && finalSelection.Count > 0)
        {
            var sourceQuestions = finalSelection.Select(c => c.Q).ToList();
            foreach (var toLang in extraLanguages)
            {
                List<GeneratedQuestion> translated;
                try
                {
                    translated = await _gemini.TranslateAsync(sourceQuestions, session.Language, toLang, session.Content.Name, ct);
                }
                catch (Exception ex)
                {
                    session.Warnings.Add($"{toLang.ToUpperInvariant()} çevirisi başarısız oldu, o dilde soru kaydedilmedi: {ex.Message}");
                    continue;
                }

                var translatedCount = 0;
                for (int i = 0; i < finalSelection.Count && i < translated.Count; i++)
                {
                    var src = finalSelection[i];
                    var tr = translated[i];
                    _db.Questions.Add(new QuestionEntity
                    {
                        Text = tr.Text,
                        Option1 = tr.Option1,
                        Option2 = tr.Option2,
                        Option3 = tr.Option3,
                        Option4 = tr.Option4,
                        CorrectOption = src.Q.CorrectOption, // birebir aynı pozisyon
                        Difficulty = src.Q.Difficulty,
                        Points = src.Q.Points,
                        Language = toLang,
                        MovieOrShowName = session.Content.Name,
                        ContentType = session.ContentType,
                        CategoryId = session.Content.CategoryId,
                        ContentId = session.Content.Id,
                        IsApproved = true,
                        IsAiGenerated = true,
                        AiModel = src.GeneratedBy,
                        TimesPlayed = 0,
                        TimesCorrect = 0,
                        IsFlagged = false,
                        EmbeddingJson = null, // çeviri için embedding sonradan backfill edilebilir
                        CreatedAt = DateTime.UtcNow,
                    });
                    translatedCount++;
                }

                if (translatedCount < finalSelection.Count)
                    session.Warnings.Add($"{toLang.ToUpperInvariant()} çevirisi eksik döndü: {finalSelection.Count} sorudan {translatedCount} tanesi çevrilebildi.");
                else
                    session.Warnings.Add($"{toLang.ToUpperInvariant()} çevirisi de kaydedildi: {translatedCount} soru (birincil dille aynı sorular).");
            }
        }

        await _db.SaveChangesAsync(ct);

        var resultQuestions = savedEntities.Zip(finalSelection, (entity, c) => new GeneratedQuestionResult(
            entity.Id, entity.Text, entity.Option1, entity.Option2, entity.Option3, entity.Option4,
            entity.CorrectOption, entity.Difficulty, entity.Points, c.GeneratedBy, c.ReviewedBy
        )).ToList();

        return new LanguageResult(session.Language, session.RequestedCount, resultQuestions.Count, resultQuestions, session.Warnings);
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    private static ManualSession GetSession(Guid sessionId)
    {
        if (!Sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException("Oturum bulunamadı ya da süresi doldu (uygulama yeniden başlamış olabilir) - baştan başlat.");
        return session;
    }

    private static List<GeneratedQuestion> ParseGeneratedQuestions(string raw)
    {
        using var parsed = JsonDocument.Parse(ExtractJson(raw));
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

    private static ReviewResponse ParseReviewResponse(string raw)
    {
        return JsonSerializer.Deserialize<ReviewResponse>(ExtractJson(raw), JsonOpts) ?? new ReviewResponse();
    }

    // Claude bazen (talimata rağmen) ```json bloğu ile sarar ya da JSON'un önüne/arkasına
    // açıklama cümlesi ekler (özellikle claude.ai'de). Savunma amaçlı temizlik.
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
        // JSON'un başında/sonunda düz metin (ör. "İşte sorular:") varsa, ilk '{' ile son '}' arasını al.
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start > -1 && end > start)
            trimmed = trimmed[start..(end + 1)];
        return trimmed;
    }
}
