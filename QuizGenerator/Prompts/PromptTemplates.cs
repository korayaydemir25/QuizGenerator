namespace QuizGenerator.Prompts;

public static class PromptTemplates
{
    /// <summary>
    /// Soru üretim prompt'u. Hem Claude hem Gemini tarafında AYNI prompt kullanılır -
    /// böylece "çatışma" gerçekten aynı standartlara göre üretilmiş iki bağımsız set arasında olur.
    /// </summary>
    public static string BuildGenerationPrompt(
        string contentName, string contentType, string language, int count,
        IReadOnlyList<string>? existingQuestions = null, string? referenceNote = null, string? personaHint = null)
    {
        var template = """
        Aşağıdaki kurallara göre JSON formatında quiz soruları üret:

        İÇERİK: {{CONTENT_NAME}}
        SORU SAYISI: {{QUESTION_COUNT}}
        DİL: {{LANGUAGE}}
        TÜR: {{CONTENT_TYPE}}
        {{PERSONA_HINT_BLOCK}}{{EXISTING_QUESTIONS_BLOCK}}{{REFERENCE_NOTE_BLOCK}}
        0. ÇIKTI DİLİ (Kritik)
        - Bu talimatlar Türkçe yazılmış olsa da, ÜRETECEĞİN TÜM soru metinleri ve şıklar SADECE "{{LANGUAGE}}" dilinde olacak (tr = Türkçe, en = İngilizce). Diller karışmayacak: İngilizce istendiyse (en) tek bir Türkçe kelime bile geçmeyecek, Türkçe istendiyse (tr) tek bir İngilizce kelime bile geçmeyecek. (Karakter/mekan gibi özel isimler bunun istisnasıdır.)

        1. İÇERİK VE SORU ÇEŞİTLİLİĞİ (Kritik)
        - Konu Kotası: Soruları şu eksenlere DENGELİ dağıt, tek bir türe yığma:
          • ~%30 karakterler ve aralarındaki ilişkiler/bağlar
          • ~%25 olaylar ve olay örgüsü (kritik anlar, dönüm noktaları)
          • ~%15 ikonik mekanlar
          • ~%15 özel eşyalar, semboller, akılda kalan replikler/diyaloglar
          • ~%15 temalar ve karakter motivasyonları
          Sadece "kim / soyadı ne" sorma; yukarıdaki eksenlerin hepsinden soru olsun.
        - Kalıp Tekrarı YASAK: Aynı cümle kalıbını en fazla 2 kez kullan. Örn "...nin soyadı nedir?", "...nin annesinin adı nedir?", "...nin mesleği nedir?" gibi kalıpların HEPSİ arka arkaya tekrarlanamaz; test bütünüyle "X'in Y'si nedir?" formatına dönüşemez. Kalıpları çeşitlendir.
        - Anlamsal Tekrar YASAK (Kritik): İki soru farklı kelimelerle sorulup AYNI cevaba/bilgiye çıkıyorsa bu bir tekrardır ve yasaktır. Örn "Çocukların okuduğu okul nedir?" ile "Mira ve Yaman'ın okuduğu okul nedir?" aynı cevabı verir — sadece BİRİNİ sor. Her soru FARKLI bir bilgi test etmeli.
        - Easy = Kolay ama Ezber Değil: Kolay sorular bile düz isim ezberinden ibaret olmasın; kolay ama ilgi çekici olabilir (tanınmış bir sahne, ikonik bir mekan, akılda kalan bir replik).
        - Uzunluk Çeşitliliği: Soru uzunlukları karışık olsun. Bazı sorular çok kısa ve net (örn: "Corleone ailesinin avukatı kimdir?"), bazıları ise bir olayı tarif eden detaylı ve betimleyici sorular olsun. Zoraki kısaltma yapıp sorunun anlamını bozma.
        - Yasaklı Konular: Yayın yılı, vizyon tarihi, Oscar/Emmy ödülleri, oyuncuların/yönetmenlerin gerçek hayattaki detayları KESİNLİKLE sorulmayacak. Sorular tamamen kurgusal evrenin (lore) içinden olacak.
        - İsim Doğruluğu (EN KRİTİK): Karakter, mekan ve ilişki isimlerini ASLA uydurma veya birbirine karıştırma. Yukarıda bir KARAKTER/BİLGİ NOTU verildiyse: özel isimlerde ve ilişkilerde SADECE ve BİREBİR onu kaynak al; nota aykırı ya da notta olmayan hiçbir isim kullanma ve WEB'DE ARAMA YAPMA — her şeyi nottan al (böylece hızlı ve ekonomik çalış). KARAKTER/BİLGİ NOTU verilmediyse: yalnızca KESİN bildiğin isimleri kullan; bir isimden/ilişkiden en ufak tereddüdün varsa o soruyu emin olduğun BAŞKA bir bilgiye çevir (yalnızca bu durumda, web araması mümkünse ismi Wikipedia'dan doğrulayabilirsin). Aynı karakteri farklı sorularda farklı yazma — tutarlı ol. Tek bir yanlış isim, sorunun tamamını geçersiz kılar.

        2. ŞIK KURALLARI (Kritik — cevabı ele veren şıklar KESİNLİKLE yasak)
        - Kendi İçinde Denge: Bir soru içindeki 4 şıkkın uzunluğu ve dilbilgisel yapısı birbirine yakın olmalıdır. Doğru cevap uzun bir açıklama, yanlışlar tek kelime OLAMAZ. Tam tersi de olamaz.
        - Parantez İçi Açıklama YASAK: Hiçbir şıkta doğru cevabı ele verecek parantez içi açıklama, tanım ya da ipucu bulunamaz. YANLIŞ örnek: "Michael Corleone (ailenin yeni babası)". Şıklar sade ifade olarak kalmalı, ekstra açıklama eklenmemeli.
        - Üslup Paralelliği: Bir sorunun 4 şıkkı ya hepsi kısa isim/isim tamlaması olmalı ya da hepsi benzer uzunlukta ifade/cümlecik olmalı. Bir soruda 3 şık kısa isim, 1 şık uzun açıklamalı cümle OLAMAZ — bu durumda doğru cevap, soru okunmadan bile belli olur.
        - Genel Çeşitlilik: Testin genelinde bazı soruların şıkları 1-2 kelimelik isimlerden, bazılarının şıkları tam cümlelerden oluşsun. Bu çeşitliliği SORU BAZINDA değil TEST GENELİNDE uygula (her sorunun kendi 4 şıkkı birbirine yakın kalsın, ama soru 3'ün şıkları kısa, soru 7'nin şıkları uzun gibi testin geneli çeşitlensin).
        - Kaliteli Çeldiriciler: Yanlış şıklar tamamen uydurma kelimeler değil, o film/dizi evrenindeki diğer mantıklı karakterler, mekanlar veya olaylar arasından seçilmelidir.
        - Kendi Kendini Kontrol Et: JSON'u vermeden önce her soruyu tekrar oku — eğer 4 şıktan biri diğerlerinden bariz uzun/detaylı/açıklamalıysa ya da parantez içeriyorsa, o şıkkı diğer 3'üyle aynı yapıda olacak şekilde YENİDEN YAZ.

        3. TEKNİK VE JSON KURALLARI
        - Zorluk Dağılımı: %40 easy, %40 medium, %20 hard. Puanlar easy: 5, medium: 10, hard: 15 olmalıdır.
        - 0-Index ve Dengeli Dağılım: correctOption değeri 0, 1, 2, 3 olabilir. Doğru cevap pozisyonları TÜM soru sayısına yaklaşık eşit dağılmalı (her pozisyon ~toplam/4 kez); hiçbir pozisyon toplam soru sayısının %35'ini geçmemeli.
        - Sıralama: Doğru cevaplar her soru için rastgele pozisyonlara (0-3 arası) yerleştirilmiş olmalıdır.
        - Çıktı Öncesi Kendi Kendini Denetle: JSON'u vermeden önce şunları kontrol et ve gerekiyorsa düzelt: (a) aynı cevaba çıkan anlamsal tekrar soru var mı, (b) tek bir cümle kalıbına yığılma var mı, (c) konu kotası dengeli mi, (d) correctOption dağılımı dengeli mi.

        ÇIKTI FORMATI:
        (Sadece aşağıdaki JSON yapısını ver, fazladan markdown veya açıklama metni ekleme)
        {
          "contentName": "{{CONTENT_NAME}}",
          "language": "{{LANGUAGE}}",
          "forceUpdate": false,
          "questions": [
            {
              "text": "Soru metni?",
              "option1": "Şık A",
              "option2": "Şık B",
              "option3": "Şık C",
              "option4": "Şık D",
              "correctOption": 2,
              "difficulty": "easy",
              "points": 5,
              "movieOrShowName": "{{CONTENT_NAME}}",
              "contentType": "{{CONTENT_TYPE}}",
              "isAiGenerated": true
            }
          ]
        }

        Sadece geçerli JSON döndür. Markdown kod bloğu, yorum veya açıklama metni EKLEME.
        """;

        var personaBlock = "";
        if (!string.IsNullOrWhiteSpace(personaHint))
        {
            personaBlock =
                "\nBU ÜRETİCİNİN ODAĞI (çeşitlilik için — başka bir üretici farklı eksenlere bakıyor):\n"
                + personaHint.Trim() + "\n";
        }

        var existingBlock = "";
        if (existingQuestions is { Count: > 0 })
        {
            var list = string.Join("\n", existingQuestions.Select(q => $"- {q}"));
            existingBlock =
                "\nZATEN SORULMUŞ SORULAR (bunları AYNEN TEKRARLAMA, aynı anlama gelen farklı kelimelerle de tekrar sorma - farklı bir açı/detay/karakter/an seç):\n"
                + list + "\n";
        }

        var referenceBlock = "";
        if (!string.IsNullOrWhiteSpace(referenceNote))
        {
            referenceBlock =
                "\nKARAKTER/BİLGİ NOTU — KESİN DOĞRU KAYNAK (Kritik):\n"
                + "Aşağıda bu içeriğin DOĞRULANMIŞ karakter isimleri, ilişkileri ve temel bilgileri var. "
                + "Soru ve şıklarda geçen TÜM özel isimler ve ilişkiler yalnızca bu listeyle BİREBİR uyumlu olmalı. "
                + "Bu nota aykırı hiçbir şey yazma, listede olmayan bir karakter/ilişki UYDURMA:\n"
                + referenceNote.Trim() + "\n";
        }

        return template
            .Replace("{{CONTENT_NAME}}", contentName)
            .Replace("{{QUESTION_COUNT}}", count.ToString())
            .Replace("{{LANGUAGE}}", language)
            .Replace("{{CONTENT_TYPE}}", contentType)
            .Replace("{{PERSONA_HINT_BLOCK}}", personaBlock)
            .Replace("{{EXISTING_QUESTIONS_BLOCK}}", existingBlock)
            .Replace("{{REFERENCE_NOTE_BLOCK}}", referenceBlock);
    }

    /// <summary>
    /// Web'de (Gemini Google Search grounding ile) arayıp bir içeriğin DOĞRULANMIŞ karakter/ilişki
    /// notunu çıkarmak için kullanılır. Çıktı düz metindir (JSON değil): ilk satır "TİP: dizi|film",
    /// ardından maddeler halinde karakterler, ilişkiler, ikonik mekan/eşya/olaylar. Bu not sonra
    /// üretim prompt'una KESİN KAYNAK olarak enjekte edilir — LLM'in isim uydurmasını engeller.
    /// </summary>
    public static string BuildGroundingPrompt(string contentName, string contentTypeHint)
    {
        var template = """
        "{{CONTENT_NAME}}" adlı film/diziyi web'de araştır ({{TYPE_HINT}} olabilir) ve quiz sorusu üretmek için
        kullanılacak DOĞRULANMIŞ bir bilgi notu çıkar. Sadece güvenilir kaynaklardan (Wikipedia, resmi kaynaklar)
        teyit ettiğin bilgileri yaz — EMİN OLMADIĞIN hiçbir ismi/ilişkiyi yazma, boş bırakmak uydurmaktan iyidir.

        Notu ŞU biçimde, sade düz metin olarak ver (JSON/markdown tablo YOK):
        - İLK SATIR tam olarak şu olsun: "TİP: dizi"  ya da  "TİP: film"  (hangisiyse)
        - Sonra "KARAKTERLER:" başlığı altında, her ana ve önemli yan karakter için bir madde:
          "Ad Soyad — kısa rol/ilişki (ör. kimin annesi/oğlu/eşi/rakibi, mesleği)".
        - Sonra "MEKANLAR/OLAYLAR:" başlığı altında ikonik mekanlar, önemli olaylar, semboller/eşyalar (biliniyorsa).

        Kurallar:
        - Karakter isimlerini BİREBİR doğru yaz; aynı karakteri iki farklı yazma.
        - Oyuncuların gerçek adlarını DEĞİL, kurgudaki karakter adlarını yaz.
        - Yayın yılı, ödül gibi kurgu-dışı bilgileri EKLEME.
        - Hiç güvenilir bilgi bulamazsan sadece "BULUNAMADI" yaz.

        İÇERİK: {{CONTENT_NAME}}
        """;

        return template
            .Replace("{{CONTENT_NAME}}", contentName)
            .Replace("{{TYPE_HINT}}", contentTypeHint);
    }

    /// <summary>
    /// Çapraz inceleme prompt'u. Bir AI'nin ürettiği soruları DİĞER AI'ye denetletmek için kullanılır.
    /// Kritik nokta: bir model kendi ürettiğini asla incelemez, sadece karşı tarafınkini inceler.
    /// </summary>
    public static string BuildReviewPrompt(string contentName, string contentType, string language, string candidatesJson, string? referenceNote = null)
    {
        var template = """
        Sen bir quiz kalite kontrol hakemisin. Aşağıda BAŞKA bir yapay zeka tarafından üretilmiş {{CONTENT_NAME}} ({{CONTENT_TYPE}}) hakkında {{LANGUAGE}} dilinde quiz soruları var. Görevin bu soruları aşağıdaki kriterlere göre TEK TEK denetlemek. Sen üretici değil hakemsin — hatalıysa göz yumma, reddet.
        {{REFERENCE_NOTE_BLOCK}}
        DENETİM KRİTERLERİ:
        1. Doğruluk: Soru ve doğru cevap {{CONTENT_NAME}} evreninin (lore) gerçek olay örgüsüyle birebir uyumlu mu? Uydurma/yanlış bilgi var mı? Bir KARAKTER/BİLGİ NOTU verildiyse, sorudaki ve şıklardaki TÜM özel isim ve ilişkiler o nota BİREBİR uyuyor mu — uymuyorsa (yanlış, karışık ya da uydurma isim) mutlaka REDDET.
        2. Yasaklı Konu İhlali: Yayın yılı, ödül, oyuncu/yönetmen gerçek hayatı gibi yasaklı bir konu soruluyor mu?
        3. Cevap Sızıntısı (EN KRİTİK): 4 şıktan biri diğerlerinden bariz şekilde uzun mu, daha açıklayıcı mı, ya da parantez içinde ipucu/tanım içeriyor mu? Soru okunmadan doğru cevap tahmin edilebiliyorsa REDDET.
        4. Çeldirici Kalitesi: Yanlış şıklar mantıklı mı (aynı evrenden) yoksa alakasız/saçma mı?
        5. Format: JSON alanları eksiksiz mi, correctOption 0-3 aralığında mı, difficulty/points eşleşiyor mu (easy:5, medium:10, hard:15)?
        6. Anlamsal Tekrar: Bu soru, aynı setteki DAHA ÖNCEKİ (daha küçük index'li) bir soruyla farklı kelimelerle AYNI cevaba/bilgiye çıkıyor mu? Çıkıyorsa REDDET — ilk görülen kalsın, sonraki tekrar reddedilsin. (Örn: "Çocukların okuduğu okul nedir?" ile "Mira ve Yaman'ın okuduğu okul nedir?" aynı cevabı verir.)

        Her soru için şu formatta bir karar döndür:
        {
          "verdicts": [
            {
              "index": 0,
              "approved": true,
              "reason": "Kısa gerekçe",
              "violatedRules": []
            }
          ]
        }

        violatedRules alanına ihlal varsa şunlardan uygun olanları yaz: ["accuracy","forbidden_topic","answer_leak","weak_distractor","duplicate","format"]

        Denetlenecek sorular (index 0'dan başlar, sırasıyla):
        {{QUESTIONS_JSON}}

        Sadece yukarıdaki JSON yapısını döndür. Açıklama, markdown blok veya başka metin EKLEME.
        """;

        var referenceBlock = "";
        if (!string.IsNullOrWhiteSpace(referenceNote))
        {
            referenceBlock =
                "\nKARAKTER/BİLGİ NOTU — KESİN DOĞRU KAYNAK (isim/ilişki denetimini buna göre yap):\n"
                + referenceNote.Trim() + "\n";
        }

        return template
            .Replace("{{CONTENT_NAME}}", contentName)
            .Replace("{{CONTENT_TYPE}}", contentType)
            .Replace("{{LANGUAGE}}", language)
            .Replace("{{REFERENCE_NOTE_BLOCK}}", referenceBlock)
            .Replace("{{QUESTIONS_JSON}}", candidatesJson);
    }

    /// <summary>
    /// Var olan, "cevap sızıntısı" tespit edilmiş bir soruyu onarmak için kullanılır.
    /// Sadece şıkları yeniden yazar, soru metnini ve doğru cevabın anlamını korur.
    /// </summary>
    public static string BuildFixPrompt(string contentName, string questionJson)
    {
        var template = """
        Aşağıdaki quiz sorusunun ŞIKLARINDA "cevap sızıntısı" sorunu var: bir şık diğerlerinden bariz uzun/açıklamalı ya da parantez içinde ipucu içeriyor.

        Soru metnini ve doğru cevabın ANLAMINI DEĞİŞTİRMEDEN, sadece 4 şıkkı yeniden yaz:
        - Tüm şıklar birbirine yakın uzunlukta ve aynı dilbilgisel yapıda olsun (hepsi kısa isim YA DA hepsi benzer uzunlukta ifade).
        - Parantez içi açıklama KULLANMA.
        - Çeldiriciler {{CONTENT_NAME}} evreninden mantıklı seçenekler olsun.
        - correctOption değeri, doğru cevabın YENİ pozisyonunu göstermeli (pozisyonu değiştirmek istersen değiştirebilirsin, sabit tutmak zorunda değilsin).

        Mevcut soru:
        {{QUESTION_JSON}}

        Sadece şu JSON formatında yanıtla:
        {
          "option1": "...",
          "option2": "...",
          "option3": "...",
          "option4": "...",
          "correctOption": 0
        }

        Açıklama, markdown blok veya başka metin EKLEME.
        """;

                return template
            .Replace("{{CONTENT_NAME}}", contentName)
            .Replace("{{QUESTION_JSON}}", questionJson);
    }

    /// <summary>
    /// Birden fazla sorunlu soruyu TEK prompt'ta düzeltmek için. Her soru "id" ve
    /// "contentName" alanlarıyla birlikte gönderilir, Claude'un cevabındaki "id" ile
    /// eşleştirme yapılır.
    /// </summary>
    public static string BuildBatchFixPrompt(string questionsJson)
    {
        var template = """
        Aşağıdaki quiz sorularının HER BİRİNDE "cevap sızıntısı" sorunu var: bir şık diğerlerinden bariz uzun/açıklamalı ya da parantez içinde ipucu içeriyor.

        Her soru için, soru metnini ve doğru cevabın ANLAMINI DEĞİŞTİRMEDEN, sadece 4 şıkkı yeniden yaz:
        - Tüm şıklar birbirine yakın uzunlukta ve aynı dilbilgisel yapıda olsun (hepsi kısa isim YA DA hepsi benzer uzunlukta ifade)
        - Parantez içi açıklama KULLANMA
        - Çeldiriciler, sorunun ait olduğu içeriğin (contentName) evreninden mantıklı seçenekler olsun
        - correctOption değeri, doğru cevabın YENİ pozisyonunu göstermeli (pozisyonu değiştirmek istersen değiştirebilirsin, sabit tutmak zorunda değilsin)

        Düzeltilecek sorular:
        {{QUESTIONS_JSON}}

        Sadece aşağıdaki JSON dizisi formatında yanıtla - HER soru için bir obje, "id" alanını AYNEN koru (eşleştirme için kullanılacak):
        [
          {
            "id": 5255,
            "option1": "...",
            "option2": "...",
            "option3": "...",
            "option4": "...",
            "correctOption": 0
          }
        ]

        Açıklama, markdown blok veya başka metin EKLEME. Sorular kaç taneyse, dizide de o kadar obje olmalı.
        """;

        return template.Replace("{{QUESTIONS_JSON}}", questionsJson);
    }

    /// <summary>
    /// Onaylanmış final soru setini, anlamını ve şık sırasını KORUYARAK başka bir dile çevirir.
    /// Amaç: "aynı sorular hem TR hem EN" - correctOption/difficulty/points çeviride değişmez
    /// (bunları backend zaten kaynaktan kopyalar), sadece metin+şıklar çevrilir.
    /// </summary>
    public static string BuildTranslationPrompt(string contentName, string fromLang, string toLang, string questionsJson)
    {
        var template = """
        Sen profesyonel bir çevirmensin. Aşağıda {{CONTENT_NAME}} hakkında "{{FROM_LANG}}" dilinde quiz soruları var. Bunları "{{TO_LANG}}" diline çevir (tr = Türkçe, en = İngilizce).

        ÇEVİRİ KURALLARI:
        - Anlamı BİREBİR koru. Soruyu yeniden yazma, yorumlama, bilgi ekleme/çıkarma yapma - sadece çevir.
        - Şıkların SIRASINI ASLA DEĞİŞTİRME: option1 → option1, option2 → option2, ... aynı sırada kalsın (doğru cevabın pozisyonu korunmalı).
        - Özel isimleri (karakter adları, mekan adları, dizi/film adı) OLDUĞU GİBİ bırak, çevirme.
        - Doğal, akıcı ve dilbilgisel olarak doğru "{{TO_LANG}}" kullan. Tek bir kaynak-dil kelimesi sızmasın (özel isimler hariç).
        - Soru sayısı ve sırası kaynakla BİREBİR aynı olmalı.

        Çevrilecek sorular (index 0'dan başlar, sırasıyla):
        {{QUESTIONS_JSON}}

        Sadece şu JSON yapısını döndür (aynı sırada, aynı sayıda obje):
        {
          "questions": [
            {
              "text": "...",
              "option1": "...",
              "option2": "...",
              "option3": "...",
              "option4": "..."
            }
          ]
        }

        Açıklama, markdown kod bloğu veya başka metin EKLEME.
        """;

        return template
            .Replace("{{CONTENT_NAME}}", contentName)
            .Replace("{{FROM_LANG}}", fromLang)
            .Replace("{{TO_LANG}}", toLang)
            .Replace("{{QUESTIONS_JSON}}", questionsJson);
    }
}
