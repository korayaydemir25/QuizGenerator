using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using QuizGenerator.Data;
using QuizGenerator.Models;

namespace QuizGenerator.Services;

/// <summary>
/// TOPLU ÜRETİM: var olan TÜM içerikler (Content) için, iki Gemini personasıyla (Claude API'siz
/// çeşitlilik) ve web grounding ile (isim uydurmayı engellemek için) soru üretir. Yüzlerce içerik
/// olabileceğinden HTTP isteğini bekletmek yerine arka planda job olarak çalışır: kullanıcı jobId
/// alır, ilerlemeyi ayrı bir uçtan sorgular.
///
/// Singleton'dır. Her içerik için AYRI bir DI scope açar (arka plan Task'ı istek scope'unun
/// DbContext'ini paylaşamaz; DbContext thread-safe değil). Gemini günlük kotası dolarsa
/// (DailyQuotaExhaustedException) iş "stopped_quota" ile zarifçe durur; kullanıcı ertesi gün
/// LastProcessedContentId'yi StartAfterContentId olarak verip kaldığı yerden devam edebilir.
/// </summary>
public class BulkGenerationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BulkGenerationService> _logger;
    private readonly ConcurrentDictionary<Guid, BulkJob> _jobs = new();

    public BulkGenerationService(IServiceScopeFactory scopeFactory, ILogger<BulkGenerationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public BulkJob Start(BulkGenerateRequest req)
    {
        var job = new BulkJob(req);
        _jobs[job.Id] = job;
        // Uzun süren işi arka planda başlat; isteği bekletme.
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    public BulkJob? Get(Guid id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public bool Cancel(Guid id)
    {
        if (_jobs.TryGetValue(id, out var j))
        {
            j.Cts.Cancel();
            return true;
        }
        return false;
    }

    private async Task RunAsync(BulkJob job)
    {
        var ct = job.Cts.Token;
        var req = job.Request;
        var languages = (req.Languages is { Count: > 0 })
            ? req.Languages.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().ToList()
            : new List<string> { "tr" };

        try
        {
            List<ContentEntity> contents;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var query = db.Contents.AsQueryable();
                if (!req.IncludeInactive) query = query.Where(c => c.IsActive);
                if (req.CategoryId is int cat) query = query.Where(c => c.CategoryId == cat);
                if (req.StartAfterContentId is int after) query = query.Where(c => c.Id > after);
                query = query.OrderBy(c => c.Id);
                if (req.Limit is int lim) query = query.Take(lim);
                contents = await query.ToListAsync(ct);
            }

            job.TotalContents = contents.Count;
            if (contents.Count == 0)
            {
                job.Finish("completed", "İşlenecek içerik bulunamadı (filtrelere uyan içerik yok).");
                return;
            }

            foreach (var content in contents)
            {
                if (ct.IsCancellationRequested)
                {
                    job.Finish("canceled", "Kullanıcı tarafından iptal edildi.");
                    return;
                }

                job.CurrentContentName = content.Name;

                try
                {
                    var result = await ProcessContentAsync(content, req, languages, ct);
                    job.AddResult(result);
                }
                catch (DailyQuotaExhaustedException ex)
                {
                    // Kota doldu — bu içeriği tamamlayamadık; bir öncekinden devam edilmeli.
                    job.LastProcessedContentId = job.Results.LastOrDefault()?.ContentId;
                    job.Finish("stopped_quota",
                        $"Gemini günlük kotası doldu, iş durduruldu. Yarın (ya da faturalandırma açarak) " +
                        $"'StartAfterContentId={job.LastProcessedContentId}' ile devam edebilirsin. Detay: {ex.Message}");
                    return;
                }
                catch (OperationCanceledException)
                {
                    job.Finish("canceled", "Kullanıcı tarafından iptal edildi.");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "İçerik işlenemedi: {Content}", content.Name);
                    job.AddResult(new BulkContentResult(
                        content.Id, content.Name, req.DefaultContentType, false, 0,
                        new List<BulkLangCount>(), new List<string> { $"İçerik hatası: {ex.Message}" }));
                }

                job.LastProcessedContentId = content.Id;

                if (req.DelayMsBetweenContents > 0 && !ct.IsCancellationRequested)
                    await Task.Delay(req.DelayMsBetweenContents, ct);
            }

            job.Finish("completed", null);
        }
        catch (OperationCanceledException)
        {
            job.Finish("canceled", "Kullanıcı tarafından iptal edildi.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toplu üretim işi çöktü.");
            job.Finish("failed", $"Beklenmeyen hata: {ex.Message}");
        }
    }

    private async Task<BulkContentResult> ProcessContentAsync(
        ContentEntity content, BulkGenerateRequest req, List<string> languages, CancellationToken ct)
    {
        // Her içerik için taze bir scope — kendi DbContext'i ve scoped servisleri.
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var orchestrator = sp.GetRequiredService<CrossReviewOrchestrator>();
        var grounding = sp.GetRequiredService<GroundingService>();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var config = sp.GetRequiredService<IConfiguration>();

        // İçeriği bu scope'un context'ine bağla (orchestrator bu db üzerinden kaydeder).
        var localContent = await db.Contents.FirstOrDefaultAsync(c => c.Id == content.Id, ct) ?? content;

        // "auto" (Karışık) seçildiyse: tip önce grounding ile içerikten TESPİT edilir; tespit
        // edilemezse güvenli yedek "series" kullanılır (Türkçe katalog çoğunlukla dizi). "series"/"movie"
        // seçildiyse hepsi o tiple işlenir.
        var isAuto = string.Equals(req.DefaultContentType, "auto", StringComparison.OrdinalIgnoreCase);
        var fallbackType = isAuto ? "series"
            : (string.Equals(req.DefaultContentType, "movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "series");
        var contentType = fallbackType;
        var typeDetected = false;
        string? note = null;
        var grounded = false;
        var warnings = new List<string>();

        if (req.UseGrounding)
        {
            var g = await grounding.GetGroundingAsync(localContent.Name, fallbackType, ct);
            if (g.Succeeded)
            {
                note = g.Note;
                grounded = true;
                if (!string.IsNullOrWhiteSpace(g.DetectedType)) { contentType = g.DetectedType!; typeDetected = true; }
            }
            else
            {
                warnings.Add($"Grounding uygulanamadı, grounding'siz üretildi: {g.FailureReason}");
            }
        }

        if (isAuto && !typeDetected)
            warnings.Add($"İçerik tipi otomatik algılanamadı, '{fallbackType}' varsayıldı.");

        // İki Gemini personası — bilerek farklı odak/sıcaklık (çeşitlilik). Claude API'ye gerek yok.
        var genA = new GeminiQuestionGenerator(factory, config, GeminiPersona.BulkA);
        var genB = new GeminiQuestionGenerator(factory, config, GeminiPersona.BulkB);

        var perLang = new List<BulkLangCount>();
        var savedTotal = 0;

        foreach (var lang in languages)
        {
            // replaceExisting=false: var olan sorulara BAKAR (embedding ile) ve benzer olmayan
            // YENİ sorular ekler — kullanıcının istediği davranış.
            var langResult = await orchestrator.GenerateForLanguageAsync(
                genA, genB, localContent, contentType, lang, req.QuestionsPerLanguage,
                replaceExisting: false, referenceNote: note, ct, markApproved: !req.SaveAsDraft);

            perLang.Add(new BulkLangCount(lang, langResult.Requested, langResult.Delivered));
            savedTotal += langResult.Delivered;

            // Sadece dikkat çeken uyarıları taşı (uzunluğu sınırla — job durumu şişmesin).
            foreach (var w in langResult.Warnings.Take(3))
                warnings.Add($"[{lang}] {w}");
        }

        return new BulkContentResult(
            localContent.Id, localContent.Name, contentType, grounded, savedTotal, perLang, warnings);
    }
}

/// <summary>Tek bir toplu üretim işinin süreç-içi (in-memory) durumu. Süreç yeniden başlarsa kaybolur
/// (kişisel araç için kabul edilebilir — iş yeniden başlatılır ve dedup sayesinde ikileme olmaz).</summary>
public class BulkJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public BulkGenerateRequest Request { get; }
    public CancellationTokenSource Cts { get; } = new();

    public string Status { get; private set; } = "running";
    public int TotalContents { get; set; }
    public int ProcessedContents { get; private set; }
    public string? CurrentContentName { get; set; }
    public int TotalQuestionsSaved { get; private set; }
    public int GroundedCount { get; private set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; private set; }
    public string? Message { get; private set; }
    public int? LastProcessedContentId { get; set; }
    public List<BulkContentResult> Results { get; } = new();

    private readonly object _lock = new();

    public BulkJob(BulkGenerateRequest request) => Request = request;

    public void AddResult(BulkContentResult r)
    {
        lock (_lock)
        {
            Results.Add(r);
            ProcessedContents++;
            TotalQuestionsSaved += r.SavedTotal;
            if (r.Grounded) GroundedCount++;
        }
    }

    public void Finish(string status, string? message)
    {
        lock (_lock)
        {
            Status = status;
            Message = message;
            FinishedAt = DateTime.UtcNow;
            CurrentContentName = null;
        }
    }

    public BulkJobStatus ToStatus()
    {
        lock (_lock)
        {
            var recent = Results.AsEnumerable().Reverse().Take(15).ToList();
            return new BulkJobStatus(
                Id, Status, TotalContents, ProcessedContents, CurrentContentName,
                TotalQuestionsSaved, GroundedCount, StartedAt, FinishedAt, Message,
                LastProcessedContentId, recent);
        }
    }
}
