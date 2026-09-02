using QuizGenerator.Models;

namespace QuizGenerator.Services;

/// <summary>
/// Otomatik (API tabanlı) ve manuel (kopyala-yapıştır köprüsü) akışların İKİSİNİN DE
/// kullandığı ortak mantık: AI hakem onayı + heuristik denetim + embedding benzerlik
/// filtresi + zorluk/doğru-şık dengeli final seçim.
/// </summary>
public static class ReviewPipeline
{
    /// <param name="reviewThreshold">Şüpheli bandın alt sınırı. Benzerlik bu değer ile dupThreshold
    /// arasındaysa aday elenmez; suspicious verildiyse kullanıcı onayına ayrılır, verilmediyse
    /// (otomatik akış) uyarıyla tutulur.</param>
    /// <param name="suspicious">Manuel akışta doldurulur; null ise şüpheli bant yok sayılır (otomatik akış).</param>
    public static async Task FilterApprovedAsync(
        List<GeneratedQuestion> batch, ReviewResponse review, string generatedBy, string reviewedBy,
        List<ApprovedCandidate> approved, List<(string Text, float[] Embedding)> seenEmbeddings,
        EmbeddingService embeddings, double dupThreshold, double reviewThreshold,
        List<SuspiciousCandidate>? suspicious, List<string> warnings, CancellationToken ct)
    {
        var verdictByIndex = review.Verdicts
            .GroupBy(v => v.Index)
            .ToDictionary(g => g.Key, g => g.First());

        for (int i = 0; i < batch.Count; i++)
        {
            var q = batch[i];

            if (!verdictByIndex.TryGetValue(i, out var verdict) || !verdict.Approved)
                continue; // AI hakem reddetti ya da hiç değerlendirmedi

            var audit = QuestionAuditService.Check(q.Option1, q.Option2, q.Option3, q.Option4);
            if (audit.Flagged)
            {
                warnings.Add($"[{generatedBy}] soru AI tarafından onaylandı ama heuristik denetimde elendi ({audit.Reason}): \"{Truncate(q.Text)}\"");
                continue;
            }

            float[] embedding;
            try
            {
                embedding = await embeddings.EmbedAsync(q.Text, ct);
            }
            catch (DailyQuotaExhaustedException) { throw; }
            catch
            {
                warnings.Add($"[{generatedBy}] soru için embedding alınamadı, benzerlik kontrolü yapılamadığından elendi: \"{Truncate(q.Text)}\"");
                continue;
            }

            var (mostSimilarText, similarity) = FindMostSimilar(embedding, seenEmbeddings);
            if (similarity >= dupThreshold)
            {
                warnings.Add($"[{generatedBy}] benzer/tekrar soru elendi (benzerlik %{similarity * 100:F0}): \"{Truncate(q.Text)}\" ~ \"{Truncate(mostSimilarText)}\"");
                continue;
            }

            var candidate = new ApprovedCandidate { Q = q, GeneratedBy = generatedBy, ReviewedBy = reviewedBy, Embedding = embedding };

            // Şüpheli bant: elenecek kadar yakın değil ama otomatik onaylanacak kadar da uzak değil.
            if (similarity >= reviewThreshold)
            {
                if (suspicious is not null)
                {
                    // Manuel akış: kullanıcı onayına ayır. Aynı adayın kopyalarını tekrar şüpheliye
                    // düşürmemek için seen'e ekliyoruz (onaylanmasa da bu turda referans olur).
                    suspicious.Add(new SuspiciousCandidate { Candidate = candidate, SimilarToText = mostSimilarText, Similarity = similarity });
                    seenEmbeddings.Add((q.Text, embedding));
                    continue;
                }

                // Otomatik akış (kullanıcı yok): tut ama uyar.
                warnings.Add($"[{generatedBy}] mevcut bir soruya benziyor olabilir (%{similarity * 100:F0}), eşiğin altında olduğu için tutuldu: \"{Truncate(q.Text)}\" ~ \"{Truncate(mostSimilarText)}\"");
            }

            approved.Add(candidate);
            seenEmbeddings.Add((q.Text, embedding));
        }
    }

    private static (string Text, double Similarity) FindMostSimilar(float[] embedding, List<(string Text, float[] Embedding)> pool)
    {
        string bestText = "";
        double best = 0;
        foreach (var (text, emb) in pool)
        {
            var sim = EmbeddingService.CosineSimilarity(embedding, emb);
            if (sim > best) { best = sim; bestText = text; }
        }
        return (bestText, best);
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    public static List<ApprovedCandidate> SelectBalanced(
        List<ApprovedCandidate> pool, int requestedCount, List<string> warnings)
    {
        int easyTarget = (int)Math.Round(requestedCount * 0.4);
        int mediumTarget = (int)Math.Round(requestedCount * 0.4);
        int hardTarget = requestedCount - easyTarget - mediumTarget;

        var byDifficulty = new Dictionary<string, List<ApprovedCandidate>>
        {
            ["easy"] = pool.Where(p => p.Q.Difficulty == "easy").ToList(),
            ["medium"] = pool.Where(p => p.Q.Difficulty == "medium").ToList(),
            ["hard"] = pool.Where(p => p.Q.Difficulty == "hard").ToList(),
        };

        var result = new List<ApprovedCandidate>();
        result.AddRange(TakeBalancedByCorrectOption(byDifficulty["easy"], easyTarget));
        result.AddRange(TakeBalancedByCorrectOption(byDifficulty["medium"], mediumTarget));
        result.AddRange(TakeBalancedByCorrectOption(byDifficulty["hard"], hardTarget));

        if (result.Count < requestedCount)
        {
            warnings.Add($"Zorluk dağılımı hedeflenen oranda tutturulamadı, kalan {requestedCount - result.Count} soru havuzdan tamamlandı.");
            var remaining = pool.Except(result).ToList();
            result.AddRange(remaining.Take(requestedCount - result.Count));
        }

        return result.Take(requestedCount).ToList();
    }

    private static List<ApprovedCandidate> TakeBalancedByCorrectOption(List<ApprovedCandidate> items, int target)
    {
        if (target <= 0) return new();

        var buckets = new List<ApprovedCandidate>[4];
        for (int i = 0; i < 4; i++) buckets[i] = new();
        foreach (var item in items)
            buckets[Math.Clamp(item.Q.CorrectOption, 0, 3)].Add(item);

        var result = new List<ApprovedCandidate>();
        var perBucket = target / 4;
        var extra = target % 4;

        for (int i = 0; i < 4; i++)
        {
            var take = perBucket + (i < extra ? 1 : 0);
            result.AddRange(buckets[i].Take(take));
        }

        if (result.Count < target)
        {
            var used = new HashSet<ApprovedCandidate>(result);
            var leftovers = items.Where(i => !used.Contains(i)).Take(target - result.Count);
            result.AddRange(leftovers);
        }

        return result;
    }
}
