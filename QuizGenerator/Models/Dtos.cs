namespace QuizGenerator.Models;

public record GenerateRequest(
    string ContentName,          // İçerik adı - ContentId verilmezse bununla aranır
    string ContentType,          // "movie" | "series" - Content.Type (int, anlamı bilinmiyor) ile İLGİSİZ, direkt kullanılır
    int QuestionCount,           // 10 veya 20
    List<string> Languages,      // ["tr","en"]
    int? ContentId = null,       // Biliniyorsa direkt ID ver, isimle arama atlanır
    bool ReplaceExisting = false,// true: o içerik+dil için eski AI sorularını siler; false (varsayılan): üzerine ekler
    string? ReferenceNote = null // Doğrulanmış karakter/bilgi notu - isim uydurmayı önlemek için prompt'a KESİN KAYNAK olarak eklenir
);

public class GeneratedQuestion
{
    public string Text { get; set; } = "";
    public string Option1 { get; set; } = "";
    public string Option2 { get; set; } = "";
    public string Option3 { get; set; } = "";
    public string Option4 { get; set; } = "";
    public int CorrectOption { get; set; }
    public string Difficulty { get; set; } = "easy";
    public int Points { get; set; }
}

public class ReviewVerdict
{
    public int Index { get; set; }
    public bool Approved { get; set; }
    public string Reason { get; set; } = "";
    public List<string> ViolatedRules { get; set; } = new();
}

public class ReviewResponse
{
    public List<ReviewVerdict> Verdicts { get; set; } = new();
}

/// <summary>
/// Üretim sonucunda API response'ta dönen soru gösterimi.
/// QuestionEntity'den FARKLI: ReviewedBy burada var ama DB'ye hiç yazılmıyor
/// (kullanıcı tercihi - sadece bu response'ta, geçici olarak gösteriliyor).
/// </summary>
public record GeneratedQuestionResult(
    int Id,
    string Text,
    string Option1,
    string Option2,
    string Option3,
    string Option4,
    int CorrectOption,
    string Difficulty,
    int Points,
    string GeneratedBy,
    string ReviewedBy
);

public record LanguageResult(
    string Language,
    int Requested,
    int Delivered,
    List<GeneratedQuestionResult> Questions,
    List<string> Warnings
);

public record GenerateResponse(
    int ContentId,
    string ContentName,
    string? Genre,
    List<LanguageResult> Results
);

public record ContentSearchResult(int Id, string Name, string? Genre, bool IsActive);

public record DuplicateQuestionRef(int Id, string Text, string Option1, string Option2, string Option3, string Option4);

public record DuplicatePairResult(double Similarity, string ContentName, string Language, DuplicateQuestionRef Question1, DuplicateQuestionRef Question2);

// --- Manuel Claude köprüsü (API ödemeden, claude.ai Pro hesabıyla) ---
// Language: birincil dil (üretim + çapraz denetim bu dilde yapılır).
// Languages: tüm hedef diller (["tr","en"]). Birden fazlaysa, birincil dışındaki diller
// final set üretildikten sonra Gemini ile otomatik çevrilip AYNI sorular olarak kaydedilir.
public record ManualStartRequest(string ContentName, string ContentType, int QuestionCount, string Language, int? ContentId = null, List<string>? Languages = null, string? ReferenceNote = null);

public record ManualStepResponse(Guid SessionId, string Step, string PromptToCopy, string Instructions);

public record ManualSubmitRequest(string ClaudeResponseText);

// --- Benzerlik "şüpheli bandı" kullanıcı onayı ---
// İnceleme adımından sonra, benzerliği reviewThreshold ile dupThreshold arasında kalan adaylar
// otomatik kaydedilmez; bu yapı ile kullanıcıya sunulur. Kullanıcı Index'lerden hangilerini
// tutacağını seçer.
public record SuspiciousQuestionDto(
    int Index,               // session.Suspicious listesindeki sıra - karar bu index'lerle verilir
    string GeneratedBy,      // "claude" | "gemini"
    double Similarity,       // 0-1 arası, benzediği soruyla kosinüs benzerliği
    string SimilarToText,    // benzediği (mevcut ya da bu turda kabul edilmiş) soru metni
    string Text,
    string Option1,
    string Option2,
    string Option3,
    string Option4,
    int CorrectOption,
    string Difficulty,
    int Points
);

// submit-review yanıtı: ya doğrudan sonuç (şüpheli yoksa) ya da onay bekleyen şüpheli listesi.
public record ManualReviewOutcome(bool NeedsReview, LanguageResult? Result, List<SuspiciousQuestionDto>? Suspicious);

// Kullanıcının tutmak istediği şüpheli adayların index'leri.
public record SimilarityDecisionRequest(List<int> KeepIndices);

// Var olan, cevap sızıntısı tespit edilmiş bir soruyu düzeltmek için AI'dan istenen/dönen şık seti
public record FixResult(string Option1, string Option2, string Option3, string Option4, int CorrectOption);

// --- Toplu manuel düzeltme (birden fazla soru, TEK prompt) ---
public record BatchFixItem(int Id, string Option1, string Option2, string Option3, string Option4, int CorrectOption);
public record BatchFixPromptRequest(List<int> Ids);
public record ApplyBatchFixRequest(List<BatchFixItem> Fixes, List<int> ExpectedIds);

// --- TOPLU ÜRETİM (tüm içerikler için, 2 Gemini personası + web grounding) ---
// Uzun sürdüğü için arka planda job olarak çalışır: başlat -> jobId -> ilerleme sorgula.
public record BulkGenerateRequest(
    List<string> Languages,             // ["tr","en"] — her içerik için hangi dillerde üretilsin
    int QuestionsPerLanguage = 10,      // her içerik+dil için hedeflenen YENİ soru sayısı
    string DefaultContentType = "series", // grounding tip tespit edemezse kullanılır ("movie"/"series")
    bool IncludeInactive = false,       // false: sadece IsActive=true içerikler
    bool UseGrounding = true,           // web araması ile karakter notu enjekte edilsin mi
    int? CategoryId = null,             // sadece bu türdeki (genre) içerikler; null = hepsi
    int? Limit = null,                  // en fazla kaç içerik işlensin (test için); null = hepsi
    int? StartAfterContentId = null,    // bu Id'den SONRAKİ içeriklerden başla (kotadan sonra devam için)
    int DelayMsBetweenContents = 0,     // içerikler arası bekleme (dakikalık limiti rahatlatmak için)
    bool SaveAsDraft = true             // true: IsApproved=false (onay bekler); false: doğrudan canlı (IsApproved=true)
);

// Bir soru listesi üzerinde toplu işlem (onayla / sil) için ortak istek gövdesi.
public record IdListRequest(List<int> Ids);

// "Son Eklenenler / Onay Bekleyenler" listesindeki bir soru satırı.
public record RecentQuestionDto(
    int Id, string Text, string Option1, string Option2, string Option3, string Option4,
    int CorrectOption, string Difficulty, int Points, string Language,
    string MovieOrShowName, int ContentId, string? AiModel,
    bool IsApproved, bool IsFlagged, DateTime CreatedAt
);

// İlerleme anlık görüntüsü (GET ile sorgulanır).
public record BulkJobStatus(
    Guid JobId,
    string Status,                      // running | completed | stopped_quota | canceled | failed
    int TotalContents,
    int ProcessedContents,
    string? CurrentContentName,
    int TotalQuestionsSaved,
    int GroundedCount,                  // grounding'in başarıyla uygulandığı içerik sayısı
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? Message,                    // kota/hata mesajı gibi genel bilgi
    int? LastProcessedContentId,        // kaldığı yer (devam için StartAfterContentId'ye verilebilir)
    List<BulkContentResult> Recent      // son işlenen içeriklerin özeti (en yeni önce)
);

public record BulkContentResult(
    int ContentId,
    string ContentName,
    string ContentType,
    bool Grounded,
    int SavedTotal,
    List<BulkLangCount> PerLanguage,
    List<string> Warnings
);

public record BulkLangCount(string Language, int Requested, int Saved);