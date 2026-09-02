using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using QuizGenerator.Data;
using QuizGenerator.Models;
using QuizGenerator.Prompts;
using QuizGenerator.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHttpClient();
builder.Services.AddScoped<ClaudeQuestionGenerator>();
builder.Services.AddScoped<GeminiQuestionGenerator>();
builder.Services.AddScoped<EmbeddingService>();
builder.Services.AddScoped<CrossReviewOrchestrator>();
builder.Services.AddScoped<ManualGenerationSessionService>();
builder.Services.AddScoped<GroundingService>();
builder.Services.AddSingleton<BulkGenerationService>();

var app = builder.Build();

// NOT: Burada db.Database.Migrate() BİLEREK çağrılmıyor.
// Questions/Content/Categories tabloları ana backend'in (movie-quiz-main) migration'ları
// tarafından zaten yönetiliyor. Bu servis sadece var olan tablolara sidecar olarak bağlanıyor.
// Açılışta sadece bağlantıyı test edip logluyoruz.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        logger.LogInformation("Veritabanı bağlantısı: {Status}", canConnect ? "OK" : "BAŞARISIZ");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Veritabanına açılışta bağlanılamadı - ConnectionStrings:Default ayarını kontrol et.");
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

// --- İçerik ara (admin arayüzünde autocomplete için) ---
app.MapGet("/api/content/search", async (string q, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<ContentSearchResult>());

    var results = await db.Contents
        .Where(c => EF.Functions.ILike(c.Name, $"%{q}%"))
        .OrderBy(c => c.Name)
        .Take(10)
        .Select(c => new { c.Id, c.Name, c.CategoryId, c.IsActive })
        .ToListAsync();

    var categoryIds = results.Select(r => r.CategoryId).Distinct().ToList();
    var categories = await db.Categories
        .Where(cat => categoryIds.Contains(cat.Id))
        .ToDictionaryAsync(cat => cat.Id, cat => cat.DisplayName);

    var mapped = results.Select(r => new ContentSearchResult(
        r.Id, r.Name, categories.GetValueOrDefault(r.CategoryId), r.IsActive));

    return Results.Ok(mapped);
});

// --- Yeni soru üret (Claude + Gemini çapraz denetimli) ---
app.MapPost("/api/generate", async (GenerateRequest req, AppDbContext db, CrossReviewOrchestrator orchestrator, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ContentName) && req.ContentId is null)
        return Results.BadRequest(new { error = "contentName ya da contentId'den biri gerekli." });
    if (req.QuestionCount <= 0)
        return Results.BadRequest(new { error = "questionCount pozitif olmalı." });
    if (req.Languages is null || req.Languages.Count == 0)
        return Results.BadRequest(new { error = "En az bir dil belirtilmeli (tr/en)." });
    if (string.IsNullOrWhiteSpace(req.ContentType))
        return Results.BadRequest(new { error = "contentType gerekli (\"movie\" veya \"series\")." });

    // İçeriği çöz: önce ContentId, yoksa isimle ara.
    // NOT: Content bulunamazsa BURADA OTOMATİK OLUŞTURMUYORUZ - çünkü Content.Type ve
    // Content.Origin (int) alanlarının gerçek enum karşılıkları bilinmiyor; yanlış değerle
    // satır oluşturmak ana uygulamanın davranışını bozabilir. Önce ana backend/admin panelinden
    // içeriği oluşturman gerekiyor.
    ContentEntity? content = req.ContentId is int cid
        ? await db.Contents.FirstOrDefaultAsync(c => c.Id == cid, ct)
        : await db.Contents.FirstOrDefaultAsync(c => EF.Functions.ILike(c.Name, req.ContentName), ct);

    if (content is null)
    {
        return Results.NotFound(new
        {
            error = $"İçerik bulunamadı: '{req.ContentName}'. Önce bu içeriği ana backend'in Content tablosuna ekle, sonra soru üretebilirsin.",
            hint = "Zaten var olduğunu düşünüyorsan /api/content/search?q=... ile ara, doğru contentId'yi bul ve isteğe contentId olarak ekle."
        });
    }

    var genre = await db.Categories
        .Where(c => c.Id == content.CategoryId)
        .Select(c => c.DisplayName)
        .FirstOrDefaultAsync(ct);

    var results = new List<LanguageResult>();
    foreach (var lang in req.Languages)
    {
        var result = await orchestrator.GenerateForLanguageAsync(
            content, req.ContentType, lang, req.QuestionCount, req.ReplaceExisting, req.ReferenceNote, ct);
        results.Add(result);
    }

    return Results.Ok(new GenerateResponse(content.Id, content.Name, genre, results));
});

// --- Belirli bir içerik+dil için soruları istemci (mobil app) formatında dışa ver ---
app.MapGet("/api/content/{contentId:int}/questions", async (int contentId, string language, AppDbContext db) =>
{
    var content = await db.Contents.FirstOrDefaultAsync(c => c.Id == contentId);
    if (content is null) return Results.NotFound();

    var questions = await db.Questions
        .Where(q => q.ContentId == contentId && q.Language == language && q.IsApproved)
        .ToListAsync();

    return Results.Ok(new
    {
        contentName = content.Name,
        contentId = content.Id,
        language,
        questions = questions.Select(q => new
        {
            id = q.Id,
            text = q.Text,
            option1 = q.Option1,
            option2 = q.Option2,
            option3 = q.Option3,
            option4 = q.Option4,
            correctOption = q.CorrectOption,
            difficulty = q.Difficulty,
            points = q.Points,
            movieOrShowName = q.MovieOrShowName,
            contentType = q.ContentType,
            isAiGenerated = q.IsAiGenerated,
        })
    });
});

// --- Var olan soruları AI kullanmadan (ücretsiz) heuristik kurallarla tara ---
app.MapGet("/api/questions/audit", async (AppDbContext db) =>
{
    var all = await db.Questions.ToListAsync();
    var flagged = new List<object>();

    foreach (var q in all)
    {
        var finding = QuestionAuditService.Check(q.Option1, q.Option2, q.Option3, q.Option4);
        q.IsFlagged = finding.Flagged;

        if (finding.Flagged)
        {
            flagged.Add(new
            {
                q.Id,
                q.MovieOrShowName,
                q.Language,
                q.Text,
                q.Option1,
                q.Option2,
                q.Option3,
                q.Option4,
                q.CorrectOption,
                Reason = finding.Reason // sadece bu response'ta - DB'de saklanmıyor
            });
        }
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { totalChecked = all.Count, flaggedCount = flagged.Count, flagged });
});

// --- Sorunlu bir soru için Claude'dan düzeltilmiş şıklar iste (önizleme döner, KAYDETMEZ) ---
// NOT: Bu, Claude API'sini DOĞRUDAN çağırır - Anthropic:ApiKey ayarlı olmalı.
// API'ye ödeme yapmak istemiyorsan bunun yerine aşağıdaki manuel-fix-prompt / parse-manual-fix
// çiftini kullanan arayüz akışını tercih et (mevcut "Denetle" sekmesindeki buton bunu kullanıyor).
app.MapPost("/api/questions/{id:int}/fix", async (int id, AppDbContext db, ClaudeQuestionGenerator claude) =>
{
    var q = await db.Questions.FindAsync(id);
    if (q == null) return Results.NotFound();

    var fix = await claude.ProposeFixAsync(q, q.MovieOrShowName);
    return Results.Ok(fix);
});

// --- Manuel: sorunlu bir soru için onarım prompt'unu hazırla (API çağrısı YOK) ---
app.MapGet("/api/questions/{id:int}/manual-fix-prompt", async (int id, AppDbContext db) =>
{
    var q = await db.Questions.FindAsync(id);
    if (q == null) return Results.NotFound();

    var questionJson = JsonSerializer.Serialize(new
    {
        text = q.Text,
        option1 = q.Option1,
        option2 = q.Option2,
        option3 = q.Option3,
        option4 = q.Option4,
        correctOption = q.CorrectOption
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    var prompt = PromptTemplates.BuildFixPrompt(q.MovieOrShowName, questionJson);

    return Results.Ok(new ManualStepResponse(
        Guid.Empty,
        "paste-claude-fix",
        prompt,
        "Bu prompt'u kopyala, claude.ai'de çalıştır, Claude'un TAM JSON cevabını aşağıya yapıştır."
    ));
});

// --- Manuel: yapıştırılan Claude cevabını FixResult'a ayrıştır (önizleme için, API çağrısı YOK) ---
app.MapPost("/api/questions/{id:int}/parse-manual-fix", (int id, ManualSubmitRequest req) =>
{
    try
    {
        var fix = ManualFixParser.Parse(req.ClaudeResponseText);
        return Results.Ok(fix);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

// --- Önizlenen düzeltmeyi onaylayıp DB'ye yaz ---
app.MapPost("/api/questions/{id:int}/apply-fix", async (int id, FixResult payload, AppDbContext db) =>
{
    var q = await db.Questions.FindAsync(id);
    if (q == null) return Results.NotFound();

    q.Option1 = payload.Option1;
    q.Option2 = payload.Option2;
    q.Option3 = payload.Option3;
    q.Option4 = payload.Option4;
    q.CorrectOption = payload.CorrectOption;
    q.IsFlagged = false;

    await db.SaveChangesAsync();
    return Results.Ok(q);
});

// --- Var olan tüm soruların embedding'ini toplu doldur (yeni DB'ye geçtikten sonra bir kez çalıştır) ---
app.MapPost("/api/questions/backfill-embeddings", async (AppDbContext db, EmbeddingService embeddings, ILogger<Program> logger, CancellationToken ct) =>
{
    var missing = await db.Questions.Where(q => q.EmbeddingJson == null).ToListAsync(ct);
    var semaphore = new SemaphoreSlim(4);
    var counters = (done: 0, failed: 0);
    var lockObj = new object();
    string? firstError = null;
    var dailyQuotaHit = false;

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

    var tasks = missing.Select(async q =>
    {
        try
        {
            await semaphore.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            if (cts.IsCancellationRequested) return;

            var emb = await embeddings.EmbedAsync(q.Text, cts.Token);
            q.EmbeddingJson = EmbeddingService.Serialize(emb);
            lock (lockObj) { counters.done++; }
        }
        catch (DailyQuotaExhaustedException ex)
        {
            lock (lockObj)
            {
                counters.failed++;
                firstError ??= ex.Message;
                dailyQuotaHit = true;
            }
            cts.Cancel();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            lock (lockObj)
            {
                counters.failed++;
                firstError ??= ex.Message;
            }
            logger.LogWarning(ex, "Embedding alınamadı, soru id={Id}", q.Id);
        }
        finally { semaphore.Release(); }
    });

    await Task.WhenAll(tasks);
    await db.SaveChangesAsync(ct);

    return Results.Ok(new { total = missing.Count, done = counters.done, failed = counters.failed, firstError, dailyQuotaHit });
});

// --- Var olan soru havuzunda tekrar eden/çok benzer ÇİFTLERİ bul (embedding gerekir) ---
app.MapGet("/api/questions/duplicates", async (AppDbContext db, IConfiguration config, double? threshold) =>
{
    var effectiveThreshold = threshold ?? config.GetValue("QuizGeneration:DuplicateSimilarityThreshold", 0.90);

    var withEmbeddings = await db.Questions
        .Where(q => q.EmbeddingJson != null)
        .ToListAsync();

    var pairs = new List<DuplicatePairResult>();

    foreach (var group in withEmbeddings.GroupBy(q => (q.ContentId, q.Language)))
    {
        var list = group.ToList();
        var embeddings = list.Select(q => EmbeddingService.Deserialize(q.EmbeddingJson)).ToList();

        for (int i = 0; i < list.Count; i++)
        {
            if (embeddings[i] is null) continue;
            for (int j = i + 1; j < list.Count; j++)
            {
                if (embeddings[j] is null) continue;
                var sim = EmbeddingService.CosineSimilarity(embeddings[i]!, embeddings[j]!);
                if (sim < effectiveThreshold) continue;

                pairs.Add(new DuplicatePairResult(
                    Math.Round(sim, 3),
                    list[i].MovieOrShowName,
                    list[i].Language,
                    new DuplicateQuestionRef(list[i].Id, list[i].Text, list[i].Option1, list[i].Option2, list[i].Option3, list[i].Option4),
                    new DuplicateQuestionRef(list[j].Id, list[j].Text, list[j].Option1, list[j].Option2, list[j].Option3, list[j].Option4)
                ));
            }
        }
    }

    var sorted = pairs.OrderByDescending(p => p.Similarity).ToList();
    return Results.Ok(new { totalWithEmbedding = withEmbeddings.Count, thresholdUsed = effectiveThreshold, pairsFound = sorted.Count, pairs = sorted });
});

// --- Tek bir soruyu kalıcı olarak sil (tekrar eden çiftlerden birini temizlemek için) ---
app.MapDelete("/api/questions/{id:int}", async (int id, AppDbContext db) =>
{
    var q = await db.Questions.FindAsync(id);
    if (q == null) return Results.NotFound();
    db.Questions.Remove(q);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = id });
});

// ============================================================
// MANUEL CLAUDE KÖPRÜSÜ (API ödemeden, claude.ai Pro hesabıyla)
// Gemini otomatik (API) çalışır. Claude tarafı 2 adımda kullanıcıdan alınır:
// 1) üretim prompt'u -> claude.ai'de çalıştır -> cevabı yapıştır
// 2) inceleme prompt'u -> claude.ai'de çalıştır -> cevabı yapıştır -> final kaydedilir
// ============================================================

app.MapPost("/api/generate/manual/start", async (ManualStartRequest req, AppDbContext db, ManualGenerationSessionService manual, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ContentName) && req.ContentId is null)
        return Results.BadRequest(new { error = "contentName ya da contentId'den biri gerekli." });
    if (req.QuestionCount <= 0)
        return Results.BadRequest(new { error = "questionCount pozitif olmalı." });
    if (string.IsNullOrWhiteSpace(req.ContentType))
        return Results.BadRequest(new { error = "contentType gerekli (\"movie\" veya \"series\")." });

    var content = req.ContentId is int cid
        ? await db.Contents.FirstOrDefaultAsync(c => c.Id == cid, ct)
        : await db.Contents.FirstOrDefaultAsync(c => EF.Functions.ILike(c.Name, req.ContentName), ct);

    if (content is null)
        return Results.NotFound(new { error = $"İçerik bulunamadı: '{req.ContentName}'." });

    // Languages verildiyse onu kullan; yoksa geriye dönük uyumluluk için tek Language.
    var languages = (req.Languages is { Count: > 0 })
        ? req.Languages.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList()
        : new List<string> { req.Language };

    try
    {
        var step = await manual.StartAsync(content, req.ContentType, languages, req.QuestionCount, req.ReferenceNote, ct);
        return Results.Ok(step);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

app.MapPost("/api/generate/manual/{sessionId:guid}/submit-generation", async (Guid sessionId, ManualSubmitRequest req, ManualGenerationSessionService manual, CancellationToken ct) =>
{
    try
    {
        var step = await manual.SubmitGenerationAsync(sessionId, req.ClaudeResponseText, ct);
        return Results.Ok(step);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

app.MapPost("/api/generate/manual/{sessionId:guid}/submit-review", async (Guid sessionId, ManualSubmitRequest req, ManualGenerationSessionService manual, CancellationToken ct) =>
{
    try
    {
        // Dönüş: ManualReviewOutcome - ya doğrudan sonuç (şüpheli yoksa) ya da onay bekleyen şüpheli liste.
        var result = await manual.SubmitReviewAsync(sessionId, req.ClaudeResponseText, ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

// --- Şüpheli-benzerlik kararları: kullanıcının tutmayı seçtiği şüpheli adayları kaydeder ---
app.MapPost("/api/generate/manual/{sessionId:guid}/submit-similar-decisions", async (Guid sessionId, SimilarityDecisionRequest req, ManualGenerationSessionService manual, CancellationToken ct) =>
{
    try
    {
        var result = await manual.SubmitSimilarityDecisionsAsync(sessionId, req.KeepIndices, ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

// --- Manuel toplu düzeltme: seçilen soruların HEPSİ için TEK prompt hazırla (API çağrısı YOK) ---
app.MapPost("/api/questions/batch-manual-fix-prompt", async (BatchFixPromptRequest req, AppDbContext db) =>
{
    if (req.Ids is null || req.Ids.Count == 0)
        return Results.BadRequest(new { error = "En az bir soru id'si gerekli." });

    var questions = await db.Questions.Where(q => req.Ids.Contains(q.Id)).ToListAsync();
    if (questions.Count == 0)
        return Results.NotFound(new { error = "Verilen id'lerle eşleşen soru bulunamadı." });

    var payload = questions.Select(q => new
    {
        id = q.Id,
        contentName = q.MovieOrShowName,
        text = q.Text,
        option1 = q.Option1,
        option2 = q.Option2,
        option3 = q.Option3,
        option4 = q.Option4,
        correctOption = q.CorrectOption
    });
    var questionsJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    var prompt = PromptTemplates.BuildBatchFixPrompt(questionsJson);

    return Results.Ok(new ManualStepResponse(
        Guid.Empty,
        "paste-claude-batch-fix",
        prompt,
        $"{questions.Count} soru için TEK prompt hazırlandı. Kopyala, claude.ai'de çalıştır, cevabı (JSON DİZİSİ) aşağıya yapıştır."
    ));
});

app.MapPost("/api/questions/parse-batch-manual-fix", (ManualSubmitRequest req) =>
{
    try
    {
        var fixes = ManualFixParser.ParseBatch(req.ClaudeResponseText);
        return Results.Ok(fixes);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

app.MapPost("/api/questions/apply-batch-fix", async (ApplyBatchFixRequest req, AppDbContext db) =>
{
    var fixIds = req.Fixes.Select(f => f.Id).ToHashSet();
    var expectedIds = req.ExpectedIds.ToHashSet();
    var missing = expectedIds.Except(fixIds).ToList();
    var unexpected = fixIds.Except(expectedIds).ToList();

    if (missing.Count > 0 || unexpected.Count > 0)
    {
        return Results.BadRequest(new
        {
            error = "Yapıştırılan cevap bu grup için beklenen sorularla eşleşmiyor - muhtemelen yanlış/eski bir cevap yapıştırıldı. HİÇBİR ŞEY kaydedilmedi.",
            missingIds = missing,
            unexpectedIds = unexpected
        });
    }

    int applied = 0, notFound = 0;

    foreach (var f in req.Fixes)
    {
        var q = await db.Questions.FindAsync(f.Id);
        if (q is null) { notFound++; continue; }

        q.Option1 = f.Option1;
        q.Option2 = f.Option2;
        q.Option3 = f.Option3;
        q.Option4 = f.Option4;
        q.CorrectOption = f.CorrectOption;
        q.IsFlagged = false;
        applied++;
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { applied, notFound });
});

// ============================================================
// TOPLU ÜRETİM (tüm içerikler, 2 Gemini personası + web grounding)
// Uzun sürer → arka planda job. Başlat, jobId al, ilerlemeyi sorgula.
// ============================================================

app.MapPost("/api/generate/bulk", (BulkGenerateRequest req, BulkGenerationService bulk) =>
{
    if (req.Languages is null || req.Languages.Count == 0)
        return Results.BadRequest(new { error = "En az bir dil belirtilmeli (tr/en)." });
    if (req.QuestionsPerLanguage <= 0)
        return Results.BadRequest(new { error = "questionsPerLanguage pozitif olmalı." });

    var job = bulk.Start(req);
    return Results.Ok(job.ToStatus());
});

app.MapGet("/api/generate/bulk/{jobId:guid}", (Guid jobId, BulkGenerationService bulk) =>
{
    var job = bulk.Get(jobId);
    return job is null ? Results.NotFound(new { error = "Job bulunamadı (süreç yeniden başlamış olabilir)." })
                       : Results.Ok(job.ToStatus());
});

app.MapPost("/api/generate/bulk/{jobId:guid}/cancel", (Guid jobId, BulkGenerationService bulk) =>
{
    return bulk.Cancel(jobId) ? Results.Ok(new { canceled = jobId })
                              : Results.NotFound(new { error = "Job bulunamadı." });
});

// ============================================================
// SON EKLENENLER / ONAY BEKLEYENLER (toplu üretim çıktısını gözden geçirme)
// ============================================================

// --- En son eklenen soruları yeni→eski listele (opsiyonel: sadece taslaklar / belirli içerik) ---
app.MapGet("/api/questions/recent", async (AppDbContext db, int? limit, bool? onlyDrafts, int? contentId, string? aiModel) =>
{
    var take = Math.Clamp(limit ?? 100, 1, 500);
    var query = db.Questions.AsQueryable();
    if (onlyDrafts == true) query = query.Where(q => !q.IsApproved);
    if (contentId is int cid) query = query.Where(q => q.ContentId == cid);
    if (!string.IsNullOrWhiteSpace(aiModel)) query = query.Where(q => q.AiModel == aiModel);

    var items = await query
        .OrderByDescending(q => q.CreatedAt).ThenByDescending(q => q.Id)
        .Take(take)
        .Select(q => new RecentQuestionDto(
            q.Id, q.Text, q.Option1, q.Option2, q.Option3, q.Option4,
            q.CorrectOption, q.Difficulty, q.Points, q.Language,
            q.MovieOrShowName, q.ContentId, q.AiModel,
            q.IsApproved, q.IsFlagged, q.CreatedAt))
        .ToListAsync();

    var pendingDrafts = await db.Questions.CountAsync(q => !q.IsApproved);
    return Results.Ok(new { count = items.Count, pendingDrafts, items });
});

// --- Seçili soruları onayla (taslak → canlı: IsApproved=true) ---
app.MapPost("/api/questions/approve", async (IdListRequest req, AppDbContext db) =>
{
    if (req.Ids is null || req.Ids.Count == 0)
        return Results.BadRequest(new { error = "En az bir soru id'si gerekli." });

    var qs = await db.Questions.Where(q => req.Ids.Contains(q.Id)).ToListAsync();
    foreach (var q in qs) q.IsApproved = true;
    await db.SaveChangesAsync();
    return Results.Ok(new { approved = qs.Count });
});

// --- Seçili soruları kalıcı olarak sil (yanlış/istenmeyen toplu çıktıyı temizlemek için) ---
app.MapPost("/api/questions/delete-batch", async (IdListRequest req, AppDbContext db) =>
{
    if (req.Ids is null || req.Ids.Count == 0)
        return Results.BadRequest(new { error = "En az bir soru id'si gerekli." });

    var qs = await db.Questions.Where(q => req.Ids.Contains(q.Id)).ToListAsync();
    db.Questions.RemoveRange(qs);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = qs.Count });
});

app.Run();