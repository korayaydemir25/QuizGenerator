# CineQuiz Soru Üretim Konsolu

Claude ve Gemini'yi **paralel çalıştırıp birbirinin ürettiği soruları çapraz denetleten** bir soru üretim sistemi. Senin **gerçek production veritabanına** (`Questions`, `Content`, `Categories` tabloları) doğrudan okuyup yazar — kendi paralel şemasını icat etmez.

> ⚠️ **Önemli not:** Bu kod NuGet erişimi olmayan bir sandbox'ta yazıldı, yani `dotnet build` ile derleyip test edemedim. Mantık uçtan uca doğrulandı ama ilk çalıştırmada küçük bir sözdizimi hatası çıkarsa şaşırma.

## Bu servis senin şemana göre nasıl davranıyor

Ekran görüntülerinden çıkardığımız gerçek şema:

**Content** (Id, Name, Type[int - anlamı bilinmiyor], Origin[int - anlamı bilinmiyor], CategoryId, IsActive)
**Categories** (Id, Name, DisplayName, Icon, Type["genre"], IsActive, CreatedAt)
**Questions** (Id, Text, Option1-4, CorrectOption, Difficulty, Points, Language, MovieOrShowName, ContentType, CategoryId, IsApproved, IsAiGenerated, AiModel, TimesPlayed, TimesCorrect, CreatedAt, ContentId, IsFlagged)

Bundan çıkan tasarım kararları:

1. **İçerik önceden var olmalı.** `Content.Type` ve `Content.Origin` int ama gerçek enum karşılıklarını bilmiyoruz. Bu yüzden bu servis **yeni Content satırı OLUŞTURMUYOR** — yanlış enum değeriyle satır oluşturmak ana uygulamanın davranışını bozabilir. `/api/generate` çağrıldığında içerik `Content` tablosunda `contentName` ile (veya doğrudan `contentId` ile) aranır, bulunamazsa 400/404 döner ve önce ana backend'den/admin panelden eklemeni ister.
2. **CategoryId, Content'ten kopyalanıyor.** `Categories.Type` hep `"genre"` olduğu için `CategoryId`'nin senin prompt'undaki "karakter/mekan/eşya" gibi bir soru-tipi DEĞİL, film/dizinin türü (drama, thriller, vb.) olduğunu varsaydık. Her üretilen soru, bağlı olduğu `Content.CategoryId`'yi kopyalar. **Bu varsayımı bir join sorgusuyla doğrulaman iyi olur** (bkz. aşağıda).
3. **Varsayılan olarak EKLER, SİLMEZ.** `TimesPlayed`/`TimesCorrect` gibi oynanma istatistikleri olduğu için, aynı içerik+dil için tekrar üretim yapınca eski soruları otomatik silmiyoruz (istatistik kaybı olmasın diye). İstersen `replaceExisting: true` ile eski AI-üretimi soruları silip baştan oluşturabilirsin — bu durumda o sorulara ait oynanma geçmişi de gider, dikkatli kullan.
4. **`ReviewedBy` DB'ye yazılmıyor** (senin kararın) — sadece `/api/generate` response'unda, o an için gösteriliyor.
5. **`IsFlagged` yeniden kullanılıyor.** Kendi "AuditFlagged" kolonumu icat etmek yerine zaten var olan `IsFlagged`'i denetim sonucunu işaretlemek için kullanıyorum. Flag SEBEBİ kalıcı değil, sadece `/api/questions/audit` her çalıştığında response'ta dönüyor.
6. **Migration YOK.** `Program.cs` içinde `db.Database.Migrate()` BİLEREK çağrılmıyor — bu tablolar ana backend'in migration'larına ait, bu servis sadece sidecar olarak bağlanıyor. İki ayrı migration geçmişinin aynı tablolara dokunması riskli olurdu.
7. **Benzer/tekrar soru koruması (embedding tabanlı).** `Questions.EmbeddingJson` (yeni, nullable kolon) her sorunun Gemini `gemini-embedding-001` ile çıkarılmış anlam vektörünü JSON float dizisi olarak saklar. Üretim sırasında iki katman çalışır:
   - **Önleyici:** Prompt'a o içerik+dil için zaten var olan son ~60 soru eklenir, AI'ya "bunları tekrar etme" denir.
   - **Doğrulayıcı:** Her aday soru embed edilip hem var olan sorularla hem AYNI TURDA üretilen diğer adaylarla (Claude/Gemini birbirinden bağımsız benzer soru üretebilir) kosinüs benzerliği karşılaştırılır. `QuizGeneration:DuplicateSimilarityThreshold` (varsayılan 0.90) eşiğini aşan soru elenir, yerine yenisi üretilir.

   Eski sorularda `EmbeddingJson` boşsa (yeni DB'ye taşındıktan hemen sonra hepsi böyle olacak), bir üretim çalıştığında karşılaştığı eski soruları otomatik "backfill" eder. Ama en verimlisi: **yeni DB'ye geçtikten hemen sonra bir kez** şunu çağır:
   ```
   POST /api/questions/backfill-embeddings
   ```
   (Arayüzde "Denetle" sekmesinde "Embedding'leri Doldur" butonu da aynı işi yapar.) Bu, tüm mevcut soruları tek seferde embed eder, sonraki üretimler daha hızlı başlar.

## Toplu Üretim — "her dizi/film için" (Claude API'siz)

Arayüzdeki **"Toplu Üret"** sekmesi (ya da `POST /api/generate/bulk`) veritabanındaki **her içerik** için soru üretir. Claude API'sine para vermeden çeşitlilik ve isim doğruluğu için üç mekanizma birlikte çalışır:

1. **İki Gemini personası** (Claude yerine). Aynı Gemini modeli, bilerek **farklı odak + farklı sıcaklıkla** iki kez çalışır: `gemini-a` olaylar/mekanlar/semboller/repliklere, `gemini-b` ilişkiler/motivasyonlar/temalara ağırlık verir. Biri üretir, **diğeri denetler** (çapraz-denetim mantığı birebir korunur). İkisi aynı soruyu üretirse embedding tekrar-filtresi eler → "sorular aynı olmasın" garantisi.
2. **Web grounding** (isim uydurmaya karşı). Her içerik için önce Gemini'nin **Google Search grounding** aracıyla içerik adı gerçekten aranır, doğrulanmış karakter/ilişki notu çıkarılır ve üretim prompt'una **KESİN KAYNAK** olarak enjekte edilir. Grounding ayrıca film/dizi tipini de tespit eder. Başarısız olursa (araç desteklenmiyor/kota) o içerik grounding'siz üretilir, iş durmaz.
3. **Var olana bakıp benzer olmayan ekler.** `replaceExisting=false` ile her içerik+dil için mevcut sorular embedding'le karşılaştırılır, sadece **yeni/farklı** sorular eklenir.

**Body:**
```json
{
  "languages": ["tr","en"],
  "questionsPerLanguage": 10,
  "defaultContentType": "series",
  "useGrounding": true,
  "includeInactive": false,
  "categoryId": null,
  "limit": 5,
  "startAfterContentId": null,
  "delayMsBetweenContents": 0,
  "saveAsDraft": true
}
```

**Önemli davranışlar:**
- **Arka plan job.** İstek anında `jobId` döner; ilerleme `GET /api/generate/bulk/{jobId}` ile izlenir (arayüz otomatik yapar). İş süreç-içi (in-memory) tutulur — uygulama yeniden başlarsa job durumu kaybolur (ama kaydedilmiş sorular DB'de kalır).
- **Kota dolunca zarifçe durur.** Gemini ücretsiz katmanının günlük kotası dolarsa iş `stopped_quota` durumuna geçer ve **kaldığı içeriğin Id'sini** bildirir. Ertesi gün (ya da faturalandırma açarak) bu Id'yi `startAfterContentId` olarak verip **kaldığın yerden devam** edersin. Tekrar/ikileme olmaz (dedup zaten var).
- **Önce küçük dene.** İlk seferinde `limit: 1` veya `2` ile çalıştırıp çıktıyı gör; sonra limiti kaldırıp hepsine sal.
- **Dakikalık limit dayanıklılığı.** Üretim çağrıları da (embedding gibi) artık 429'da Google'ın `retryDelay`'ini bekleyip otomatik tekrar dener.
- **Varsayılan: onaysız taslak.** `saveAsDraft: true` (varsayılan) ile sorular **IsApproved=false** kaydedilir — yani "Son Eklenenler" sekmesinden onaylayana kadar taslakta durur. `false` verirsen doğrudan canlı (IsApproved=true) yazar.
  > ⚠️ Taslak modu oyuncuları **ancak ana uygulaman yalnızca IsApproved=true soruları gösteriyorsa** korur. Emin değilsen: bir tane taslak üret, admin sayfasında (`/admin/`) görünüyor mu diye bak. Görünüyorsa uygulama onaya bakmıyor demektir — o zaman taslak modu sadece bir "kuyruk" işlevi görür, gerçek koruma sağlamaz; yanlışları hızlıca silmen gerekir.

## Son Eklenenler / Onay Bekleyenler (toplu çıktıyı gözden geçirme)

Toplu üretim doğrudan production DB'ye yazdığı için, çıktısını gözden geçirecek bir yer gerekiyordu. Arayüzdeki **"Son Eklenenler"** sekmesi bunu sağlar:

- En son eklenen soruları **yeni→eski** listeler (içerik adı, üreten model, zorluk, tarih, `canlı`/`onay bekliyor` rozeti).
- **"Sadece onay bekleyenler"** filtresi + içerik adına göre arama.
- Çoklu seçim → **Seçilenleri Onayla** (taslak → canlı) veya **Seçilenleri Sil** (kalıcı).
- Satır başına **Düzelt** (mevcut manuel Claude düzeltme köprüsünü kullanır) ve **Sil**.

İlgili uçlar:

| Metot | Yol | Açıklama |
|---|---|---|
| GET | `/api/questions/recent?limit=100&onlyDrafts=true` | En son eklenen soruları (opsiyonel: sadece taslaklar / içerik / model) yeni→eski döner; `pendingDrafts` = tüm DB'deki onay bekleyen sayısı |
| POST | `/api/questions/approve` | `{ ids: [...] }` — seçili soruları onaylar (IsApproved=true) |
| POST | `/api/questions/delete-batch` | `{ ids: [...] }` — seçili soruları kalıcı siler |

**Tipik güvenli akış:** Toplu Üret (taslak) → Son Eklenenler'de gözden geçir → yanlışları sil / bozukları düzelt → doğruları toplu onayla.

## Manuel Claude Köprüsü (API'ye ödeme yapmadan)

Claude Pro/Max aboneliği, Claude API'sini KAPSAMAZ (Anthropic'in resmi politikası — ikisi ayrı faturalandırılır). Eğer API'ye ekstra ödeme yapmak istemiyorsan, arayüzdeki **"Manuel Claude Modu"** kutusunu işaretle:

- **Gemini tarafı yine tam otomatik** çalışır (API, ücretsiz katman yeterli).
- **Claude tarafı API üzerinden çağrılmaz.** Sistem sana prompt'u gösterir → sen claude.ai'de (Pro hesabınla) çalıştırırsın → cevabı geri yapıştırırsın. İki adım: önce Claude'un soru üretmesi, sonra Claude'un Gemini'nin sorularını incelemesi.
- Çapraz denetim mantığı (kim kimi inceliyor, heuristik denetim, embedding benzerlik filtresi) **birebir aynı** — sadece Claude'un API çağrısı yerine senin elle yaptığın bir adım var.

**Kısıtlar:**
- Manuel mod **tek tur** çalışır (otomatik moddaki gibi 3 tur yeniden deneme yok) — daha büyük bir tampon (buffer) ile telafi ediliyor ama nadiren hedeflenen soru sayısına tam ulaşamayabilir (warning ile bildirilir, tekrar üretim başlatabilirsin).
- `replaceExisting` (eskiyi silip baştan oluşturma) seçeneği şu an sadece otomatik moddan destekleniyor.
- Birden fazla dil seçtiysen, her dil için ayrı ayrı 2'şer manuel adım gerekir (örn. tr+en seçilirse toplam 4 kopyala-yapıştır).

**"Claude ile Düzelt" butonu da aynı köprüyü kullanır** — "Denetle" sekmesinde bir soruyu düzeltmek istediğinde artık API çağrısı yapılmıyor, sana onarım prompt'unu gösterip cevabını senden alıyor (tek adım, üretimdeki gibi 2 değil). Eski API tabanlı `/api/questions/{id}/fix` endpoint'i kodda hâlâ duruyor (ileride gerçek bir API key eklersen diye), ama arayüz artık onu çağırmıyor.

## Doğrulamanı önerdiğim tek şey

`CategoryId`'nin gerçekten Content'ten kopyalandığını teyit etmek için:

```sql
SELECT q."Id", q."CategoryId" AS question_category, c."CategoryId" AS content_category
FROM "Questions" q JOIN "Contents" c ON q."ContentId" = c."Id"
WHERE q."ContentId" = 57;
```

İkisi hep eşleşiyorsa varsayım doğru demektir.

## Kurulum

Önceki README'deki adımlar aynı (NuGet paketleri, appsettings.json, `dotnet run`) — tek fark artık **migration çalıştırmana gerek yok**, sadece `ConnectionStrings:Default` ve API anahtarlarını doldurup çalıştırman yeterli.

```json
{
  "ConnectionStrings": { "Default": "Host=...;Port=5432;Database=...;Username=...;Password=..." },
  "Anthropic": { "ApiKey": "sk-ant-...", "Model": "claude-sonnet-5" },
  "Gemini": { "ApiKey": "AIza...", "Model": "gemini-3.5-flash" }
}
```

**Bu akşam DB değiştirdiğinde:** sadece `ConnectionStrings:Default`'ı yeni DB'nin bağlantı bilgisiyle güncellemen yeterli — şema aynıysa kodda başka değişiklik gerekmez.

## Uç Noktalar

| Metot | Yol | Açıklama |
|---|---|---|
| GET | `/api/content/search?q=godfather` | İçerik adında arama (admin arayüzü autocomplete için, opsiyonel kullanım) |
| POST | `/api/generate` | `{ contentName, contentType, questionCount, languages, contentId?, replaceExisting? }` |
| POST | `/api/generate/bulk` | TÜM içerikler için toplu üretim başlatır (arka plan job), `jobId` döner. Body aşağıda. |
| GET | `/api/generate/bulk/{jobId}` | Toplu üretim işinin ilerlemesini döner (arayüz 3 sn'de bir sorgular) |
| POST | `/api/generate/bulk/{jobId}/cancel` | Çalışan toplu üretim işini durdurur (o ana kadar kaydedilenler kalır) |
| GET | `/api/content/{contentId}/questions?language=tr` | O içerik+dil için onaylı soruları istemci formatında döner |
| GET | `/api/questions/audit` | Var olan TÜM soruları ücretsiz heuristik kurallarla tarar, `IsFlagged`'i günceller |
| POST | `/api/questions/{id}/fix` | Sorunlu bir sorunun şıklarını Claude'a yeniden yazdırır (önizleme, kaydetmez) |
| POST | `/api/questions/{id}/apply-fix` | Önizlenen düzeltmeyi DB'ye yazar, `IsFlagged`'i false yapar |
| POST | `/api/questions/backfill-embeddings` | Embedding'i olmayan tüm soruları toplu olarak embed eder (yeni DB sonrası bir kez çalıştır) |
| GET | `/api/questions/duplicates?threshold=0.90` | Var olan soru havuzunda (aynı içerik+dil içinde) birbirine çok benzeyen ÇİFTLERİ listeler |
| DELETE | `/api/questions/{id}` | Tek bir soruyu kalıcı olarak siler (tekrar eden çiftlerden birini temizlemek için) |

### Var olan tekrar eden soruları temizleme adımları

Şu an soru havuzunda zaten benzer/tekrar soru sorunu olduğunu belirttin — bunu temizlemek için sırayla:

1. `ALTER TABLE "Questions" ADD COLUMN "EmbeddingJson" text NULL;` (bir kez, DB'de)
2. Arayüzde "Embedding'leri Doldur" (ya da `POST /api/questions/backfill-embeddings`) — tüm mevcut sorulara anlam vektörü hesaplar
3. Arayüzde "Tekrar Edenleri Bul" (ya da `GET /api/questions/duplicates`) — birbirine çok benzeyen çiftleri gösterir
4. Her çiftte hangisinin kalacağına karar verip diğerini "Sil" ile kaldır

Not: adım 3, O(n²) karşılaştırma yaptığı için çok büyük soru havuzlarında (binlerce soru/içerik) biraz sürebilir — ama tipik bir içeriğin soru sayısı (onlarca-yüzlerce) için sorun olmaz.

## Hâlâ açık olan sorular

- `Content.Type` ve `Content.Origin` (int) ne anlama geliyor? Örnek verilerle birlikte paylaşırsan bu servis o bilgiyi de kullanabilir (şu an dokunmuyor).
- Yeni DB'ye geçtikten sonra şema aynı mı kaldı, yoksa bu fırsatla değişti mi? Değiştiyse `Models/Entities.cs` + `Data/AppDbContext.cs`'i tekrar hizalarız.
