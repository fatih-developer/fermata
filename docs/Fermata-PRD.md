# Fermata — Product Requirements Document (PRD)

**Durum:** Draft v1.1 (Faz 0 spike sonrası revize)  
**Tarih:** 2026-10-02  
**İlgili:** [`CODEX_INTEGRATION.md`](CODEX_INTEGRATION.md) — App Server protokol doğrulaması  
**Çalışma adı:** Fermata  
**Hedef platformlar:** Windows, macOS, Linux  
**Ürün tipi:** Cross-platform local companion / background service + CLI + optional desktop UI

---

## 1. Ürün Özeti

Fermata, Codex kullanımı sırasında 5 saatlik veya haftalık kullanım limiti dolduğunda bunu tespit eden, kullanılabilir reset hakkını kontrol eden ve kullanıcıya reset kullanma seçeneği sunan işletim sistemi bağımsız bir yardımcı uygulamadır.

Ürün Codex'in kendisini değiştirmez. Codex ile yerel olarak çalışan ayrı bir companion uygulama olarak tasarlanır.

Temel amaç:

> Kullanıcı Codex ile uzun süreli geliştirme yaparken limite takıldığında durumu manuel olarak kontrol etmek zorunda kalmadan reset hakkını güvenli şekilde kullanabilsin ve çalışmaya devam edebilsin.

**Kapsam notu:** Fermata limiti açar, ancak limite takılıp durmuş bir Codex oturumunu/turn'ünü **otomatik olarak devam ettirmez**. Reset sonrası kullanıcı Codex'te işlemi kendisi yeniden başlatır (retry / "continue"). Durmuş oturumun otomatik devamı MVP kapsamı dışındadır.

---

## 2. Problem

Codex yoğun kullanıldığında iki farklı kullanım penceresi çalışmayı durdurabilir:

- 5 saatlik kullanım limiti
- Haftalık kullanım limiti

Kullanıcının reset hakkı olsa bile mevcut süreçte:

1. Limitin dolduğunu fark etmesi,
2. Kullanım durumunu kontrol etmesi,
3. Reset hakkı olup olmadığını görmesi,
4. Reset işlemini manuel başlatması,
5. Daha sonra geliştirmeye devam etmesi

gerekir.

Uzun süren agentic coding oturumlarında bu süreç gereksiz kesinti oluşturur.

---

## 3. Hedef

Codex limit yönetimini otomatik izleyen, ancak reset hakkının tüketimini kullanıcı kontrolünde tutan cross-platform bir sistem geliştirmek.

### Ana hedefler

- 5 saatlik ve haftalık Codex limitlerini izlemek.
- Limitlerden herhangi biri çalışmayı engellediğinde bunu tespit etmek.
- Kullanılabilir reset hakkı sayısını kontrol etmek.
- Kullanıcıya reset kullanma seçeneği sunmak.
- Onay verilirse reset işlemini gerçekleştirmek.
- Reset sonrası limitleri tekrar doğrulamak.
- Windows, macOS ve Linux üzerinde aynı temel kod tabanını kullanmak.
- CLI üzerinden GUI olmadan da çalışabilmek.

---

## 4. Hedef Dışı Konular

İlk sürümde aşağıdakiler kapsam dışıdır:

- Codex abonelik planı satın alma/değiştirme
- Ücretli instant reset satın alma
- OpenAI hesap yönetimi
- Birden fazla OpenAI hesabını aynı anda yönetme
- Cloud tabanlı merkezi kullanıcı hesabı
- Telemetri veya merkezi analytics sistemi
- Codex kullanım limitlerini aşmaya yönelik herhangi bir bypass
- OpenAI tarafından tanımlanmamış reset üretme veya çoğaltma
- Browser automation / scraping tabanlı reset
- Codex binary'sinin patch edilmesi

---

## 5. Temel Ürün İlkeleri

### 5.1 Kullanıcı kontrolü

Reset hakkı değerli ve sınırlı bir kaynak olabilir.

Varsayılan davranış:

**Confirm Mode**

Limit dolduğunda kullanıcıdan açık onay alınır.

### 5.2 Local-first

Uygulama mümkün olduğunca tamamen kullanıcının cihazında çalışır.

Kullanım verileri üçüncü taraf bir sunucuya gönderilmez.

### 5.3 Cross-platform

Core business logic işletim sisteminden bağımsız olmalıdır.

Platform farklılıkları adapter katmanında çözülmelidir.

### 5.4 Güvenli otomasyon

Otomatik reset modu yalnızca kullanıcı açıkça etkinleştirirse çalışmalıdır.

---

## 6. Kullanıcı Profili

### Birincil kullanıcı

Codex CLI, Codex Desktop veya Codex tabanlı geliştirme araçlarını yoğun kullanan geliştirici.

### Tipik kullanım

- Uzun süren coding session
- Agentic coding
- Büyük refactor
- Test / implement / verify döngüleri
- Birden fazla saat süren proje geliştirme
- Hafta boyunca yüksek Codex kullanımı

---

## 7. Temel Kullanıcı Senaryosu

```text
Kullanıcı Codex ile çalışıyor
        ↓
Fermata limitleri izliyor
        ↓
5 saatlik veya haftalık limit doluyor
        ↓
Fermata kullanılabilir reset hakkını kontrol ediyor
        ↓
Reset hakkı varsa bildirim gösteriliyor
        ↓
"Reset kullanılsın mı?"
        ↓
[Kullan] [Bekle]
        ↓
Kullanıcı "Kullan" seçiyor
        ↓
Reset isteği gönderiliyor
        ↓
Limitler tekrar okunuyor
        ↓
Başarılı ise kullanıcı bilgilendiriliyor
        ↓
Codex kullanımına devam ediliyor
```

---

## 8. Limit Tetikleme Kuralı

Faz 0 spike'ı iki önemli gerçeği doğruladı (bkz. `CODEX_INTEGRATION.md`):

- Backend, kullanılabilirlik için otorite alan olarak `ordinaryUsageAllowed` döner. İstemci yüzdelerden kullanılabilirlik çıkarımı yapmamalıdır.
- Reset kredisi **"Full reset (Weekly + 5 hr)"** tipindedir. Tek kredi **iki pencereyi birlikte** sıfırlar.

### 8.1 Engel tespiti

```text
blocked =
    ordinaryUsageAllowed == false
    OR (ordinaryUsageAllowed == null AND (fiveHour.usedPercent >= 100 OR weekly.usedPercent >= 100))
```

`ordinaryUsageAllowed == null` durumunda yüzdeler yalnızca **bilgi amaçlı** gösterilir. Automatic mode bu durumda reset **yapmaz**, yalnızca Confirm teklifi üretilebilir.

### 8.2 Reset teklif kuralı

```text
offerReset =
    blocked
    AND availableCount > 0
    AND authOk
    AND NOT (timeToNaturalUnblock < reset.min_time_to_natural_reset)
```

`timeToNaturalUnblock`: Engelleyen **tüm** pencerelerin `resetsAt` değerlerinin en büyüğü ile şimdiki zaman arasındaki fark. Örneğin yalnızca 5 saatlik pencere doluysa ve 8 dakika içinde kendiliğinden açılacaksa reset teklif edilmez. Kullanıcıya "8 dk sonra açılacak" bilgisi gösterilir. Manual `fermata reset` komutu bu eşiği bir uyarıyla geçebilir.

İki limitin aynı anda dolması gerekmez. Kredi iki pencereyi birlikte sıfırladığı için hangi pencerenin engellediğinden bağımsız olarak tek kredi yeterlidir.

Örnek:

```text
5 saatlik: %100 (2s 40dk sonra açılır)
Haftalık: %63
Reset: 1

→ Reset seçeneği gösterilir.
```

veya:

```text
5 saatlik: %38
Haftalık: %100 (3 gün sonra açılır)
Reset: 2

→ Reset seçeneği gösterilir.
```

veya:

```text
5 saatlik: %100 (6 dk sonra açılır)
Haftalık: %70
Reset: 2

→ Reset teklif EDİLMEZ (min_time_to_natural_reset = 15 dk). "6 dk sonra açılacak" gösterilir.
```

### 8.3 Kredi seçimi

Krediler süreli olabilir (`expiresAt`; gözlenen süre ~30 gün). Birden fazla kredi varsa `status == available` olanlar arasından **`expiresAt` değeri en yakın olan** kullanılır.

---

## 9. Çalışma Modları

### 9.1 Manual

Sistem yalnızca durumu gösterir.

Reset işlemi kullanıcı tarafından CLI veya UI üzerinden manuel başlatılır.

### 9.2 Confirm

**Varsayılan mod.**

Limit dolduğunda:

```text
Codex kullanım limitine ulaştı.

5 saatlik: %100
Haftalık: %72
Reset hakkı: 1

[Reset Kullan] [Bekle]
```

### 9.3 Automatic

Limit dolduğunda ve reset hakkı varsa reset otomatik kullanılır.

Bu seçenek:

- Varsayılan kapalı olmalıdır.
- Kullanıcı tarafından açıkça etkinleştirilmelidir.
- Her reset işleminden sonra bildirim üretmelidir.
- Tekrarlanan tüketimi engelleyen korumaya sahip olmalıdır.

---

## 10. Fonksiyonel Gereksinimler

### FR-01 — Codex durumunu okuyabilme

Sistem Codex kullanım limitlerini okuyabilmelidir.

Okunması gereken minimum bilgiler (protokol eşlemesi `CODEX_INTEGRATION.md` §4):

| Bilgi | Kaynak |
|---|---|
| Kullanılabilirlik (otorite) | `ordinaryUsageAllowed` |
| 5 saatlik kullanım oranı / reset zamanı | `windowDurationMins == 300` olan pencere → `usedPercent`, `resetsAt` |
| Haftalık kullanım oranı / reset zamanı | `windowDurationMins == 10080` olan pencere |
| Kullanılabilir reset hakkı | `rateLimitResetCredits.availableCount` |
| Kredi detayları (id, expiresAt, status) | `rateLimitResetCredits.credits[]` |
| Engel tipi | `rateLimitReachedType` |
| Hesap / auth durumu | `accountId`, `account/read` |

`primary` / `secondary` sırasına güvenilmez. Pencereler `windowDurationMins` değerine göre eşlenir.

**MVP:** MVP-1

---

### FR-02 — Limit izleme

Background service belirli aralıklarla limit durumunu kontrol etmelidir.

- **Birincil sinyal: polling.** Fermata'nin kendi App Server instance'ı, kullanıcının başka bir Codex sürecindeki kullanımını push ile göremeyebilir.
- **İkincil sinyal: `account/rateLimits/updated` push bildirimi.** Bildirim geldiğinde hemen yeniden okuma tetiklenir.
- Background poll'lar `excludeResetCreditDetails: true` ile yapılır (hafif okuma). Detaylı okuma yalnızca engel tespit edildiğinde ve reset öncesinde yapılır.

Varsayılan aralık:

```text
30 saniye
```

Kullanıcı tarafından değiştirilebilir. Minimum:

```text
10 saniye
```

Engel durumunda (`blocked`) ve kullanıcı yanıt beklerken aralık 60 saniyeye çıkarılabilir.

**MVP:** MVP-1

---

### FR-03 — Limit tespiti

Tespit, §8.1'deki `blocked` kuralına göre yapılır. Öncelik sırası:

```text
1. ordinaryUsageAllowed == false                       (otorite)
2. ordinaryUsageAllowed == null  → usedPercent >= 100  (yalnızca Confirm/Manual için)
3. ErrorNotification.codexErrorInfo ∈ {usageLimitExceeded, rateLimitExceeded}
   → hemen yeniden okuma tetikler, tek başına reset tetiklemez
```

**MVP:** MVP-1

---

### FR-04 — Reset hakkı kontrolü

Limit dolduğunda sistem **detaylı** okuma (`excludeResetCreditDetails: false`) yaparak kullanılabilir reset hakkını kontrol etmelidir.

```text
availableCount > 0
```

ve §8.2'deki diğer koşullar sağlanıyorsa kullanıcıya reset seçeneği sunulur. Seçilecek kredi §8.3'e göre belirlenir. Teklif ekranında kredinin son kullanma tarihi gösterilir.

**MVP:** MVP-1

---

### FR-05 — Reset kullanma

Kullanıcı reset kullanımını onayladığında `account/rateLimitResetCredit/consume` çağrılır.

Reset isteği:

- Sunucu tarafı idempotency kullanır: her mantıksal deneme için bir UUID `idempotencyKey` üretilir ve **istek gönderilmeden önce** state'e yazılır.
- Aynı olay için ikinci bir mantıksal deneme başlatılmaz.
- Timeout veya bağlantı kopması durumunda **aynı key** ile tekrar denenebilir. Sunucu bu durumda `alreadyRedeemed` döner.
- Yalnızca `blocked == true` iken çağrılır. Limit dolu değilken `consume` davranışı doğrulanmadığı için bu kural kredi kaybını önler.
- Outcome eşlemesi:

| Outcome | Sonraki durum |
|---|---|
| `reset` | `Verifying` |
| `alreadyRedeemed` | `Verifying` (önceki deneme başarılı sayılır) |
| `nothingToReset` | `Healthy` (bilgi bildirimi) |
| `noCredit` | `NoCredit` |

**MVP:** MVP-1

---

### FR-06 — Reset sonrası doğrulama

Reset isteğinden sonra `verify_after_seconds` beklenip limitler tekrar okunur.

Başarı kriteri (ölçülebilir):

```text
ordinaryUsageAllowed == true
OR
(ordinaryUsageAllowed == null AND tetikleyen pencerenin usedPercent < 100)
```

Kriter sağlanmazsa `verify_timeout_seconds` boyunca `verify_interval_seconds` aralıkla tekrar okunur (propagasyon gecikmesi). Süre dolunca:

```text
→ ResetUnconfirmed (terminal)
→ yeni reset denemesi otomatik başlatılmaz
→ kullanıcıya bildirim gösterilir, manuel inceleme istenir
```

**MVP:** MVP-1

---

### FR-07 — Notification

Platform-native bildirim desteklenmelidir.

Destek:

- Windows notification
- macOS notification
- Linux desktop notification

Bildirim mevcut değilse CLI fallback kullanılmalıdır.

**MVP:** MVP-2. MVP-1'de yalnızca terminal çıktısı ve terminal bell kullanılır.

**Not:** MVP-1'de Confirm mode yalnızca `fermata watch` ön planda (TTY) çalışırken onay alabilir. TTY olmayan arka plan sürecinde onay, bildirim aksiyonları üzerinden alınır (MVP-2). O zamana kadar TTY yoksa durum yalnızca loglanır ve `status` çıktısında gösterilir.

---

### FR-08 — CLI

Minimum CLI:

```bash
fermata status
fermata watch
fermata reset
fermata config
fermata doctor
```

Tüm komutlar MVP-1 kapsamındadır.

`reset` seçenekleri:

```bash
fermata reset            # onay ister
fermata reset --yes      # onaysız (script kullanımı)
fermata reset --force    # min_time_to_natural_reset eşiğini uyarıyla geçer
fermata reset --verbose  # ham outcome ve doğrulama adımlarını yazdırır
```

---

### FR-09 — Status

Örnek çıktı:

```text
Codex Usage
──────────────────────────
5 hour       87%
Weekly       63%
Resets        2
Mode        confirm

Status      OK
```

Limit durumunda:

```text
Codex Usage
──────────────────────────
5 hour      100%   resets in 2h 40m
Weekly       63%   resets in 3d 4h
Resets        2    next expires in 9d
Mode        confirm

Status      LIMIT_REACHED
Action      RESET_AVAILABLE
```

`--json` seçeneği makine tarafından okunabilir çıktı üretir.

---

### FR-10 — Doctor

Kurulum ve bağlantı sorunlarını kontrol etmelidir.

```bash
fermata doctor
```

Kontroller:

- Codex executable bulundu mu?
- Codex sürümü test edilmiş aralıkta mı? (`codex --version`)
- Codex App Server başlatılabiliyor mu? (`initialize` yanıtı)
- Authentication mevcut mu? (`account/read`)
- Rate-limit bilgisi okunabiliyor mu? (`account/rateLimits/read`)
- Reset capability mevcut mu? (`rateLimitResetCredits != null` ve şemada `consume` metodu var mı)
- Notification desteği var mı?
- Config okunabiliyor mu?
- Config/state dosya izinleri doğru mu?
- Başka bir Fermata instance'ı lock'u tutuyor mu?

**MVP:** MVP-1

---

### FR-11 — Tek aktif reset yetkilisi (cross-process lock)

Aynı anda `watch`, daemon ve desktop çalışabilir. Bu durumda yalnızca **bir süreç** reset başlatabilir.

- Reset akışı (`Resetting` başlangıcından `Verifying` sonuna kadar) OS seviyesinde bir **exclusive file lock** altında yürütülür (`state.lock`).
- Lock alınamazsa ikinci süreç reset başlatmaz. "Başka bir Fermata örneği reset işlemini yürütüyor" mesajı gösterir.
- State dosyası atomik yazılır (temp dosya + rename).
- Lock alındıktan sonra state yeniden okunur. Devam eden bir deneme (`pendingIdempotencyKey`) varsa yeni deneme başlatılmaz, mevcut deneme sonuçlandırılır.

**MVP:** MVP-1

---

## 11. Desktop UI

Desktop UI zorunlu değildir.

CLI uygulaması tek başına tüm temel fonksiyonları sağlayabilmelidir.

Desktop uygulama şu özellikleri sağlar:

- Tray / menu bar
- Kullanım göstergeleri
- Reset sayısı
- Reset confirmation dialog
- Settings
- Service status

Örnek:

```text
Fermata

5 hour
████████░░ 82%

Weekly
██████░░░░ 61%

Available resets: 2

Mode: Confirm
Status: Monitoring
```

---

## 12. Tray / Menu Bar

### Windows

System Tray

### macOS

Menu Bar

### Linux

System Tray / AppIndicator destekleniyorsa kullanılır.

Tray desteği olmayan desktop environment'larda CLI/background service çalışmaya devam eder.

**Uygulama (MVP-2):** Avalonia `TrayIcon` (Windows tray, macOS menu bar extra, Linux StatusNotifierItem). Tray olmayan masaüstlerinde (ör. eklentisiz GNOME) uygulamayı yeniden başlatmak, çalışan örneğin penceresini öne getirir (tek örnek + named pipe). Başsız Linux için `fermata daemon` + `systemd --user` servisi kullanılır.

---

## 13. Önerilen Teknik Mimari

```text
Fermata
│
├── Core
│   ├── Domain
│   ├── Policies
│   ├── RateLimitMonitor
│   ├── ResetManager
│   └── StateMachine
│
├── CodexIntegration
│   ├── AppServerClient
│   ├── AccountClient
│   ├── RateLimitClient
│   └── ResetCreditClient
│
├── Daemon
│   ├── Worker
│   ├── Scheduler
│   └── EventLoop
│
├── CLI
│
├── Desktop
│   └── Avalonia UI
│
└── Platform
    ├── Windows
    ├── macOS
    └── Linux
```

---

## 14. Teknoloji Stack

### Runtime

```text
.NET 10
```

### Desktop

```text
Avalonia UI
```

### CLI

```text
System.CommandLine
```

veya minimum dependency için özel argument parser.

### Configuration

```text
TOML
```

Önerilen kütüphane:

```text
Tomlyn
```

### Logging

```text
Microsoft.Extensions.Logging
```

### Background Service

```text
Microsoft.Extensions.Hosting
BackgroundService
```

---

## 15. Codex Entegrasyonu

Codex entegrasyonu ayrı bir adapter üzerinden yapılmalıdır.

Amaç:

Codex API/App Server protokolü ileride değişirse Core katmanının etkilenmemesi.

Interface:

```csharp
public interface ICodexUsageClient
{
    Task<CodexUsage> GetUsageAsync(
        bool includeCreditDetails,
        CancellationToken cancellationToken);

    Task<AccountStatus> GetAccountAsync(
        CancellationToken cancellationToken);

    Task<ResetOutcome> ConsumeResetAsync(
        string idempotencyKey,
        string? creditId,
        CancellationToken cancellationToken);

    IObservable<UsageDelta> UsageUpdates { get; }   // account/rateLimits/updated
}

public enum ResetOutcome { Reset, NothingToReset, NoCredit, AlreadyRedeemed }
```

---

## 16. Codex App Server

Faz 0 spike'ında `codex-cli 0.159.3` üzerinde doğrulandı. Ayrıntılar için bkz. [`CODEX_INTEGRATION.md`](CODEX_INTEGRATION.md).

Kullanılan operasyonlar:

```text
initialize / initialized                 handshake
account/read                             auth durumu
account/rateLimits/read                  usage + reset kredileri
account/rateLimitResetCredit/consume     reset (idempotencyKey, creditId?)
account/rateLimits/updated   (notif.)    sparse push güncellemesi
account/updated              (notif.)    auth/plan değişimi
error                        (notif.)    turn hatası (usageLimitExceeded fallback)
```

**Lifecycle (MVP-1):** Fermata `codex app-server` sürecini stdio transport ile kendi child process'i olarak başlatır. Auth `~/.codex` üzerinden paylaşılır. Child process çökerse backoff ile yeniden başlatılır.

Bu metodlar uygulama içinde doğrudan dağınık şekilde kullanılmamalıdır.

Tek bir adapter arkasında tutulmalıdır:

```text
CodexAppServerClient
```

Böylece upstream protokol değişiklikleri izole edilir.

---

## 17. Domain Model

### CodexUsage

```text
CodexUsage
├── AccountId
├── UsageAllowed            (bool?  ← ordinaryUsageAllowed)
├── ReachedType             (← rateLimitReachedType)
├── FiveHour
│   ├── UsedPercent
│   └── ResetAt
│
├── Weekly
│   ├── UsedPercent
│   └── ResetAt
│
├── ResetCredits
│   ├── AvailableCount
│   └── Credits[]           (Id, Status, GrantedAt, ExpiresAt, Title)  — nullable
│
└── ReadAt                  (yerel okuma zamanı)
```

### MonitorState (kanonik enum)

Bu enum hem state machine'in hem `status` çıktısının hem de UI'nin **tek** kaynağıdır.

| State | Anlam |
|---|---|
| `Starting` | Config yüklendi, App Server'a bağlanılıyor |
| `Healthy` | Kullanım serbest |
| `NearLimit` | Eşik aşıldı (MVP-3) |
| `LimitReached` | `blocked == true`, reset teklif koşulları sağlanmıyor (kredi yok, doğal açılma yakın, auth yok) |
| `ResetAvailable` | `blocked == true` ve §8.2 sağlandı |
| `AwaitingConfirmation` | Kullanıcıya teklif gösterildi, yanıt bekleniyor |
| `Resetting` | Lock alındı, key kaydedildi, `consume` gönderildi |
| `Verifying` | Outcome `reset`/`alreadyRedeemed`, doğrulama okuması yapılıyor |
| `ResetSucceeded` | Doğrulama başarılı (kısa süreli; sonra `Healthy`) |
| `ResetFailed` | Kesin başarısızlık (hata yanıtı, `noCredit`) |
| `ResetUnconfirmed` | Doğrulama süresi doldu, sonuç belirsiz. **Terminal**: otomatik reset kilitlenir |
| `AuthRequired` | Auth yok veya geçersiz |
| `Unavailable` | App Server'a erişilemiyor |

---

## 18. Reset State Machine

```text
Healthy ──blocked──▶ LimitReached ──(§8.2 sağlandı)──▶ ResetAvailable
                                                           │
                         Confirm: ──▶ AwaitingConfirmation ─┤ [Bekle] ──▶ LimitReached (bu olay için tekrar sorulmaz)
                         Automatic: (§39 korumaları) ───────┤
                                                           ▼ [Kullan]
                                                       Resetting
                                    ┌──────────────┬───────┴────────┬──────────────┐
                                 reset /        nothingToReset   noCredit      timeout / ağ hatası
                              alreadyRedeemed       │               │               │
                                    ▼               ▼               ▼               ▼
                                Verifying        Healthy       ResetFailed    aynı key ile retry
                              ┌─────┴─────┐                                  (max N) → başarısızsa
                           başarılı    süre doldu                            Verifying (usage oku)
                              ▼           ▼
                       ResetSucceeded  ResetUnconfirmed (terminal)
                              ▼
                           Healthy
```

Kurallar:

- `Resetting` durumuna girmeden **önce** `pendingIdempotencyKey` state'e yazılır.
- Süreç `Resetting` veya `Verifying` sırasında çökerse, yeniden başlangıçta `pendingIdempotencyKey` bulunur ve **aynı key ile** akış sürdürülür. Yeni key üretilmez.
- `ResetUnconfirmed` durumundan yalnızca kullanıcı aksiyonu (`fermata reset --resume` veya UI) veya `Healthy` gözlemi ile çıkılır.

---

## 19. Double Reset Koruması

En kritik güvenlik özelliklerinden biridir.

Aynı limit olayı birden fazla reset tüketmemelidir.

Koruma üç katmanlıdır:

### 19.1 Sunucu tarafı idempotency

`consume` metodu `idempotencyKey` parametresini zorunlu tutar ve aynı key tekrar gönderildiğinde `alreadyRedeemed` döner. Her **mantıksal reset denemesi** için bir **UUID v4** üretilir.

Key, pencere bazlı değil **olay bazlıdır**. İki pencere aynı anda dolsa bile tek kredi ikisini birden sıfırladığı için tek deneme yapılır.

### 19.2 Olay kimliği (yerel)

Aynı engel olayı için ikinci bir mantıksal deneme başlatılmasını engellemek için bir olay kimliği tutulur:

```text
limitEventId = hash(accountId + sorted(engelleyen pencerelerin resetsAt değerleri))
```

`limitEventId` için bir deneme kesin sonuçlandıysa (`reset`, `alreadyRedeemed`, `ResetUnconfirmed`) aynı olay için yeni deneme **yapılmaz**. Yeni bir `limitEventId`, ancak pencereler değiştiğinde (yeni `resetsAt` değerleri) oluşur.

### 19.3 Cross-process lock

Bkz. FR-11.

### Local state

```text
pendingIdempotencyKey      (istek öncesi yazılır, sonuçlanınca temizlenir)
pendingCreditId
pendingLimitEventId
lastResetAttemptAt
lastResetOutcome
lastResetLimitEventId
resetHistory[]             (son N deneme: zaman, outcome, key, eventId)
```

---

## 20. Configuration

Örnek:

```toml
mode = "confirm"

[monitor]
enabled = true
interval_seconds = 30

[limits]
five_hour = true
weekly = true

[notifications]
enabled = true

[reset]
cooldown_seconds = 120
verify_after_seconds = 3
verify_interval_seconds = 5
verify_timeout_seconds = 120
min_time_to_natural_reset_minutes = 15
consume_retry_max = 3              # aynı idempotency key ile

[automatic]                        # yalnızca mode = "automatic" iken geçerli
max_resets_per_day = 1
max_resets_per_week = 2
require_usage_allowed_false = true # ordinaryUsageAllowed == null iken asla otomatik reset yapma

[codex]
executable = ""                    # boşsa PATH'te aranır

[ui]
start_minimized = true
```

---

## 21. Local Data

Uygulamanın minimum local state'i:

```text
config
last known usage
last reset action
idempotency key
application logs
```

Kullanıcı mesajları, Codex promptları ve proje kaynak kodları saklanmaz.

---

## 22. Dosya Konumları

### Windows

```text
%APPDATA%/Fermata/
```

### macOS

```text
~/Library/Application Support/Fermata/
```

### Linux

```text
~/.config/fermata/
```

Platform-specific path yalnızca infrastructure adapter tarafından belirlenmelidir.

---

## 23. Arka Planda Çalışma

### Windows

Başlangıçta:

```text
User startup / background process
```

İleri sürüm:

```text
Windows Service
```

gerekirse değerlendirilebilir.

### macOS

```text
LaunchAgent
```

### Linux

```text
systemd --user
```

Core daemon aynı executable veya aynı Worker bileşeni üzerinden çalışmalıdır.

---

## 24. macOS Gereksinimleri

macOS birinci sınıf desteklenen platform olmalıdır.

Destek:

- Apple Silicon
- x64 opsiyonel
- Menu bar
- Native notification integration
- LaunchAgent background startup
- `.app` bundle

Production dağıtım için:

- Code signing
- Notarization

gerekecektir.

---

## 25. Linux Gereksinimleri

Desteklenen minimum kullanım:

```bash
fermata watch
```

Desktop environment bulunması zorunlu değildir.

Headless kullanım:

```text
SSH
server
container-like developer environment
```

senaryolarında CLI ve daemon çalışabilmelidir.

---

## 26. Hata Yönetimi

### Codex App Server yok

```text
CODEX_UNAVAILABLE
```

Retry uygulanır.

### Authentication yok

```text
AUTH_REQUIRED
```

Reset gönderilmez.

### Reset yok

```text
NO_RESET_CREDIT
```

Kullanıcıya yalnızca limitin doğal reset zamanı gösterilir.

### Reset isteği timeout

**Yeni key ile** ikinci reset gönderilmez.

Aynı `idempotencyKey` ile en fazla `consume_retry_max` kez tekrar denenebilir (sunucu `alreadyRedeemed` ile korur). Denemeler tükenirse usage okunarak doğrulama yapılır (`Verifying`). Sonuç belirsiz kalırsa durum `ResetUnconfirmed` olur.

### Usage durumu bilinmiyor

```text
USAGE_UNKNOWN   (ordinaryUsageAllowed == null)
```

Yüzdeler gösterilir. Automatic reset yapılmaz.

### Workspace limiti

`rateLimitReachedType` değeri `workspace_*` ise engel, kullanıcının kişisel limiti değil workspace kredisi/limitidir. Reset teklif edilmez ve kullanıcıya workspace yöneticisine başvurması gerektiği bildirilir.

### Ağ problemi

Exponential backoff uygulanır.

---

## 27. Retry

Read operasyonları retry edilebilir.

Örnek:

```text
1s
2s
5s
10s
30s
```

Reset consume operasyonunda **yeni key ile** blind retry yapılmamalıdır.

Retry yalnızca aynı `idempotencyKey` ile ve sınırlı sayıda yapılır. Ardından state doğrulanır.

---

## 28. Güvenlik

### Secrets

Uygulama kullanıcıdan OpenAI API key istememelidir.

Mümkünse mevcut Codex authentication context kullanılmalıdır.

### Log sanitization

Loglara aşağıdakiler yazılmamalıdır:

- tokens
- auth headers
- cookies
- credential material
- e-posta adresi (`account/read` yanıtındaki `email`)

`accountId` loglanabilir. Kredi `id` değerleri yalnızca Debug seviyesinde loglanır.

### Local permissions

Config ve state dosyaları yalnızca ilgili OS kullanıcısı tarafından erişilebilir olmalıdır.

- macOS / Linux: `0600` dosya, `0700` dizin.
- Windows: Miras alınan ACL kaldırılır, yalnızca mevcut kullanıcıya (ve SYSTEM'e) erişim verilir.

Fermata Codex auth dosyalarını (`~/.codex/auth.json` vb.) **doğrudan okumaz**. Auth işlemleri tamamen App Server üzerinden yürür.

---

## 29. Privacy

Uygulama aşağıdaki içerikleri okumamalıdır:

- proje source code
- Codex promptları
- sohbet içeriği
- repository dosyaları

Sadece usage/reset için gereken account metadata kullanılmalıdır.

---

## 30. Observability

Local structured logging yeterlidir.

Log levels:

```text
Error
Warning
Information
Debug
Trace
```

Default:

```text
Information
```

CLI:

```bash
fermata logs [-n N] [--json] [--path]
```

**Uygulama (MVP-1):**

- Dosya: `<veri dizini>/logs/fermata-YYYY-MM-DD.log`, satır başına bir JSON nesnesi (`ts`, `level`, `category`, `event`, `msg`, `props`, `exception`).
- Saklama: `[logging] retention_days` (varsayılan 14). Günlük dosya üst sınırı 20 MB.
- Birden fazla Fermata süreci aynı dosyaya güvenli şekilde yazabilir (kısa, kilitli ekleme ve yeniden deneme).
- Tüm metinler §28 gereği `LogSanitizer`'dan geçer (e-posta, Bearer, JWT, `sk-` ve `gh*_` anahtarları, `token=`/`password:` atamaları).
- Codex isteklerinin parametreleri ve yanıtları loglanmaz. Yalnızca metod adı, süre ve hata loglanır.
- Olay ID aralıkları: 1–3 CLI, 1xx reset, 2xx monitör, 3xx Codex entegrasyonu.

---

## 31. Event Model

Örnek internal events:

```text
UsageUpdated
LimitApproaching
LimitReached
ResetAvailable
ResetRequested
ResetStarted
ResetSucceeded
ResetFailed
ResetUnconfirmed
AuthRequired
CodexUnavailable
```

Event adları §17'deki `MonitorState` adlarıyla tutarlıdır.

Bu event modeli GUI ve CLI'nin Core'dan ayrılmasını kolaylaştırır.

---

## 32. Notification Kuralları

Spam engellenmelidir.

Aynı limit için sürekli bildirim gönderilmemeli.

Öneri:

```text
LimitReached notification
↓
acknowledged
↓
tekrar bildirim yok
↓
state değişirse yeniden bildirim
```

---

## 33. Near Limit

MVP için zorunlu değildir.

Sonraki sürüm:

```text
80%
90%
95%
```

eşikleri kullanılabilir.

Örnek:

```text
Codex 5 saatlik kullanımınız %90 seviyesinde.
```

---

## 34. Startup Davranışı

Uygulama başlangıcında:

1. Config yüklenir.
2. Codex erişimi kontrol edilir.
3. Usage okunur.
4. State oluşturulur.
5. Monitor başlatılır.

Eğer Codex çalışmıyorsa uygulama kapanmaz.

Monitor düşük frekansta Codex'in kullanılabilir olmasını bekler.

---

## 35. Codex Process Detection

Codex process detection yardımcı sinyal olarak kullanılabilir.

Ancak sistem yalnızca process adına bağlı olmamalıdır.

Çünkü:

- CLI
- Desktop
- App Server
- gelecekte farklı Codex client'ları

ayrı process davranışı gösterebilir.

---

## 36. CLI UX

### Watch

```bash
fermata watch
```

Örnek:

```text
08:24  5h 62%  weekly 41%  resets 2
08:28  5h 71%  weekly 42%  resets 2
08:34  5h 100% weekly 44%  resets 2

Limit reached.

Use one reset credit? [y/N]
```

---

## 37. Desktop UX

Ana pencere şart değildir.

Menu bar / tray ana interaction olabilir.

Menu:

```text
Fermata
────────────────
5h usage        82%
Weekly          63%
Resets           2
────────────────
Reset now
Mode > Confirm
Open settings
Quit
```

---

## 38. Reset Confirmation

Confirmation aşağıdaki bilgileri göstermelidir:

```text
Codex 5 saatlik limitine ulaşıldı.

5 saatlik kullanım: %100   (2s 40dk sonra kendiliğinden açılır)
Haftalık kullanım:  %67

Kullanılabilir reset: 2
Kullanılacak kredi: "Full reset (Weekly + 5 hr)" — 9 gün sonra sona eriyor

Bu işlem hem 5 saatlik hem haftalık limiti sıfırlar.
Bir reset hakkı kullanılsın mı?

[Reset Kullan]
[Bekle]
```

Kullanıcının hangi limit nedeniyle durduğu ve doğal açılma süresi açıkça belirtilmelidir.

Kredi her iki pencereyi birlikte sıfırladığı için, haftalık kullanım düşükken yalnızca 5 saatlik limit için reset kullanmak kredinin değerinin bir kısmını kullanmak anlamına gelir. Haftalık kullanım `%50` değerinin altındaysa teklif ekranında bu bir not olarak gösterilir.

---

## 39. Automatic Mode Koruması

Automatic mode'da:

- available reset > 0 olmalı
- gerçek limit engeli doğrulanmalı: `ordinaryUsageAllowed == false` (null kabul edilmez)
- engel workspace kaynaklı olmamalı (`rateLimitReachedType` `workspace_*` değil)
- doğal açılmaya kalan süre `min_time_to_natural_reset` değerinden büyük olmalı
- önceki reset cooldown süresi geçmiş olmalı
- aynı `limitEventId` için daha önce deneme yapılmamış olmalı
- `max_resets_per_day` / `max_resets_per_week` aşılmamış olmalı
- son durum `ResetUnconfirmed` olmamalı
- cross-process lock alınmış olmalı
- reset sonrası state doğrulanmalı

---

## 40. MVP

### MVP-1

CLI-first sürüm.

İçerik:

- Codex App Server bağlantısı
- Usage okuma
- Reset hakkı okuma
- Limit detection
- `status`
- `watch`
- `reset`
- `config`
- `doctor`
- Confirm mode
- Double-reset koruması (idempotency, olay kimliği, cross-process lock)
- Config
- Logging
- Cross-platform build

Bu sürüm GUI olmadan yayınlanabilir.

---

## 41. MVP-2

Desktop integration.

- Avalonia UI
- Windows tray
- macOS menu bar
- Linux tray
- Native notifications
- Start at login

**Durum: tamamlandı (2026-10-02).**

| Parça | Uygulama |
|---|---|
| Masaüstü uygulaması | `FermataApp` (Avalonia 12): durum penceresi, ayarlar, reset onay penceresi (§38), son olaylar. Kapatınca tray'e gizlenir. |
| Tray / menu bar | Menü (§37): kullanım, krediler, "Reset now…", Mode alt menüsü, pencere, loglar, çıkış. Simge çalışma anında çizilir (kullanım halkası, sağlık rengi). |
| Bildirimler | Windows: PowerShell WinRT toast. macOS: `osascript display notification`. Linux: `notify-send`. Yoksa terminal/log. Limit olayı ve reset sonucu başına bir bildirim (§32). |
| Açılışta başlatma | Windows: HKCU Run (tray uygulaması). macOS: LaunchAgent (`com.fermata.desktop` / `com.fermata.daemon`). Linux: XDG autostart (masaüstü) veya `systemd --user` servisi (daemon). CLI: `fermata autostart status/enable/disable [--target]`; uygulamada "Start Fermata at login". |
| Arka plan servisi | `fermata daemon`: başsız monitör; soru sormaz, confirm modunda yalnızca bildirir; automatic modda tüm korumalarla reset yapar. |
| Paketleme | `scripts/package.sh <rid>`: Windows zip, Linux tar.gz, macOS `Fermata.app` (LSUIElement, yalnızca menü çubuğu). CLI ve uygulama yan yana; büyük/küçük harf çakışması kontrolü. |
| Doğrulama | Headless render testleri (ekran görüntüleri), host ve view model testleri; CI'da her OS'ta paket + sahte Codex'e karşı daemon smoke testi. Windows'ta gerçek pencereyle UI Automation e2e (onay → tek consume) yapıldı. |

Uygulamanın çalıştırılabilir adı `FermataApp`'tir: `Fermata` adı, büyük/küçük harf ayırmayan dosya sistemlerinde (macOS, Windows) `fermata` CLI'ı ile çakışır.

---

## 42. MVP-3

Automation improvements.

**Durum (2026-10-03):** Aşağıdaki maddeler uygulandı. Release'lerin imzalanması (Authenticode, notarization) ayrı iş olarak kalıyor (§43).

| Madde | Uygulama |
|---|---|
| Automatic mode | Masaüstünde açık onay penceresi (korumalar listelenir); reddedilirse önceki moda döner. CLI'da `config.toml` ile. |
| Near-limit warnings | `[near_limit]` (varsayılan 80/90/95). Eşik başına pencere dönemi içinde bir kez; aynı anda birden fazla eşik geçilirse yalnızca en yükseği. Terminal, daemon logu, masaüstü olayları ve bildirim. |
| Better retry/recovery | Yarım kalan deneme başlangıçta bildirilir, masaüstünde "Finish pending reset" (aynı idempotency key). `state.json.bak` ile bozuk durum dosyasından kurtarma. Yeniden bağlanma 1 sn → 5 dk, log seyreltme. Eşzamanlı log yazımında satır kaybı düzeltildi. |
| Self-update | `fermata update [--check]`, masaüstünde günde bir kontrol + "Install update". GitHub Releases + `SHA256SUMS.txt` doğrulaması; Unix'te yerinde klasör değişimi, Windows'ta çıkıştan sonra çalışan yardımcı (hata olursa geri alma). Geliştirme derlemeleri hiçbir zaman değiştirilmez. `[updates] check_automatically`. Release: `v*` tag'i → `.github/workflows/release.yml`. |
| Diagnostics export | `fermata diagnostics`, masaüstünde "Export diagnostics": sistem bilgisi, config, state, son 7 günün logları, doctor çıktısı. Yeniden temizlenir; kullanıcı profili yolu `~` olur. |

Güncelleme kontrolü, Fermata'nin Codex dışındaki tek ağ isteğidir: herkese açık GitHub release bilgisini okur, kullanıcıya ait veri göndermez (User-Agent yalnızca sürümü içerir).

- Automatic mode
- Near-limit warnings
- Better retry/recovery
- Self-update
- Diagnostics export

---

## 43. Paketleme

Hedef çıktılar:

### Windows

```text
win-x64
win-arm64
```

### macOS

```text
osx-arm64
osx-x64
```

### Linux

```text
linux-x64
linux-arm64
```

Self-contained deployment tercih edilmelidir.

### Windows kod imzalama

Windows 11'de **Smart App Control** açık olan kullanıcılarda imzasız ve itibarı bilinmeyen binary'ler engellenebilir. Bu durum geliştirme sırasında gözlendi (2026-10-02): imzasız `Fermata.Core.dll` Code Integrity tarafından engellendi (olay 3033/3077), aynı kodun farklı hash'li derlemesi ise yüklendi. Karar binary bazında ve öngörülemez.

Bu nedenle Windows release'leri için:

- Tüm `.exe` ve `.dll` dosyaları **Authenticode ile imzalanmalıdır** (EV veya Azure Trusted Signing).
- İmzasız geliştirme build'lerinin Smart App Control açık makinelerde engellenebileceği README'de belirtilmelidir.
- Avalonia'nın NuGet DLL'leri de imzasızdır (yalnızca SkiaSharp Microsoft imzalı); `Avalonia.Build.Tasks.dll` Smart App Control açık makinede **derlemeyi** de engelledi. Release için önerilen yol: masaüstü uygulamasını ve CLI'ı **NativeAOT** ile tek native exe olarak yayınlamak ve yalnızca bu exe'leri imzalamak. Geliştirmede derleme Linux konteynerinde yapılabilir (`scripts/dotnet-in-docker.sh`).

---

## 44. Release Artifacts

Örnek:

```text
fermata-win-x64.zip
fermata-win-arm64.zip

fermata-macos-arm64.tar.gz
fermata-macos-x64.tar.gz

fermata-linux-x64.tar.gz
fermata-linux-arm64.tar.gz
```

İleri sürüm:

```text
Homebrew
WinGet
Chocolatey
AUR
deb/rpm
```

---

## 45. Repository Yapısı

```text
fermata/
│
├── src/
│   ├── Fermata.Core/
│   ├── Fermata.Codex/
│   ├── Fermata.Daemon/
│   ├── Fermata.Cli/
│   ├── Fermata.Desktop/
│   └── Fermata.Platform/
│
├── tests/
│   ├── Fermata.Core.Tests/
│   ├── Fermata.Codex.Tests/
│   └── Fermata.IntegrationTests/
│
├── docs/
│   ├── Fermata-PRD.md
│   ├── TECHNICAL_ARCHITECTURE.md
│   ├── CODEX_INTEGRATION.md
│   ├── SECURITY.md
│   └── RELEASE.md
│
├── scripts/
│
├── .github/
│   └── workflows/
│
├── Directory.Build.props
├── Fermata.sln
└── README.md
```

---

## 46. Test Gereksinimleri

### Unit tests

- Limit state detection
- Reset policy
- State machine
- Cooldown
- Idempotency
- Config parsing

### Integration tests

Mock App Server üzerinden:

- usage read
- `ordinaryUsageAllowed` true / false / null
- primary/secondary sırası ters gelen pencereler
- no reset credits (`noCredit`)
- reset success (`reset`)
- `nothingToReset`
- `alreadyRedeemed` (aynı key ile retry)
- reset timeout → aynı key ile retry
- reset ambiguous result → `ResetUnconfirmed`
- süreç `Resetting` sırasında öldürülüp yeniden başlatılıyor → aynı key ile devam
- iki süreç aynı anda reset denemesi yapıyor → yalnızca biri `consume` gönderiyor
- workspace limiti
- app server unavailable / child process crash

### Protokol uyumluluk testi

CI'da `codex app-server generate-json-schema` çıktısı, kullanılan metod ve tiplerin snapshot'ıyla karşılaştırılır. Upstream şema değişikliği build'i uyarı ile işaretler.

### Platform tests

Minimum CI matrix:

```text
Windows
macOS
Linux
```

---

## 47. Acceptance Criteria

MVP başarılı kabul edilirken:

1. Windows, macOS ve Linux üzerinde CLI başlatılabilmeli.
2. Codex usage verisi okunabilmeli.
3. 5 saatlik limit ayrı tespit edilebilmeli.
4. Haftalık limit ayrı tespit edilebilmeli.
5. Reset hakkı sayısı okunabilmeli.
6. Limit dolduğunda kullanıcıdan confirmation alınmalı.
7. Onaydan sonra yalnızca bir reset tüketilmeli.
8. Reset sonucu doğrulanmalı.
9. Network/App Server hatasında reset iki kez tüketilmemeli.
10. Kullanıcının source code veya prompt verisi okunmamalı.

---

## 48. Başarı Metrikleri

Local utility olduğu için telemetry zorunlu değildir.

Ürün başarısı teknik olarak ölçülür:

```text
Limit detection reliability
Reset success rate
Duplicate reset count = 0
False reset trigger count = 0
Background CPU usage
Background memory usage
```

Hedef:

```text
Duplicate reset: 0
False reset: 0
Idle CPU: <1%
```

---

## 49. Riskler

### Codex internal protocol değişiklikleri

En büyük teknik risk. `codex app-server` komut grubu CLI'da hâlâ `[experimental]` olarak işaretli. Kullanılan metodlar ise stable şemada.

Çözüm:

- Codex entegrasyonunu adapter arkasında izole etmek.
- CI'da şema snapshot karşılaştırması yapmak.
- `doctor` içinde Codex sürüm kontrolü yapmak.

### Kredi değerinin boşa harcanması

Kredi iki pencereyi birlikte sıfırlar ve süreli olabilir. Doğal açılmaya az süre kalmışken veya haftalık kullanım düşükken reset kullanmak değer kaybıdır. Bunu `min_time_to_natural_reset` eşiği, teklif ekranındaki bilgiler ve en yakın tarihte sona eren kredinin seçilmesi azaltır.

### Polling görünürlüğü

Fermata'nin kendi App Server instance'ı, ayrı Codex süreçlerindeki kullanımı push ile göremeyebilir. Bu nedenle polling zorunludur. Polling sıklığının sunucu tarafında limite takılıp takılmadığı henüz doğrulanmadı.

### Reset capability değişikliği

OpenAI reset mekanizmasını değiştirebilir veya kaldırabilir.

Ürün reset özelliğini capability detection ile kontrol etmelidir.

### Automatic mode

Yanlış implementasyon reset hakkını gereksiz tüketebilir.

Bu nedenle Confirm varsayılandır.

### Windows Smart App Control

İmzasız binary'ler son kullanıcı makinesinde engellenebilir. Çözüm: Windows release'lerini imzalamak (bkz. §43).

### Tray portability

Avalonia temel UI taşınabilir olsa da tray/menu bar davranışları platformlar arasında farklılık gösterebilir.

Platform adapter gerekir.

---

## 50. Açık Teknik Kararlar

### Faz 0 spike ile kapatılanlar (2026-10-02, `codex-cli 0.159.3`)

| Konu | Karar / Bulgu |
|---|---|
| App Server lifecycle | Kendi child process'imiz, stdio transport |
| Mevcut App Server'a bağlanma | MVP'de yok. İleride `codex app-server proxy` / daemon değerlendirilecek |
| Auth paylaşımı | `~/.codex` üzerinden otomatik. API key gerekmiyor |
| Consume response formatı | `{ outcome: reset \| nothingToReset \| noCredit \| alreadyRedeemed }` |
| Idempotency | Sunucu tarafında var (`idempotencyKey` zorunlu) |
| Reset kapsamı | Full reset: haftalık + 5 saatlik |
| Push desteği | `account/rateLimits/updated` var. Polling birincil, push ikincil |

### Hâlâ açık olanlar

İlk gerçek limit olayında kontrollü olarak gözlenecekler:

- Reset sonrası `ordinaryUsageAllowed` ve `usedPercent` için propagasyon gecikmesi
- `nothingToReset` dönüşünde kredinin tüketilmediği
- Ayrı Codex süreçlerindeki kullanımın push ile gelip gelmediği
- Polling için sunucu tarafı limit olup olmadığı

MVP-2'de kapatılanlar:

- macOS notification adapter: `osascript` (`display notification`; metin argv ile geçer, kaçış gerektirmez).
- Linux notification fallback: `notify-send` yoksa veya grafik oturum yoksa terminal çıktısı ve log.
- Windows notification: PowerShell'in WinRT köprüsü (ek paket veya Windows'a özel TFM gerekmez). Gönderen "Windows PowerShell" görünür; kendi AppUserModelID'si için installer'da Start Menu kısayolu gerekir (paketleme işi).

Bu kararlar ürün kapsamını değiştirmez; entegrasyon implementasyonunu etkiler.

---

## 51. Ürün Kararı

İlk geliştirme **CLI-first** yapılmalıdır.

Sıra:

```text
Faz 0 spike ✅
→ Core
→ Codex Integration
→ CLI status
→ CLI reset
→ Monitor/watch
→ Confirm flow
→ Daemon
→ Notifications
→ Avalonia Desktop
```

Bu yaklaşım Codex entegrasyonunun GUI geliştirmeden önce doğrulanmasını sağlar ve Windows, macOS, Linux desteğinin temelini en erken aşamada test eder.

---

## 52. MVP Sonucu

İlk kullanılabilir sürümde kullanıcı şu komutu çalıştırabilmelidir:

```bash
fermata watch
```

ve ardından sistem herhangi bir Codex limiti dolduğunda:

```text
5-hour Codex limit reached (opens naturally in 2h 40m).
Weekly usage: 64%
Reset credits: 1 (expires in 9d) — resets both 5h and weekly

Use reset credit? [y/N]
```

göstermeli; kullanıcı `y` dediğinde reset uygulanmalı, doğrulanmalı ve monitoring devam etmelidir.

Bu, ürünün temel değer önerisini tek başına karşılayan minimum sürümdür.
