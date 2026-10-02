# Codex App Server Entegrasyonu — Faz 0 Spike Sonuçları

**Tarih:** 2026-10-02
**Test edilen sürüm:** `codex-cli 0.159.3` (Windows 11, x86_64)
**Hesap tipi:** ChatGPT, plan `plus`
**Probe script:** `scripts/probe-app-server.mjs` (salt-okunur; `consume` çağırmaz)

---

## 1. Özet

| Soru | Sonuç |
|---|---|
| `account/rateLimits/read` var mı? | ✅ Var, stable şemada, canlı test edildi |
| `account/rateLimitResetCredit/consume` var mı? | ✅ Var, **stable** şemada (experimental değil). Canlı **çağrılmadı** (kredi tüketir) |
| Sunucu tarafı idempotency var mı? | ✅ `idempotencyKey` zorunlu parametre; outcome'larda `alreadyRedeemed` var |
| Reset neyi sıfırlıyor? | ✅ Kredi başlığı: **"Full reset (Weekly + 5 hr)"**. İki pencere birlikte sıfırlanıyor |
| Krediler süresiz mi? | ❌ Hayır. `expiresAt` var (gözlenen: verilişten 30 gün sonra) |
| Push bildirim var mı? | ✅ `account/rateLimits/updated` (sparse) |
| Kullanılabilirlik için otorite alan | ✅ `ordinaryUsageAllowed` (yüzdelerden çıkarım yapılmamalı) |
| API key gerekiyor mu? | ❌ Hayır. `~/.codex` altındaki mevcut ChatGPT auth kullanılıyor |
| Okuma gecikmesi | ~0.6–0.8 sn / `rateLimits/read` (başlatma dahil ilk çağrı ~2.2 sn) |

**Sonuç:** Ürünün temel varsayımı doğrulandı. MVP-1 Codex App Server üzerinden uygulanabilir.

---

## 2. Bağlantı

### Transport

```text
codex app-server                 # stdio (varsayılan), JSON-RPC 2.0, satır başına bir mesaj
codex app-server --listen ws://127.0.0.1:PORT
codex app-server --listen unix://PATH
codex app-server proxy --sock PATH   # çalışan daemon'un control socket'ine stdio köprüsü
codex app-server daemon start|stop|version
```

`app-server` komut grubu CLI'da hâlâ **`[experimental]`** olarak işaretli. Ancak kullandığımız metodlar stable şemada.

### Handshake

```jsonc
→ {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
     "clientInfo":{"name":"resetme","title":null,"version":"x.y.z"},
     "capabilities":{"experimentalApi":false,"requestAttestation":false}}}
← {"id":1,"result":{"userAgent":"...","codexHome":"C:\\Users\\<user>\\.codex",
     "platformFamily":"windows","platformOs":"windows"}}
→ {"jsonrpc":"2.0","method":"initialized","params":{}}
```

Başlatmadan hemen sonra sunucu kendiliğinden `account/updated` (authMode, planType) ve `remoteControl/status/changed` bildirimlerini gönderir.

### Lifecycle kararı (MVP-1)

ResetMe **kendi `codex app-server` child process'ini stdio üzerinden başlatır**. Gerekçeler:

- Ek kurulum veya daemon gerektirmez, her üç platformda aynı şekilde çalışır.
- Auth, `codexHome` (`~/.codex`) üzerinden paylaşılır; ayrı login gerekmez.
- Sorun: Bu instance'ın `account/rateLimits/updated` bildirimleri yalnızca **kendi** turn'lerini yansıtabilir. Kullanıcının ayrı Codex CLI/Desktop sürecindeki kullanımı bu instance'a push edilmeyebilir. **Bu yüzden polling birincil sinyal, push ikincil sinyaldir.**

İleri sürüm: Çalışan bir daemon varsa (`codex app-server daemon version`) `proxy` üzerinden ona bağlanmak değerlendirilebilir.

---

## 3. `account/read`

Auth durumu kontrolü için kullanılır (`doctor`, `AUTH_REQUIRED` tespiti).

```jsonc
→ {"method":"account/read","params":{"refreshToken":false}}
← {"account":{"type":"chatgpt","email":"<pii>","planType":"plus"},
   "requiresOpenaiAuth":true,
   "workspaceRouting":{"chatgptAccountId":"<uuid>","backendOrigin":"https://chatgpt.com", ...}}
```

`email` loglanmamalıdır.

---

## 4. `account/rateLimits/read`

### Parametreler (opsiyonel)

| Alan | Kullanım |
|---|---|
| `excludeResetCreditDetails: true` | Background polling için. Yanıtta `availableCount` gelir, `credits` dizisi `null` olur |
| `supportsLunaReserve` | **Gönderilmemeli** (experiment exposure kaydı tetikler) |

### Gerçek yanıt (kırpılmış)

```jsonc
{
  "ordinaryUsageAllowed": true,
  "rateLimits": {
    "limitId": "codex",
    "primary":   { "usedPercent": 5,  "windowDurationMins": 300,   "resetsAt": 1790947849 },
    "secondary": { "usedPercent": 41, "windowDurationMins": 10080, "resetsAt": 1791389735 },
    "credits": { "hasCredits": false, "unlimited": false, "balance": "0" },
    "spendControlReached": false,
    "planType": "plus",
    "rateLimitReachedType": null
  },
  "rateLimitsByLimitId": { "codex": { /* aynı snapshot */ } },
  "rateLimitResetCredits": {
    "availableCount": 2,
    "credits": [
      {
        "id": "RateLimitResetCredit_<redacted>",
        "resetType": "codexRateLimits",
        "status": "available",
        "grantedAt": 1790108480,
        "expiresAt": 1792700480,
        "title": "Full reset (Weekly + 5 hr)",
        "description": "Thanks for using Codex! You've been granted one free rate limit reset."
      }
    ]
  },
  "accountId": "<redacted-uuid>",
  "rateLimitUpsell": null
}
```

### Domain eşlemesi

| Domain | Protokol alanı | Not |
|---|---|---|
| Kullanılabilir mi? | `ordinaryUsageAllowed` | **Otorite.** `null` = bilinmiyor, tahmin yapılmaz |
| 5 saatlik pencere | `windowDurationMins == 300` olan pencere | `primary`/`secondary` sırasına **güvenilmez**, süreye göre eşlenir |
| Haftalık pencere | `windowDurationMins == 10080` olan pencere | |
| Reset zamanı | `resetsAt` (Unix saniye, sunucu zamanı) | |
| Kullanım oranı | `usedPercent` (0–100) | |
| Engel tipi | `rateLimitReachedType` | `rate_limit_reached` veya workspace varyantları |
| Reset hakkı sayısı | `rateLimitResetCredits.availableCount` | `bigint` (JSON number) |
| Kredi detayları | `rateLimitResetCredits.credits[]` | `null` = detay yok; liste kırpılmış olabilir |
| Hesap | `accountId` | Idempotency ve state anahtarı |
| Bucket | `rateLimitsByLimitId["codex"]` | Çoklu bucket olabilir; MVP yalnızca `codex` bucket'ını izler |

`ordinaryUsageAllowed` alanının şema açıklaması: *"Null means unavailable; clients must not infer recovery from percentages or reset times."*

---

## 5. `account/rateLimitResetCredit/consume`

```ts
params: {
  idempotencyKey: string,   // "Identifies one logical reset attempt. A UUID is recommended;
                            //  reuse the same value when retrying that attempt."
  creditId?: string | null  // boşsa backend sıradaki krediyi seçer
}
result: { outcome: "reset" | "nothingToReset" | "noCredit" | "alreadyRedeemed" }
```

### Outcome anlamları ve ürün davranışı

| Outcome | Anlam (çıkarım) | Ürün davranışı |
|---|---|---|
| `reset` | Kredi kullanıldı, limitler sıfırlandı | `Verifying` → usage tekrar okunur |
| `nothingToReset` | Sıfırlanacak limit yok | Başarısız sayılmaz, `Healthy`. Kredinin tüketilmediği **varsayılıyor (doğrulanmadı)** |
| `noCredit` | Kullanılabilir kredi yok | `NO_RESET_CREDIT` |
| `alreadyRedeemed` | Bu idempotency key / kredi daha önce kullanılmış | Önceki deneme başarılı sayılır, usage okunarak doğrulanır, **yeni kredi istenmez** |

### Idempotency kuralı

- Her **mantıksal reset denemesi** için bir UUID üretilir ve **istek gönderilmeden önce** state dosyasına yazılır.
- Timeout veya bağlantı kopması durumunda **aynı key** ile tekrar denenebilir. Sunucu `alreadyRedeemed` dönerek ikinci tüketimi engeller.
- Yeni key yalnızca önceki deneme kesin olarak sonuçlandıktan (`reset`, `noCredit`, `nothingToReset` veya doğrulanmış başarısızlık) sonra üretilir.

### Kredi seçimi

`creditId` boş bırakılabilir. Ancak krediler süreli olduğu için ResetMe `status == "available"` olan krediler arasından **`expiresAt` değeri en yakın olanı** açıkça seçer. Süresi dolmak üzere olan kredi önce kullanılır.

---

## 6. Bildirimler

| Bildirim | Kullanım |
|---|---|
| `account/rateLimits/updated` | Sparse snapshot. Son `read` sonucuyla birleştirilir veya yeniden okuma tetiklenir. `null` alanlar önceki değeri **silmez** |
| `account/updated` | authMode/planType değişimi. Logout sonrası `AUTH_REQUIRED` tespiti |
| `error` (`ErrorNotification`) | Turn hatası. `codexErrorInfo` alanı `usageLimitExceeded` veya `rateLimitExceeded` ise fallback limit sinyali (yalnızca bu instance'ın turn'leri için) |

---

## 7. Hâlâ doğrulanmamış konular

Bu konular ancak gerçek bir limit olayında ve gerçek kredi tüketimiyle doğrulanabilir:

1. `consume` → `reset` sonrasında `usedPercent` ve `ordinaryUsageAllowed` değerlerinin ne kadar sürede güncellendiği (propagasyon gecikmesi).
2. `nothingToReset` dönüşünde kredinin tüketilmediği.
3. Limit dolu değilken `consume` çağrısının gerçekten `nothingToReset` dönüp dönmediği. Dönmüyorsa krediyi boşa harcar. **Bu nedenle ürün `consume` çağrısını yalnızca `ordinaryUsageAllowed == false` iken yapar.**
4. Ayrı bir Codex sürecindeki kullanımın bu instance'a `account/rateLimits/updated` olarak gelip gelmediği.
5. Polling sıklığının sunucu tarafında bir rate limit'e takılıp takılmadığı.

**Öneri:** 1–3 numaralı maddeler, ilk gerçek limit olayında `resetme reset --verbose` ile kontrollü olarak gözlenip bu dokümana eklenmeli.

---

## 8. Riskler

- `app-server` komut grubu `[experimental]` olduğu için protokol değişebilir. Adapter, `generate-json-schema` çıktısını CI'da snapshot olarak karşılaştırmalıdır. Böylece upstream değişiklik erken yakalanır.
- `doctor` komutu, `codex --version` sonucunu test edilmiş sürüm aralığıyla karşılaştırıp uyarı vermelidir.
