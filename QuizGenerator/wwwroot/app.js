// --- Sekme geçişleri ---
document.querySelectorAll(".tab").forEach((tab) => {
  tab.addEventListener("click", () => {
    document.querySelectorAll(".tab").forEach((t) => { t.classList.remove("active"); t.setAttribute("aria-selected", "false"); });
    document.querySelectorAll(".panel").forEach((p) => p.classList.remove("active"));
    tab.classList.add("active");
    tab.setAttribute("aria-selected", "true");
    document.getElementById(`panel-${tab.dataset.tab}`).classList.add("active");
  });
});

// ============================================================
// ÜRETİM SEKMESİ
// ============================================================
const generateForm = document.getElementById("generate-form");
const generateBtn = document.getElementById("generate-btn");
const statusBox = document.getElementById("generate-status");
const resultsBox = document.getElementById("generate-results");

generateForm.addEventListener("submit", async (e) => {
  e.preventDefault();

  const contentName = document.getElementById("contentName").value.trim();
  const contentType = document.getElementById("contentType").value;
  const questionCount = parseInt(document.getElementById("questionCount").value, 10);
  const languages = Array.from(document.querySelectorAll('input[name="lang"]:checked')).map((el) => el.value);
  const replaceExisting = document.getElementById("replaceExisting").checked;
  const manualMode = document.getElementById("manualMode").checked;
  const referenceNote = document.getElementById("referenceNote").value.trim();

  if (!contentName) return;
  if (languages.length === 0) {
    showStatus("En az bir dil seçmelisiniz.", "error");
    return;
  }

  resultsBox.innerHTML = "";

  if (manualMode) {
    generateBtn.disabled = true;
    await runManualFlow(contentName, contentType, questionCount, languages, referenceNote);
    generateBtn.disabled = false;
    return;
  }

  generateBtn.disabled = true;
  showStatus(`"${contentName}" için Claude ve Gemini paralel üretime başladı, çapraz inceleme sürüyor… (birkaç dakika sürebilir)`, "loading");

  try {
    const resp = await fetch("/api/generate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ contentName, contentType, questionCount, languages, replaceExisting, referenceNote }),
    });

    if (!resp.ok) {
      const err = await safeJson(resp);
      throw new Error(err?.error || `Sunucu hatası (${resp.status})`);
    }

    const data = await resp.json();
    hideStatus();
    renderGenerateResults(data);
  } catch (err) {
    showStatus(`Hata: ${err.message}`, "error");
  } finally {
    generateBtn.disabled = false;
  }
});

function renderGenerateResults(data) {
  resultsBox.innerHTML = "";

  const header = document.createElement("div");
  header.className = "card";
  header.style.marginBottom = "20px";
  header.innerHTML = `
    <h2 style="margin:0">${escapeHtml(data.contentName)}</h2>
    ${data.contentId || data.genre ? `<p class="muted">${data.contentId ? "İçerik #" + data.contentId : ""}${data.contentId && data.genre ? " · " : ""}${data.genre ? escapeHtml(data.genre) : ""}</p>` : ""}
  `;
  resultsBox.appendChild(header);

  data.results.forEach((r) => {
    const block = document.createElement("div");
    block.className = "lang-block";

    const ok = r.delivered >= r.requested;
    block.innerHTML = `
      <div class="lang-header">
        <h3>${langLabel(r.language)}</h3>
        <span class="pill ${ok ? "ok" : "warn"}">${r.delivered} / ${r.requested} soru</span>
      </div>
      ${r.warnings.length ? `<ul class="warnings">${r.warnings.map((w) => `<li>${escapeHtml(w)}</li>`).join("")}</ul>` : ""}
      <table class="q-table">
        <thead>
          <tr><th style="width:32%">Soru</th><th style="width:32%">Şıklar</th><th style="width:14%">Zorluk</th><th style="width:22%">Kaynak</th></tr>
        </thead>
        <tbody>${r.questions.map(renderQuestionRow).join("")}</tbody>
      </table>
    `;
    resultsBox.appendChild(block);
  });
}

function renderQuestionRow(q) {
  const options = [q.option1, q.option2, q.option3, q.option4];
  const optsHtml = options
    .map((o, i) => `<li class="${i === q.correctOption ? "correct" : ""}">${escapeHtml(o)}</li>`)
    .join("");

  return `
    <tr>
      <td>${escapeHtml(q.text)}</td>
      <td><ul class="opt-list">${optsHtml}</ul></td>
      <td>
        <div class="difficulty">${q.difficulty}</div>
        <div class="points">${q.points} puan</div>
      </td>
      <td>
        <div class="badge-row">
          <span><span class="label">Üreten: </span><span class="badge ${q.generatedBy}">${q.generatedBy || "?"}</span></span>
          <span><span class="label">İnceleyen: </span><span class="badge ${q.reviewedBy}">${q.reviewedBy || "?"}</span></span>
        </div>
      </td>
    </tr>
  `;
}

function langLabel(code) {
  return code === "tr" ? "Türkçe" : code === "en" ? "English" : code;
}

function showStatus(msg, type) {
  statusBox.textContent = msg;
  statusBox.className = `status ${type}`;
  statusBox.classList.remove("hidden");
}
function hideStatus() {
  statusBox.classList.add("hidden");
}

// ============================================================
// MANUEL CLAUDE KÖPRÜSÜ (API'ye ödeme yapmadan)
// ============================================================
const manualFlowBox = document.getElementById("manual-flow");

async function runManualFlow(contentName, contentType, questionCount, languages, referenceNote) {
  // Üretim + çapraz denetim BİRİNCİL dilde (languages[0]) tek turda yapılır.
  // Birden fazla dil seçiliyse, diğer diller final set üretildikten sonra
  // backend'de otomatik çevrilip AYNI sorular olarak kaydedilir (ikinci bir manuel tur YOK).
  const primary = languages[0];
  const others = languages.slice(1);
  const langNote = others.length
    ? ` — birincil dil ${langLabel(primary)}; ${others.map(langLabel).join(", ")} otomatik çevrilecek`
    : ` (${langLabel(primary)})`;

  showStatus(`"${contentName}"${langNote}. Gemini otomatik üretiyor…`, "loading");

  let step;
  try {
    const resp = await fetch("/api/generate/manual/start", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ contentName, contentType, questionCount, language: primary, languages, referenceNote }),
    });
    if (!resp.ok) {
      const err = await safeJson(resp);
      throw new Error(err?.error || err?.detail || err?.title || `Sunucu hatası (${resp.status})`);
    }
    step = await resp.json();
  } catch (err) {
    showStatus(`Hata: ${err.message}`, "error");
    return;
  }

  hideStatus();

  step = await showManualStep(step, `/api/generate/manual/${step.sessionId}/submit-generation`, `${langLabel(primary)} · 1/2`, false);
  if (!step) return;

  const sessionId = step.sessionId;
  const outcome = await showManualStep(step, `/api/generate/manual/${sessionId}/submit-review`, `${langLabel(primary)} · 2/2`, true);
  if (!outcome) return;

  // İnceleme sonrası benzerlik "şüpheli bandı" varsa, kaydetmeden önce kullanıcıya sor.
  let finalResult;
  if (outcome.needsReview) {
    finalResult = await showSimilarityReview(sessionId, outcome.suspicious || []);
    if (!finalResult) return;
  } else {
    finalResult = outcome.result;
  }

  manualFlowBox.classList.add("hidden");
  manualFlowBox.innerHTML = "";
  renderGenerateResults({ contentName, contentId: null, genre: null, results: [finalResult] });
}

// Benzerlik "şüpheli bandı" onay ekranı: her şüpheli aday, benzediği mevcut soruyla gösterilir.
// Kullanıcı eklemek istediklerini işaretler; işaretlenmeyenler atılır. Temiz sorular zaten kaydedilir.
function showSimilarityReview(sessionId, suspicious) {
  return new Promise((resolve) => {
    manualFlowBox.classList.remove("hidden");

    const rows = suspicious.map((s) => {
      const opts = [s.option1, s.option2, s.option3, s.option4]
        .map((o, i) => `<li style="${i === s.correctOption ? "color:#3fb950;font-weight:600" : ""}">${escapeHtml(o)}</li>`)
        .join("");
      return `
        <div class="card" style="margin-bottom:12px;border-left:3px solid var(--amber)">
          <label class="checkbox" style="text-transform:none;font-weight:600;color:var(--text)">
            <input type="checkbox" class="sim-keep" data-index="${s.index}" /> Bu soruyu yine de ekle
          </label>
          <p style="margin:10px 0 4px"><strong>${escapeHtml(s.text)}</strong> <span class="muted" style="font-size:12px">(${escapeHtml(s.generatedBy)})</span></p>
          <ul style="margin:0 0 10px 18px;font-size:13px">${opts}</ul>
          <div class="muted" style="font-size:12px;background:var(--surface-2);padding:8px 10px;border-radius:6px">
            ⚠️ Mevcut bir soruya <strong>%${Math.round(s.similarity * 100)}</strong> benziyor:<br>"${escapeHtml(s.similarToText)}"
          </div>
        </div>`;
    }).join("");

    manualFlowBox.innerHTML = `
      <div class="card">
        <h2>Benzerlik onayı — ${suspicious.length} şüpheli soru</h2>
        <p class="muted">Bu sorular mevcut sorulara yakın bulundu, o yüzden otomatik eklenmedi. Eklemek istediklerini işaretle; işaretlemediklerin atılır. (Şüpheli olmayan temiz sorular zaten kaydedilecek.)</p>
        <div style="margin:16px 0">${rows}</div>
        <div style="display:flex;gap:10px;align-items:center">
          <button class="btn-primary" id="sim-confirm-btn" type="button">Onayla ve Kaydet</button>
          <button class="btn-secondary" id="sim-none-btn" type="button">Hiçbirini Ekleme</button>
        </div>
        <div id="sim-error" style="color:var(--red);font-size:13px;margin-top:10px"></div>
      </div>`;

    async function submitDecisions(keepIndices) {
      const btn = document.getElementById("sim-confirm-btn");
      const noneBtn = document.getElementById("sim-none-btn");
      const errBox = document.getElementById("sim-error");
      btn.disabled = true;
      noneBtn.disabled = true;
      btn.textContent = "Kaydediliyor…";
      errBox.textContent = "";
      try {
        const resp = await fetch(`/api/generate/manual/${sessionId}/submit-similar-decisions`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ keepIndices }),
        });
        if (!resp.ok) {
          const err = await safeJson(resp);
          throw new Error(err?.error || err?.detail || err?.title || `Sunucu hatası (${resp.status})`);
        }
        resolve(await resp.json());
      } catch (err) {
        errBox.textContent = `Hata: ${err.message}`;
        btn.disabled = false;
        noneBtn.disabled = false;
        btn.textContent = "Onayla ve Kaydet";
      }
    }

    document.getElementById("sim-confirm-btn").addEventListener("click", () => {
      const keep = Array.from(document.querySelectorAll(".sim-keep:checked")).map((el) => parseInt(el.dataset.index, 10));
      submitDecisions(keep);
    });
    document.getElementById("sim-none-btn").addEventListener("click", () => submitDecisions([]));
  });
}

function showManualStep(stepData, submitUrl, stepLabel, isFinal) {
  return new Promise((resolve) => {
    manualFlowBox.classList.remove("hidden");
    manualFlowBox.innerHTML = `
      <div class="card">
        <h2>Manuel adım ${stepLabel}${isFinal ? " (son)" : ""}</h2>
        <p class="muted">${escapeHtml(stepData.instructions)}</p>

        <label style="display:block;font-size:11px;text-transform:uppercase;font-weight:600;color:var(--text-muted);margin-bottom:6px">1. Bu prompt'u kopyala</label>
        <textarea id="manual-prompt" readonly style="width:100%;height:180px;background:var(--surface-2);color:var(--text);border:1px solid var(--border);border-radius:8px;padding:10px;font-family:var(--font-mono);font-size:12px;margin-bottom:10px">${escapeHtml(stepData.promptToCopy)}</textarea>
        <button class="btn-secondary" id="manual-copy-btn" type="button">📋 Panoya Kopyala</button>

        <label style="display:block;margin-top:20px;font-size:11px;text-transform:uppercase;font-weight:600;color:var(--text-muted);margin-bottom:6px">2. claude.ai'den aldığın cevabı buraya yapıştır</label>
        <textarea id="manual-response" style="width:100%;height:180px;background:var(--surface-2);color:var(--text);border:1px solid var(--border);border-radius:8px;padding:10px;font-family:var(--font-mono);font-size:12px;margin-bottom:10px" placeholder="Claude'un JSON cevabını buraya yapıştır… (sadece JSON, markdown bloğu olsa da olur, biz temizleriz)"></textarea>

        <div style="display:flex;gap:10px;align-items:center">
          <button class="btn-primary" id="manual-continue-btn" type="button">Devam Et</button>
          <button class="btn-secondary" id="manual-cancel-btn" type="button">İptal</button>
        </div>
        <div id="manual-error" style="color:var(--red);font-size:13px;margin-top:10px"></div>
      </div>
    `;

    document.getElementById("manual-copy-btn").addEventListener("click", () => {
      navigator.clipboard.writeText(stepData.promptToCopy);
      const btn = document.getElementById("manual-copy-btn");
      const original = btn.textContent;
      btn.textContent = "✓ Kopyalandı";
      setTimeout(() => { btn.textContent = original; }, 1500);
    });

    document.getElementById("manual-cancel-btn").addEventListener("click", () => {
      manualFlowBox.classList.add("hidden");
      manualFlowBox.innerHTML = "";
      resolve(null);
    });

    document.getElementById("manual-continue-btn").addEventListener("click", async () => {
      const btn = document.getElementById("manual-continue-btn");
      const errBox = document.getElementById("manual-error");
      const responseText = document.getElementById("manual-response").value.trim();

      if (!responseText) {
        errBox.textContent = "Claude'un cevabını yapıştırmadan devam edemezsin.";
        return;
      }

      btn.disabled = true;
      btn.textContent = "İşleniyor…";
      errBox.textContent = "";

      try {
        const resp = await fetch(submitUrl, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ claudeResponseText: responseText }),
        });
        if (!resp.ok) {
          const err = await safeJson(resp);
          throw new Error(err?.error || err?.detail || err?.title || `Sunucu hatası (${resp.status})`);
        }
        const data = await resp.json();
        resolve(data);
      } catch (err) {
        errBox.textContent = `Hata: ${err.message}`;
        btn.disabled = false;
        btn.textContent = "Devam Et";
      }
    });
  });
}

// ============================================================
// EMBEDDING BACKFILL (yeni DB'ye geçtikten sonra bir kez çalıştır)
// ============================================================
const backfillBtn = document.getElementById("backfill-btn");
const backfillSummary = document.getElementById("backfill-summary");

backfillBtn.addEventListener("click", async () => {
  backfillBtn.disabled = true;
  backfillBtn.textContent = "Dolduruluyor… (biraz sürebilir)";
  backfillSummary.classList.remove("hidden");
  backfillSummary.innerHTML = `<p class="muted">Embedding'i olmayan sorular taranıyor…</p>`;

  try {
    const resp = await fetch("/api/questions/backfill-embeddings", { method: "POST" });
    if (!resp.ok) throw new Error(`Sunucu hatası (${resp.status})`);
    const data = await resp.json();
    const dailyNote = data.dailyQuotaHit
      ? `<p style="color:var(--amber);font-family:var(--font-mono);font-size:12px;margin-top:6px">⚠️ Günlük ücretsiz kota (günde ~1000 istek) doldu. Kalanı yarın (Pasifik saatiyle gece yarısı sıfırlanır) veya faturalandırma açarsan hemen tamamlayabilirsin. "Embedding'leri Doldur" kaldığı yerden devam eder, tekrar basman yeterli.</p>`
      : "";
    backfillSummary.innerHTML = `<p class="muted"><strong style="color:var(--green)">${data.done}</strong> / ${data.total} soru için embedding hesaplandı${data.failed ? `, <strong style="color:var(--red)">${data.failed}</strong> tanesi başarısız oldu` : ""}.</p>` +
      dailyNote +
      (data.firstError && !data.dailyQuotaHit ? `<p style="color:var(--red);font-family:var(--font-mono);font-size:12px">İlk hata: ${escapeHtml(data.firstError)}</p>` : "");
  } catch (err) {
    backfillSummary.innerHTML = `<p style="color:var(--red)">Hata: ${err.message}</p>`;
  } finally {
    backfillBtn.disabled = false;
    backfillBtn.textContent = "Embedding'leri Doldur";
  }
});

// ============================================================
// TEKRAR EDEN / ÇOK BENZER SORULARI BUL
// ============================================================
const duplicatesBtn = document.getElementById("duplicates-btn");
const duplicatesSummary = document.getElementById("duplicates-summary");
const duplicatesResults = document.getElementById("duplicates-results");

duplicatesBtn.addEventListener("click", async () => {
  duplicatesBtn.disabled = true;
  duplicatesBtn.textContent = "Taranıyor…";
  duplicatesResults.innerHTML = "";
  duplicatesSummary.classList.add("hidden");

  try {
    const resp = await fetch("/api/questions/duplicates");
    if (!resp.ok) throw new Error(`Sunucu hatası (${resp.status})`);
    const data = await resp.json();

    duplicatesSummary.classList.remove("hidden");
    duplicatesSummary.innerHTML = `<p class="muted">${data.totalWithEmbedding} soru karşılaştırıldı (eşik: %${Math.round(data.thresholdUsed * 100)}), <strong style="color:var(--red)">${data.pairsFound}</strong> benzer çift bulundu.</p>`;

    if (data.pairs.length === 0) {
      duplicatesResults.innerHTML = `<div class="empty-state">Tekrar eden çift bulunamadı.</div>`;
    } else {
      data.pairs.forEach((p) => duplicatesResults.appendChild(renderDuplicateCard(p)));
    }
  } catch (err) {
    duplicatesSummary.classList.remove("hidden");
    duplicatesSummary.innerHTML = `<p style="color:var(--red)">Hata: ${err.message}</p>`;
  } finally {
    duplicatesBtn.disabled = false;
    duplicatesBtn.textContent = "Tekrar Edenleri Bul";
  }
});

function renderDuplicateCard(pair) {
  const card = document.createElement("div");
  card.className = "audit-card";

  const side = (q) => `
    <div>
      <div class="q-text" style="margin-bottom:6px">${escapeHtml(q.text)}</div>
      <ul class="opt-list">
        <li>${escapeHtml(q.option1)}</li>
        <li>${escapeHtml(q.option2)}</li>
        <li>${escapeHtml(q.option3)}</li>
        <li>${escapeHtml(q.option4)}</li>
      </ul>
      <button class="btn-secondary btn-delete" data-id="${q.id}" style="margin-top:8px">Bunu Sil (#${q.id})</button>
    </div>
  `;

  card.innerHTML = `
    <div class="meta-row">
      <span class="content-tag">${escapeHtml(pair.contentName)} · ${langLabel(pair.language)}</span>
      <span class="reason-tag">%${Math.round(pair.similarity * 100)} benzer</span>
    </div>
    <div class="diff-cols">
      ${side(pair.question1)}
      ${side(pair.question2)}
    </div>
  `;

  card.querySelectorAll(".btn-delete").forEach((btn) => {
    btn.addEventListener("click", async () => {
      if (!confirm(`#${btn.dataset.id} numaralı soruyu kalıcı olarak silmek istediğine emin misin?`)) return;
      btn.disabled = true;
      btn.textContent = "Siliniyor…";
      try {
        const resp = await fetch(`/api/questions/${btn.dataset.id}`, { method: "DELETE" });
        if (!resp.ok) throw new Error(`Sunucu hatası (${resp.status})`);
        card.remove();
      } catch (err) {
        alert(`Silinemedi: ${err.message}`);
        btn.disabled = false;
        btn.textContent = `Bunu Sil (#${btn.dataset.id})`;
      }
    });
  });

  return card;
}

// ============================================================
// DENETİM SEKMESİ
// ============================================================
const auditBtn = document.getElementById("audit-btn");
const auditSummary = document.getElementById("audit-summary");
const auditResults = document.getElementById("audit-results");
const auditToolbar = document.getElementById("audit-toolbar");
const auditSelectAll = document.getElementById("audit-select-all");
const auditSelectedCount = document.getElementById("audit-selected-count");
const auditBatchFixBtn = document.getElementById("audit-batch-fix-btn");

const BATCH_FIX_SIZE = 40; // tek prompt'ta en fazla kaç soru gönderilecek
const selectedFixIds = new Set();

auditBtn.addEventListener("click", async () => {
  auditBtn.disabled = true;
  auditBtn.textContent = "Taranıyor…";
  auditResults.innerHTML = "";
  auditSummary.classList.add("hidden");
  auditToolbar.classList.add("hidden");
  selectedFixIds.clear();

  try {
    const resp = await fetch("/api/questions/audit");
    if (!resp.ok) throw new Error(`Sunucu hatası (${resp.status})`);
    const data = await resp.json();

    auditSummary.classList.remove("hidden");
    auditSummary.innerHTML = `<p class="muted">${data.totalChecked} soru tarandı, <strong style="color:var(--red)">${data.flaggedCount}</strong> tanesinde cevap sızıntısı kalıbı bulundu.</p>`;

    if (data.flagged.length === 0) {
      auditResults.innerHTML = `<div class="empty-state">Sorun bulunamadı — tüm şıklar dengeli görünüyor.</div>`;
    } else {
      data.flagged.forEach((q) => auditResults.appendChild(renderAuditCard(q)));
      auditToolbar.classList.remove("hidden");
      auditSelectAll.checked = false;
      updateFixSelectionUi();
    }
  } catch (err) {
    auditSummary.classList.remove("hidden");
    auditSummary.innerHTML = `<p style="color:var(--red)">Hata: ${err.message}</p>`;
  } finally {
    auditBtn.disabled = false;
    auditBtn.textContent = "Taramayı Başlat";
  }
});

auditSelectAll.addEventListener("change", () => {
  const checkboxes = auditResults.querySelectorAll(".audit-select");
  checkboxes.forEach((cb) => {
    cb.checked = auditSelectAll.checked;
    const id = parseInt(cb.dataset.id, 10);
    if (auditSelectAll.checked) selectedFixIds.add(id);
    else selectedFixIds.delete(id);
  });
  updateFixSelectionUi();
});

function updateFixSelectionUi() {
  auditSelectedCount.textContent = `${selectedFixIds.size} seçili`;
  auditBatchFixBtn.disabled = selectedFixIds.size === 0;
}

auditBatchFixBtn.addEventListener("click", async () => {
  const ids = Array.from(selectedFixIds);
  auditBatchFixBtn.disabled = true;
  await runBatchFixFlow(ids);
  auditBatchFixBtn.disabled = selectedFixIds.size === 0;
});

async function runBatchFixFlow(ids) {
  const chunks = [];
  for (let i = 0; i < ids.length; i += BATCH_FIX_SIZE) chunks.push(ids.slice(i, i + BATCH_FIX_SIZE));

  let totalApplied = 0;

  for (let c = 0; c < chunks.length; c++) {
    const chunk = chunks[c];

    let stepData;
    try {
      const resp = await fetch("/api/questions/batch-manual-fix-prompt", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ids: chunk }),
      });
      if (!resp.ok) { const err = await safeJson(resp); throw new Error(err?.error || err?.detail || `Sunucu hatası (${resp.status})`); }
      stepData = await resp.json();
    } catch (err) {
      alert(`Grup ${c + 1}/${chunks.length} için prompt hazırlanamadı: ${err.message}`);
      break;
    }

    const fixes = await showManualStep(
      stepData,
      "/api/questions/parse-batch-manual-fix",
      `${c + 1}/${chunks.length} — ${chunk.length} soru`,
      c === chunks.length - 1
    );
    if (!fixes) break;

        try {
      const applyResp = await fetch("/api/questions/apply-batch-fix", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ fixes, expectedIds: chunk }),
      });
      if (!applyResp.ok) {
        const err = await safeJson(applyResp);
        let msg = err?.error || err?.detail || `Sunucu hatası (${applyResp.status})`;
        if (err?.missingIds?.length) msg += ` — eksik id'ler: [${err.missingIds.join(", ")}]`;
        if (err?.unexpectedIds?.length) msg += ` — fazladan/yanlış id'ler: [${err.unexpectedIds.join(", ")}]`;
        throw new Error(msg);
      }
      const applyData = await applyResp.json();
      totalApplied += applyData.applied;

      chunk.forEach((id) => {
        selectedFixIds.delete(id);
        const card = auditResults.querySelector(`.audit-card[data-id="${id}"]`);
        if (card) card.remove();
      });
    } catch (err) {
      alert(`Grup ${c + 1}/${chunks.length} kaydedilemedi: ${err.message}\n\nBu grup atlandı, kartlar listede kaldı (yanlışlıkla "düzeldi" görünmedi). Doğru cevabı bulup bu grubu tekrar deneyebilirsin.`);
      break;
    }
  }

  updateFixSelectionUi();
  auditSummary.innerHTML += `<p class="muted" style="margin-top:6px"><strong style="color:var(--green)">${totalApplied}</strong> soru toplu düzeltmeyle güncellendi.</p>`;
}

function renderAuditCard(q) {
  const card = document.createElement("div");
  card.className = "audit-card";
  card.dataset.id = q.id;

  const options = [q.option1, q.option2, q.option3, q.option4];

  card.innerHTML = `
    <div class="meta-row">
      <label class="checkbox" style="text-transform:none;margin-right:4px">
        <input type="checkbox" class="audit-select" data-id="${q.id}" />
      </label>
      <span class="content-tag">${escapeHtml(q.movieOrShowName || "?")} · ${langLabel(q.language)}</span>
      <span class="reason-tag">${escapeHtml(q.reason)}</span>
    </div>
    <div class="q-text">${escapeHtml(q.text)}</div>
    <ul class="opt-list">
      ${options.map((o, i) => `<li class="${i === q.correctOption ? "correct" : ""}">${escapeHtml(o)}</li>`).join("")}
    </ul>
    <div class="fix-actions">
      <button class="btn-secondary btn-fix">Claude ile Düzelt</button>
      <button class="btn-secondary btn-dismiss">Yoksay</button>
    </div>
    <div class="fix-preview"></div>
  `;

  card.querySelector(".audit-select").addEventListener("change", (e) => {
    if (e.target.checked) selectedFixIds.add(q.id);
    else selectedFixIds.delete(q.id);
    updateFixSelectionUi();
  });

  card.querySelector(".btn-dismiss").addEventListener("click", () => {
    selectedFixIds.delete(q.id);
    updateFixSelectionUi();
    card.remove();
  });

  card.querySelector(".btn-fix").addEventListener("click", async (e) => {
    const btn = e.target;
    btn.disabled = true;
    btn.textContent = "Prompt hazırlanıyor…";

    try {
      const promptResp = await fetch(`/api/questions/${q.id}/manual-fix-prompt`);
      if (!promptResp.ok) throw new Error(`Sunucu hatası (${promptResp.status})`);
      const stepData = await promptResp.json();

      const fix = await showManualStep(stepData, `/api/questions/${q.id}/parse-manual-fix`, "1/1", true);
      if (fix) renderFixPreview(card, q, fix);
    } catch (err) {
      alert(`Prompt hazırlanamadı: ${err.message}`);
    } finally {
      btn.disabled = false;
      btn.textContent = "Claude ile Düzelt";
    }
  });

  return card;
}

function renderFixPreview(card, original, fix) {
  const preview = card.querySelector(".fix-preview");
  preview.classList.add("visible");

  const origOpts = [original.option1, original.option2, original.option3, original.option4];
  const fixOpts = [fix.option1, fix.option2, fix.option3, fix.option4];

  preview.innerHTML = `
    <div class="diff-cols">
      <div>
        <h4>Mevcut</h4>
        <ul class="opt-list">${origOpts.map((o, i) => `<li class="${i === original.correctOption ? "correct" : ""}">${escapeHtml(o)}</li>`).join("")}</ul>
      </div>
      <div>
        <h4>Önerilen</h4>
        <ul class="opt-list">${fixOpts.map((o, i) => `<li class="${i === fix.correctOption ? "correct" : ""}">${escapeHtml(o)}</li>`).join("")}</ul>
      </div>
    </div>
    <div class="fix-actions">
      <button class="btn-primary btn-apply">Kaydet</button>
    </div>
  `;

  preview.querySelector(".btn-apply").addEventListener("click", async (e) => {
    const btn = e.target;
    btn.disabled = true;
    btn.textContent = "Kaydediliyor…";

    try {
      const resp = await fetch(`/api/questions/${original.id}/apply-fix`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(fix),
      });
      if (!resp.ok) throw new Error(`Sunucu hatası (${resp.status})`);
      card.remove();
    } catch (err) {
      alert(`Kaydedilemedi: ${err.message}`);
      btn.disabled = false;
      btn.textContent = "Kaydet";
    }
  });
}

// ============================================================
// TOPLU ÜRETİM SEKMESİ (tüm içerikler, 2 Gemini personası + web grounding)
// ============================================================
const bulkStartBtn = document.getElementById("bulk-start-btn");
const bulkCancelBtn = document.getElementById("bulk-cancel-btn");
const bulkProgress = document.getElementById("bulk-progress");
let bulkPollTimer = null;
let bulkJobId = null;

bulkStartBtn.addEventListener("click", async () => {
  const languages = Array.from(document.querySelectorAll('input[name="bulk-lang"]:checked')).map((el) => el.value);
  if (languages.length === 0) { alert("En az bir dil seçmelisiniz."); return; }

  const limitVal = document.getElementById("bulk-limit").value.trim();
  const startAfterVal = document.getElementById("bulk-start-after").value.trim();

  const body = {
    languages,
    questionsPerLanguage: parseInt(document.getElementById("bulk-count").value, 10),
    defaultContentType: document.getElementById("bulk-default-type").value,
    includeInactive: document.getElementById("bulk-inactive").checked,
    useGrounding: document.getElementById("bulk-grounding").checked,
    limit: limitVal ? parseInt(limitVal, 10) : null,
    startAfterContentId: startAfterVal ? parseInt(startAfterVal, 10) : null,
    delayMsBetweenContents: parseInt(document.getElementById("bulk-delay").value || "0", 10),
    saveAsDraft: document.getElementById("bulk-draft").checked,
  };

  const totalContentsHint = limitVal ? ` (en fazla ${limitVal} içerik)` : " (TÜM içerikler)";
  const draftHint = body.saveAsDraft
    ? "Sorular ONAYSIZ TASLAK olarak eklenecek; 'Son Eklenenler' sekmesinden onaylaman gerekir."
    : "⚠️ Sorular DOĞRUDAN CANLI (onaylı) eklenecek.";
  if (!confirm(`Toplu üretim başlatılsın mı?${totalContentsHint}\n\nDiller: ${languages.map(langLabel).join(", ")}\nİçerik+dil başına ${body.questionsPerLanguage} yeni soru.\n${draftHint}\n\nBu çok sayıda Gemini isteği harcar; ücretsiz kota dolarsa iş durur ve kaldığı yerden devam edebilirsin.`)) return;

  bulkStartBtn.disabled = true;
  bulkStartBtn.querySelector(".btn-label").textContent = "Başlatılıyor…";

  try {
    const resp = await fetch("/api/generate/bulk", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!resp.ok) { const err = await safeJson(resp); throw new Error(err?.error || `Sunucu hatası (${resp.status})`); }
    const status = await resp.json();
    bulkJobId = status.jobId;
    bulkCancelBtn.classList.remove("hidden");
    renderBulkProgress(status);
    startBulkPolling();
  } catch (err) {
    alert(`Toplu üretim başlatılamadı: ${err.message}`);
    bulkStartBtn.disabled = false;
    bulkStartBtn.querySelector(".btn-label").textContent = "Toplu Üretimi Başlat";
  }
});

bulkCancelBtn.addEventListener("click", async () => {
  if (!bulkJobId) return;
  if (!confirm("Toplu üretim durdurulsun mu? (o ana kadar kaydedilenler kalır)")) return;
  bulkCancelBtn.disabled = true;
  try { await fetch(`/api/generate/bulk/${bulkJobId}/cancel`, { method: "POST" }); } catch {}
});

function startBulkPolling() {
  clearInterval(bulkPollTimer);
  bulkPollTimer = setInterval(async () => {
    if (!bulkJobId) return;
    try {
      const resp = await fetch(`/api/generate/bulk/${bulkJobId}`);
      if (!resp.ok) return;
      const status = await resp.json();
      renderBulkProgress(status);
      if (status.status !== "running") stopBulkPolling();
    } catch {}
  }, 3000);
}

function stopBulkPolling() {
  clearInterval(bulkPollTimer);
  bulkPollTimer = null;
  bulkStartBtn.disabled = false;
  bulkStartBtn.querySelector(".btn-label").textContent = "Toplu Üretimi Başlat";
  bulkCancelBtn.classList.add("hidden");
  bulkCancelBtn.disabled = false;
}

function bulkStatusPill(status) {
  const map = {
    running: ["loading", "Çalışıyor"],
    completed: ["ok", "Tamamlandı"],
    stopped_quota: ["warn", "Kota doldu — durdu"],
    canceled: ["warn", "İptal edildi"],
    failed: ["warn", "Hata"],
  };
  const [cls, label] = map[status] || ["warn", status];
  return `<span class="pill ${cls}">${label}</span>`;
}

function renderBulkProgress(s) {
  bulkProgress.classList.remove("hidden");
  const pct = s.totalContents > 0 ? Math.round((s.processedContents / s.totalContents) * 100) : 0;

  const recentRows = (s.recent || []).map((r) => {
    const langs = (r.perLanguage || []).map((l) => `${langLabel(l.language)}: ${l.saved}/${l.requested}`).join(" · ");
    const warns = (r.warnings || []).length
      ? `<ul class="warnings" style="margin-top:6px">${r.warnings.map((w) => `<li>${escapeHtml(w)}</li>`).join("")}</ul>` : "";
    return `
      <div class="lang-block" style="margin-bottom:10px">
        <div class="lang-header">
          <h3 style="font-size:15px">${escapeHtml(r.contentName)} <span class="muted" style="font-size:12px;font-weight:400">#${r.contentId} · ${escapeHtml(r.contentType)}${r.grounded ? " · 🌐 grounded" : ""}</span></h3>
          <span class="pill ${r.savedTotal > 0 ? "ok" : "warn"}">${r.savedTotal} soru</span>
        </div>
        <div class="muted" style="font-size:13px">${escapeHtml(langs)}</div>
        ${warns}
      </div>`;
  }).join("");

  const msg = s.message ? `<p style="color:var(--amber);font-size:13px;margin-top:8px">${escapeHtml(s.message)}</p>` : "";
  const resumeHint = (s.status === "stopped_quota" && s.lastProcessedContentId != null)
    ? `<p class="muted" style="font-size:12px">Devam etmek için "Şu ID'den sonra başla" alanına <strong>${s.lastProcessedContentId}</strong> yazıp tekrar başlat.</p>` : "";

  bulkProgress.innerHTML = `
    <div class="card" style="margin-bottom:16px">
      <div style="display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px">
        <h2 style="margin:0">İlerleme ${bulkStatusPill(s.status)}</h2>
        <div class="muted" style="font-size:13px">
          <strong style="color:var(--green)">${s.totalQuestionsSaved}</strong> soru kaydedildi ·
          ${s.groundedCount} içerik grounded
        </div>
      </div>
      <div style="height:12px;background:var(--surface-2);border-radius:8px;overflow:hidden;margin:14px 0 6px">
        <div style="height:100%;width:${pct}%;background:var(--green);transition:width .4s"></div>
      </div>
      <div class="muted" style="font-size:13px">
        ${s.processedContents} / ${s.totalContents} içerik (%${pct})
        ${s.currentContentName ? ` · şu an: <strong>${escapeHtml(s.currentContentName)}</strong>` : ""}
      </div>
      ${msg}
      ${resumeHint}
    </div>
    <div class="card">
      <h2 style="margin:0 0 12px">Son işlenen içerikler</h2>
      ${recentRows || '<p class="muted">Henüz içerik işlenmedi…</p>'}
    </div>`;
}

// ============================================================
// SON EKLENENLER / ONAY BEKLEYENLER (toplu üretim çıktısını gözden geçir)
// ============================================================
const recentRefreshBtn = document.getElementById("recent-refresh-btn");
const recentOnlyDrafts = document.getElementById("recent-only-drafts");
const recentLimit = document.getElementById("recent-limit");
const recentFilter = document.getElementById("recent-filter");
const recentSummary = document.getElementById("recent-summary");
const recentResults = document.getElementById("recent-results");
const recentToolbar = document.getElementById("recent-toolbar");
const recentSelectAll = document.getElementById("recent-select-all");
const recentSelectedCount = document.getElementById("recent-selected-count");
const recentApproveBtn = document.getElementById("recent-approve-btn");
const recentDeleteBtn = document.getElementById("recent-delete-btn");

const recentSelected = new Set();
let recentItems = [];

// Sekmeye ilk kez geçildiğinde otomatik yükle.
document.querySelector('.tab[data-tab="recent"]').addEventListener("click", () => {
  if (recentItems.length === 0) loadRecent();
});

recentRefreshBtn.addEventListener("click", loadRecent);
recentOnlyDrafts.addEventListener("change", loadRecent);
recentLimit.addEventListener("change", loadRecent);
recentFilter.addEventListener("input", renderRecent);

async function loadRecent() {
  recentRefreshBtn.disabled = true;
  recentRefreshBtn.textContent = "Yükleniyor…";
  recentSelected.clear();
  try {
    const params = new URLSearchParams({
      limit: recentLimit.value,
      onlyDrafts: recentOnlyDrafts.checked ? "true" : "false",
    });
    const resp = await fetch(`/api/questions/recent?${params}`);
    if (!resp.ok) throw new Error(`Sunucu hatası (${resp.status})`);
    const data = await resp.json();
    recentItems = data.items || [];

    recentSummary.classList.remove("hidden");
    recentSummary.innerHTML = `<p class="muted"><strong style="color:var(--amber)">${data.pendingDrafts}</strong> soru onay bekliyor (tüm veritabanında). Listede ${recentItems.length} soru gösteriliyor.</p>`;
    renderRecent();
  } catch (err) {
    recentSummary.classList.remove("hidden");
    recentSummary.innerHTML = `<p style="color:var(--red)">Hata: ${err.message}</p>`;
  } finally {
    recentRefreshBtn.disabled = false;
    recentRefreshBtn.textContent = "Yenile";
  }
}

function renderRecent() {
  const filter = recentFilter.value.trim().toLocaleLowerCase("tr");
  const shown = filter
    ? recentItems.filter((q) => (q.movieOrShowName || "").toLocaleLowerCase("tr").includes(filter))
    : recentItems;

  recentResults.innerHTML = "";
  if (recentItems.length === 0) {
    recentResults.innerHTML = `<div class="empty-state">Gösterilecek soru yok. (Toplu üretim çalıştıysa "Yenile"ye bas.)</div>`;
    recentToolbar.classList.add("hidden");
    return;
  }
  recentToolbar.classList.remove("hidden");

  if (shown.length === 0) {
    recentResults.innerHTML = `<div class="empty-state">"${escapeHtml(recentFilter.value)}" filtresine uyan soru yok.</div>`;
  } else {
    shown.forEach((q) => recentResults.appendChild(renderRecentCard(q)));
  }
  recentSelectAll.checked = false;
  updateRecentSelectionUi();
}

function renderRecentCard(q) {
  const card = document.createElement("div");
  card.className = "audit-card";
  card.dataset.id = q.id;

  const options = [q.option1, q.option2, q.option3, q.option4];
  const statusPill = q.isApproved
    ? `<span class="pill ok">canlı</span>`
    : `<span class="pill warn">onay bekliyor</span>`;
  const created = q.createdAt ? new Date(q.createdAt).toLocaleString("tr-TR") : "";

  card.innerHTML = `
    <div class="meta-row">
      <label class="checkbox" style="text-transform:none;margin-right:4px">
        <input type="checkbox" class="recent-select" data-id="${q.id}" ${recentSelected.has(q.id) ? "checked" : ""} />
      </label>
      <span class="content-tag">${escapeHtml(q.movieOrShowName || "?")} · ${langLabel(q.language)}</span>
      ${statusPill}
      <span class="reason-tag">${escapeHtml(q.aiModel || "?")} · ${q.difficulty} · ${created}</span>
    </div>
    <div class="q-text">${escapeHtml(q.text)}</div>
    <ul class="opt-list">
      ${options.map((o, i) => `<li class="${i === q.correctOption ? "correct" : ""}">${escapeHtml(o)}</li>`).join("")}
    </ul>
    <div class="fix-actions">
      <button class="btn-secondary btn-recent-fix">Düzelt</button>
      <button class="btn-secondary btn-recent-delete">Sil</button>
    </div>
    <div class="fix-preview"></div>
  `;

  card.querySelector(".recent-select").addEventListener("change", (e) => {
    if (e.target.checked) recentSelected.add(q.id);
    else recentSelected.delete(q.id);
    updateRecentSelectionUi();
  });

  card.querySelector(".btn-recent-delete").addEventListener("click", async () => {
    if (!confirm(`#${q.id} numaralı soruyu kalıcı olarak silmek istediğine emin misin?`)) return;
    try {
      const resp = await fetch(`/api/questions/${q.id}`, { method: "DELETE" });
      if (!resp.ok) throw new Error(`Sunucu hatası (${resp.status})`);
      recentSelected.delete(q.id);
      recentItems = recentItems.filter((x) => x.id !== q.id);
      card.remove();
      updateRecentSelectionUi();
    } catch (err) {
      alert(`Silinemedi: ${err.message}`);
    }
  });

  // Düzeltme mevcut manuel-fix köprüsünü kullanır (Claude/claude.ai ile).
  card.querySelector(".btn-recent-fix").addEventListener("click", async (e) => {
    const btn = e.target;
    btn.disabled = true;
    btn.textContent = "Prompt hazırlanıyor…";
    try {
      const promptResp = await fetch(`/api/questions/${q.id}/manual-fix-prompt`);
      if (!promptResp.ok) throw new Error(`Sunucu hatası (${promptResp.status})`);
      const stepData = await promptResp.json();
      const fix = await showManualStep(stepData, `/api/questions/${q.id}/parse-manual-fix`, "1/1", true);
      if (fix) renderFixPreview(card, q, fix);
    } catch (err) {
      alert(`Prompt hazırlanamadı: ${err.message}`);
    } finally {
      btn.disabled = false;
      btn.textContent = "Düzelt";
    }
  });

  return card;
}

recentSelectAll.addEventListener("change", () => {
  recentResults.querySelectorAll(".recent-select").forEach((cb) => {
    cb.checked = recentSelectAll.checked;
    const id = parseInt(cb.dataset.id, 10);
    if (recentSelectAll.checked) recentSelected.add(id);
    else recentSelected.delete(id);
  });
  updateRecentSelectionUi();
});

function updateRecentSelectionUi() {
  recentSelectedCount.textContent = `${recentSelected.size} seçili`;
  recentApproveBtn.disabled = recentSelected.size === 0;
  recentDeleteBtn.disabled = recentSelected.size === 0;
}

recentApproveBtn.addEventListener("click", async () => {
  const ids = Array.from(recentSelected);
  if (ids.length === 0) return;
  if (!confirm(`${ids.length} soru ONAYLANSIN mı? (IsApproved=true → canlıya alınır)`)) return;
  recentApproveBtn.disabled = true;
  recentApproveBtn.textContent = "Onaylanıyor…";
  try {
    const resp = await fetch("/api/questions/approve", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids }),
    });
    if (!resp.ok) { const err = await safeJson(resp); throw new Error(err?.error || `Sunucu hatası (${resp.status})`); }
    const data = await resp.json();
    alert(`${data.approved} soru onaylandı.`);
    loadRecent();
  } catch (err) {
    alert(`Onaylanamadı: ${err.message}`);
  } finally {
    recentApproveBtn.textContent = "Seçilenleri Onayla";
    updateRecentSelectionUi();
  }
});

recentDeleteBtn.addEventListener("click", async () => {
  const ids = Array.from(recentSelected);
  if (ids.length === 0) return;
  if (!confirm(`${ids.length} soru KALICI olarak silinsin mi? Bu geri alınamaz.`)) return;
  recentDeleteBtn.disabled = true;
  recentDeleteBtn.textContent = "Siliniyor…";
  try {
    const resp = await fetch("/api/questions/delete-batch", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids }),
    });
    if (!resp.ok) { const err = await safeJson(resp); throw new Error(err?.error || `Sunucu hatası (${resp.status})`); }
    const data = await resp.json();
    alert(`${data.deleted} soru silindi.`);
    loadRecent();
  } catch (err) {
    alert(`Silinemedi: ${err.message}`);
  } finally {
    recentDeleteBtn.textContent = "Seçilenleri Sil";
    updateRecentSelectionUi();
  }
});

// --- Yardımcılar ---
async function safeJson(resp) {
  try { return await resp.json(); } catch { return null; }
}
function escapeHtml(str) {
  const div = document.createElement("div");
  div.textContent = str ?? "";
  return div.innerHTML;
}