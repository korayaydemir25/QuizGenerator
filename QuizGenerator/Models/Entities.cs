namespace QuizGenerator.Models;

/// <summary>
/// "Content" tablosunun EF Core karşılığı - SADECE OKUMA amaçlı.
/// Type ve Origin alanlarının gerçek enum karşılıkları bilinmiyor (int olarak saklanıyor),
/// bu yüzden bu servis onları YORUMLAMAYA çalışmaz, sadece Name ve CategoryId okur.
/// </summary>
public class ContentEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Type { get; set; }       // Anlamı bilinmiyor - dokunulmuyor
    public int Origin { get; set; }     // Anlamı bilinmiyor - dokunulmuyor
    public int CategoryId { get; set; } // Categories'e FK (genre)
    public bool IsActive { get; set; }
}

/// <summary>
/// "Categories" tablosunun EF Core karşılığı - SADECE OKUMA amaçlı (genre listesi).
/// </summary>
public class CategoryEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Type { get; set; } = ""; // şu an hep "genre"
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// "Questions" tablosunun EF Core karşılığı - gerçek production şemasıyla birebir eşleşir.
/// NOT: "ReviewedBy" burada YOK (kullanıcı kararı: DB'ye yazılmasın, sadece üretim anında
/// API response'ta gösterilsin - bkz. Services/CrossReviewOrchestrator.cs).
/// NOT: Denetim (audit) sonucu için var olan "IsFlagged" kolonu yeniden kullanılıyor,
/// ayrı bir "AuditReason" kolonu YOK - sebep sadece API response'ta döner, kalıcı değil.
/// </summary>
public class QuestionEntity
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public string Option1 { get; set; } = "";
    public string Option2 { get; set; } = "";
    public string Option3 { get; set; } = "";
    public string Option4 { get; set; } = "";
    public int CorrectOption { get; set; } // 0-3
    public string Difficulty { get; set; } = "easy"; // easy | medium | hard
    public int Points { get; set; }
    public string Language { get; set; } = ""; // "tr" | "en"

    // Content tablosundan denormalize edilmiş kopyalar (hızlı okuma için)
    public string MovieOrShowName { get; set; } = "";
    public string ContentType { get; set; } = ""; // "movie" | "series"

    public int CategoryId { get; set; }  // Content.CategoryId'den kopyalanır (genre)
    public int ContentId { get; set; }   // Content tablosuna FK

    public bool IsApproved { get; set; }
    public bool IsAiGenerated { get; set; } = true;
    public string? AiModel { get; set; } // "claude" | "gemini" - ÜRETEN taraf

    public int TimesPlayed { get; set; }
    public int TimesCorrect { get; set; }

    public bool IsFlagged { get; set; } // heuristik denetimde sorun bulunursa true

    // Anlam vektörü (embedding) - JSON float dizisi olarak saklanır.
    // Benzer/tekrar soruları yakalamak için kullanılır. Null ise henüz hesaplanmamış demektir
    // (ilk kez bir üretim/backfill turunda karşılaşıldığında otomatik doldurulur).
    public string? EmbeddingJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
