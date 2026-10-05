# Codex Quota Supervisor
## Kota Bilinçli Checkpoint, Zamanlama ve Otomatik Devam Sistemi

**Doküman türü:** Ürün + teknik tasarım dokümanı  
**Durum:** Taslak / uygulanabilir mimari  
**Tarih:** 4 Ekim 2026  
**Çalışma adı:** Codex Quota Supervisor (CQS)

---

## 1. Amaç

Codex kullanımında 5 saatlik ve haftalık kullanım pencereleri bulunabilir. Her iki limit de geçerliyse, çalışmanın devam edebilmesi için iki pencerede de kullanılabilir hak bulunması gerekir.

Bu sistemin amacı limiti "resetlemek" değildir. Sunucu tarafındaki kullanım kotasını değiştirmek mümkün değildir ve ürünün hedefi bu olmamalıdır.

Amaç şudur:

> Codex ile yürütülen uzun süreli bir işi kota tükenmeden güvenli bir noktada checkpoint'e almak, gerekli bağlamı kalıcılaştırmak, gerçek reset zamanını beklemek ve kota tekrar kullanılabilir olduğunda aynı işi mümkün olduğunca aynı thread/context üzerinden otomatik devam ettirmek.

Sistem ayrıca kullanıcıya işi ileri bir saate planlama imkânı vermelidir:

```bash
cqs resume --at "23:30"
cqs resume --in 3h
cqs resume --when-quota-available
```

Temel yaklaşım:

```text
RUN
 ↓
Quota izle
 ↓
Checkpoint hazırla
 ↓
Yeni ağır işi durdur
 ↓
Thread + proje durumunu kaydet
 ↓
Reset zamanına kadar bekle
 ↓
Quota'yı tekrar doğrula
 ↓
Thread'i resume et
 ↓
İşe devam et
```

---

## 2. Temel Tasarım Kararı

Bu sistem bir "Codex reset aracı" olarak tasarlanmamalıdır.

Doğru ürün tanımı:

> **Quota-aware Codex job supervisor**

Yani Codex'in üstünde çalışan, iş yaşam döngüsünü yöneten küçük bir kontrol katmanı.

Supervisor'ın sorumlulukları:

1. Codex kullanım limitlerini izlemek.
2. Çalışan işi ve thread kimliğini takip etmek.
3. Kota kritik seviyeye geldiğinde checkpoint üretmek.
4. Yeni büyük işlerin başlamasını engellemek.
5. İşin devam edebilmesi için gereken proje durumunu saklamak.
6. Reset zamanını hesaplamak.
7. Reset sonrasında kotayı tekrar kontrol etmek.
8. Aynı Codex thread'ini yeniden açmak.
9. İşe kaldığı noktadan devam komutu vermek.
10. Makine yeniden başlasa bile işi kaybetmemek.
11. Manuel zamanlanmış işleri de aynı mekanizma üzerinden çalıştırmak.

---

## 3. Codex Tarafında Kullanacağımız Güncel Yetkinlikler

Mimarinin mümkün olduğu üç temel Codex yeteneği vardır.

### 3.1 Rate limit bilgisi

Codex app-server protokolünde:

```text
account/rateLimits/read
```

ile kullanım limiti snapshot'ı alınabilir.

Bir kullanım penceresinde şu bilgiler bulunabilir:

```text
used_percent
window_minutes
resets_at
```

Örnek mantıksal veri:

```json
{
  "primary": {
    "usedPercent": 91.2,
    "windowMinutes": 300,
    "resetsAt": 1791154800
  },
  "secondary": {
    "usedPercent": 64.1,
    "windowMinutes": 10080,
    "resetsAt": 1791440400
  }
}
```

Supervisor şu hesabı yapar:

```text
remaining_percent = 100 - used_percent
```

Önemli:

`primary = 5 saat` ve `secondary = hafta` varsayımı kod içine sabit yazılmamalıdır.

Doğru yöntem `window_minutes` üzerinden pencereyi sınıflandırmaktır.

Örneğin:

```text
~300 dakika   → 5 saatlik pencere
~10080 dakika → haftalık pencere
```

Backend tarafındaki değişikliklere dayanıklılık için isim yerine süre bazlı algılama tercih edilmelidir.

---

### 3.2 Thread devam ettirme

Codex app-server:

```text
thread/start
thread/resume
turn/start
```

akışını destekler.

Yeni iş:

```text
thread/start
      ↓
thread_id kaydet
      ↓
turn/start
```

Devam eden iş:

```text
thread/resume(thread_id)
      ↓
turn/start
```

`thread/resume` mümkün olduğunda thread ID ile yapılmalıdır.

Bu sistem açısından en önemli alanlardan biri:

```text
thread_id
```

olacaktır.

Thread ID kaybolursa aynı konuşma bağlamına dönmek zorlaşır. Bu nedenle job state ile birlikte kalıcı olarak saklanmalıdır.

---

### 3.3 Codex Goal desteği

Codex'in güncel sürümlerinde thread-scoped Goal desteği vardır.

Interactive Codex tarafında:

```text
/goal
/goal pause
/goal resume
/goal clear
```

kullanılabilir.

App-server tarafında da goal kontrol düzlemi bulunmaktadır:

```text
thread/goal/set
thread/goal/get
thread/goal/clear
```

Bu sistem için Goal faydalıdır; çünkü "bir sonraki prompt" yerine uzun süreli işin tamamlanma hedefini temsil eder.

Örnek:

```text
GOAL:
GOAL-06 kapsamındaki düzeltme, ters kayıt ve mutabakat işlerini
tamamla; PostgreSQL, Core ve browser testlerini geçir;
ilgili proje dokümanlarını güncelle.
```

Ancak mimari yalnızca Goal özelliğine bağımlı olmamalıdır.

Goal kullanılamazsa supervisor kendi `job.goal` alanını ve `handoff.md` dosyasını kullanabilmelidir.

---

## 4. Kritik Sınırlama

MVP şu varsayımla geliştirilmelidir:

> Supervisor yalnızca kendisinin başlattığı veya açıkça kaydettiği Codex thread'lerini tam otomatik yönetir.

Zaten Codex Desktop/TUI içinde manuel olarak çalışan, supervisor'ın başlatmadığı aktif bir oturuma dışarıdan bağlanmak daha kırılgan bir senaryodur.

Bu nedenle ilk sürümde:

```text
cqs run ...
```

ile başlatılmış işler garanti edilen yol olmalıdır.

Sonraki sürümde:

```text
cqs adopt
```

gibi mevcut thread'i sahiplenmeye çalışan bir özellik eklenebilir; fakat bu best-effort olmalıdır.

---

# 5. Kullanıcı Deneyimi

## 5.1 Normal çalışma

Kullanıcı proje dizininde:

```bash
cqs run
```

veya:

```bash
cqs run --goal GOAL-06.md
```

çalıştırır.

CQS:

1. Codex app-server'ı başlatır.
2. Codex hesabını doğrular.
3. Kota bilgisini alır.
4. Thread oluşturur veya mevcut job thread'ini resume eder.
5. Job state oluşturur.
6. Codex çalışmasını başlatır.
7. Codex eventlerini izler.
8. Kota güncellemelerini takip eder.

---

## 5.2 Kota azalırken

Örneğin:

```text
5h remaining: 12%
weekly remaining: 44%
```

durumunda sistem normal çalışır fakat checkpoint hazırlık moduna girer.

```text
remaining <= 10%
```

olduğunda:

```text
CHECKPOINT_PREPARE
```

durumuna geçilir.

Amaç kalan kotayı tamamen tüketmeden semantik handoff oluşturmaktır.

---

## 5.3 Kritik seviye

Örneğin:

```text
5h remaining: 4.7%
```

olduğunda:

```text
STOP_NEW_WORK
```

aktif edilir.

Bu noktadan sonra supervisor:

- yeni geniş kapsamlı turn başlatmaz,
- yeni refactor başlatmaz,
- yeni araştırma başlatmaz,
- yalnızca mevcut işi güvenli kapatma/checkpoint işlemlerine izin verir.

---

## 5.4 Limit dolduğunda

```text
5h remaining: 0%
```

olursa:

```text
WAITING_FOR_QUOTA
```

durumuna geçilir.

State kaydedilir.

Örneğin:

```text
5h reset:     2026-10-05 01:32
weekly reset: 2026-10-08 16:10
```

Her iki limit de blokluyorsa gerçek resume zamanı:

```text
max(blocking_reset_times)
```

olmalıdır.

Yani:

```text
resume_at = en geç reset zamanı
```

Sadece "5 saat sonra tekrar çalıştır" yaklaşımı yanlış olur.

---

# 6. Threshold Politikası

Önerilen varsayılan değerler:

```yaml
quota:
  checkpoint_prepare_remaining: 10
  hard_stop_remaining: 5
  emergency_remaining: 2
```

Durumlar:

| Kalan kota | Durum | Davranış |
|---|---|---|
| > %15 | NORMAL | Normal çalışma |
| %15 - %10 | WATCH | Daha sık quota kontrolü |
| <= %10 | PREPARE_CHECKPOINT | Semantik checkpoint hazırlığı |
| <= %5 | STOP_NEW_WORK | Yeni büyük turn başlatma |
| <= %2 | EMERGENCY | Yalnızca mekanik state koruma |
| %0 / rate-limit error | WAITING_QUOTA | Çalışmayı durdur |

### Neden yalnızca %5 kullanılmamalı?

Bir Codex turn'ünün maliyeti sabit değildir.

Tek bir ağır çalışma:

```text
remaining 8%
→
remaining 0%
```

şeklinde bitebilir.

Bu nedenle checkpoint üretimi %5'e bırakılmamalıdır.

---

# 7. Checkpoint Tasarımı

Checkpoint iki katmanlı olmalıdır.

## 7.1 Mekanik checkpoint

LLM kullanmadan oluşturulabilir.

Bu nedenle quota tamamen bittiğinde bile yapılabilir.

İçerik:

```text
thread_id
turn_id
job_id
cwd
git branch
git HEAD
git status
changed files
uncommitted diff bilgisi
goal
son tamamlanan turn
son hata
quota snapshot
resume_at
job state
```

Dosya:

```text
.codex-resume/state.json
```

Örnek:

```json
{
  "schemaVersion": 1,
  "jobId": "vira-goal-06",
  "status": "waiting_quota",
  "project": "D:/Projects/ViraTYS",
  "threadId": "thr_xxx",
  "lastTurnId": "turn_xxx",
  "goal": "GOAL-06",
  "git": {
    "branch": "feature/goal-06",
    "head": "31a4fc0",
    "dirty": true
  },
  "quota": {
    "fiveHourRemaining": 0,
    "fiveHourResetAt": "2026-10-05T01:32:00+03:00",
    "weeklyRemaining": 31,
    "weeklyResetAt": "2026-10-08T16:10:00+03:00"
  },
  "resume": {
    "mode": "when_quota_available",
    "notBefore": "2026-10-05T01:34:00+03:00"
  }
}
```

---

## 7.2 Semantik checkpoint

Codex'in kendisine iş durumu özetletilir.

Dosya:

```text
.codex-resume/handoff.md
```

Örnek:

```markdown
# Resume Context

## Active Goal
GOAL-06 — Düzeltme, Ters Kayıt ve Mutabakat

## Completed
- reversal migration tamamlandı
- correction API tamamlandı
- reconciliation service tamamlandı

## Current Work
Browser reconciliation testindeki son senaryo üzerinde çalışılıyor.

## Remaining
- failing browser test
- acceptance kontrolü
- dokümantasyon güncellemesi

## Verification
- PostgreSQL: 78/78
- Core: 11/11
- Playwright: 4/5

## Important Decisions
- AggregateAmount davranışını değiştirme
- mevcut entitlement rezervasyon davranışını koru

## Next Best Action
Failing Playwright reconciliation testini çalıştır ve root cause'u çöz.

## Do Not
- kapsam dışı refactor yapma
- veri modelini yeniden tasarlama
```

Bu belge yeni bir thread açılması gerekse bile işi kurtarabilmelidir.

---

# 8. Checkpoint Üretme Algoritması

## %10 seviyesinde

Supervisor Codex'e kısa ve kontrollü bir checkpoint turn'ü gönderebilir:

```text
Kalan kullanım kotası kritik seviyeye yaklaşıyor.

Yeni geliştirme yapma.
Mevcut çalışmanın durumunu checkpoint için özetle.

Şunları üret:
1. aktif hedef
2. tamamlanan işler
3. devam eden iş
4. açık işler
5. önemli teknik kararlar
6. değiştirilen dosyalar
7. son test sonuçları
8. bilinen hata/bloker
9. devam edildiğinde ilk yapılacak iş

Kısa, deterministik ve devam ettirilebilir biçimde yaz.
```

Çıktı:

```text
handoff.md
```

olarak saklanır.

---

## %5 seviyesinde

Artık semantik özet oluşturmak için yeni büyük turn başlatılmamalıdır.

Supervisor:

1. Son mevcut turn'ün bitmesini tercih eder.
2. Mekanik checkpoint'i yazar.
3. Yeni work turn başlatmaz.
4. Daha önce oluşturulmuş `handoff.md` varsa korur.
5. Kota reset zamanını kaydeder.

---

## %2 seviyesinde

Emergency mod:

```text
NO NEW MODEL CALL
```

mümkün olduğu kadar uygulanmalıdır.

Supervisor sadece yerel verileri yazar:

```text
git status
git diff --stat
thread id
turn id
quota
scheduler state
timestamps
```

---

# 9. Codex İçinde Çalışma Modeli

En temiz model:

```text
CQS
 │
 ├── codex app-server
 │      │
 │      ├── account/rateLimits/read
 │      ├── thread/start
 │      ├── thread/resume
 │      ├── thread/goal/*
 │      ├── turn/start
 │      ├── turn/interrupt
 │      └── notifications
 │
 ├── Job Engine
 │
 ├── Checkpoint Engine
 │
 ├── Scheduler
 │
 └── State Store
```

CQS, Codex'in yanına eklenen ayrı bir agent değildir.

CQS bir orchestration/control-plane servisidir.

Kod yazan yine Codex'tir.

---

# 10. App-server Bağlantı Akışı

Supervisor:

```bash
codex app-server
```

başlatır.

İletişim stdin/stdout üzerinden newline-delimited JSON mesajlarıyla yapılabilir.

Başlangıç:

```text
initialize
↓
initialized
↓
account/read
↓
account/rateLimits/read
↓
thread/start veya thread/resume
↓
turn/start
```

Bir turn sırasında supervisor şu eventleri dinlemelidir:

```text
turn/started
item/started
item/completed
turn/plan/updated
thread/tokenUsage/updated
account/rateLimits/updated
turn/completed
```

En kritik olanlar:

```text
account/rateLimits/updated
turn/completed
```

olacaktır.

---

# 11. Neden Polling + Event Birlikte Kullanılmalı?

Sadece event'e güvenmek doğru değildir.

Aynı şekilde sürekli polling yapmak da gereksizdir.

Öneri:

```text
account/rateLimits/updated
```

geldiğinde snapshot güncellenir.

Ek olarak:

```text
60 saniyede bir
```

veya her turn sonunda:

```text
account/rateLimits/read
```

ile authoritative snapshot alınır.

Örnek politika:

```yaml
quota_monitor:
  polling_seconds: 60
  refresh_after_turn: true
  stale_after_minutes: 5
```

Rate limit snapshot'ı stale ise otomatik ağır turn başlatılmamalıdır.

---

# 12. Turn Yönetimi

İdeal çalışma:

```text
START TURN
   ↓
turn/started
   ↓
work
   ↓
quota events
   ↓
turn/completed
   ↓
quota refresh
   ↓
policy evaluate
   ↓
next turn / checkpoint / wait
```

En güvenli checkpoint sınırı:

```text
turn/completed
```

sonrasıdır.

Mümkün olduğunca çalışan turn ortasında kesilmemelidir.

---

# 13. Mid-turn Kritik Kota Durumu

Bazı durumlarda kota çalışan turn sırasında hızla düşebilir.

Supervisor üç aşamalı politika uygulamalıdır.

### Seviye 1 — Soft warning

```text
remaining <= 10%
```

Turn devam eder.

Sonraki turn checkpoint olarak planlanır.

---

### Seviye 2 — Hard stop

```text
remaining <= 5%
```

Mevcut turn tehlikeli veya aşırı uzun değilse bitmesine izin verilebilir.

Yeni normal turn başlatılmaz.

---

### Seviye 3 — Emergency

```text
remaining <= 2%
```

Gerekirse aktif `turn_id` biliniyorsa:

```text
turn/interrupt
```

kullanılabilir.

Fakat interrupt varsayılan davranış olmamalıdır.

Çünkü yarım kalan dosya işlemleri veya test akışları olabilir.

Önerilen öncelik:

```text
safe completion
>
checkpoint
>
interrupt
```

---

# 14. State Machine

Ana state machine:

```text
IDLE
 │
 ▼
STARTING
 │
 ▼
RUNNING
 │
 ├──────────────► COMPLETED
 │
 ├──────────────► BLOCKED
 │
 ├──────────────► FAILED
 │
 ▼
WATCH
 │
 ▼
CHECKPOINT_PENDING
 │
 ▼
CHECKPOINTING
 │
 ▼
WAITING_QUOTA
 │
 ▼
RESUME_PENDING
 │
 ▼
RESUMING
 │
 └──────────────► RUNNING
```

Ek manuel durum:

```text
PAUSED
SCHEDULED
CANCELLED
```

---

# 15. Job Durumları

```csharp
public enum JobStatus
{
    Idle,
    Starting,
    Running,
    Watch,
    CheckpointPending,
    Checkpointing,
    WaitingQuota,
    Scheduled,
    ResumePending,
    Resuming,
    Paused,
    Blocked,
    Completed,
    Failed,
    Cancelled
}
```

---

# 16. Reset Zamanı Hesaplama

Her rate-limit penceresi değerlendirilir.

Pseudo-code:

```text
blocking = windows
  where remaining_percent <= hard_stop

if blocking.empty:
    quota_available = true
else:
    quota_available = false
    resume_at = max(blocking.resets_at)
```

Örnek:

```text
5h:
remaining = 0%
reset = 01:30

weekly:
remaining = 0%
reset = Thursday 16:00
```

Resume:

```text
Thursday 16:00
```

olmalıdır.

---

# 17. Grace Period

Reset saniyesinde doğrudan Codex çağrısı yapılmamalıdır.

Örnek:

```yaml
scheduler:
  reset_grace_seconds: 90
```

Resume zamanı:

```text
reset_at + 90 saniye
```

olarak hesaplanabilir.

Daha sonra mutlaka tekrar:

```text
account/rateLimits/read
```

çağrılmalıdır.

Reset zamanı geçmiş olması tek başına yeterli değildir.

---

# 18. Resume Algoritması

```text
scheduler wakes
      ↓
read job state
      ↓
start app-server
      ↓
initialize
      ↓
account/rateLimits/read
      ↓
quota usable?
   /       \
 no         yes
 │           │
recalculate  thread/resume
 │           │
schedule     goal/status check
             │
             ▼
          turn/start
             │
             ▼
          RUNNING
```

---

# 19. Resume Prompt

Thread başarıyla resume edildiğinde gereksiz uzun prompt gönderilmemelidir.

Örnek:

```text
Önceki işi kaldığı noktadan devam ettir.

Aktif goal ve mevcut thread bağlamını koru.
.codex-resume/handoff.md dosyasını kontrol et.
Önce çalışma ağacını ve son test durumunu doğrula.
Daha önce tamamlanan işleri tekrarlama.
İlk olarak handoff içindeki "Next Best Action" adımından devam et.
```

Thread bağlamı yoksa fallback prompt daha ayrıntılı olabilir.

---

# 20. Thread Resume Başarısız Olursa

Fallback zinciri:

```text
1. thread/resume(thread_id)
        ↓ fail
2. persisted thread path varsa dene
        ↓ fail
3. yeni thread oluştur
        ↓
4. handoff.md + state.json yükle
        ↓
5. git state doğrula
        ↓
6. devam et
```

Ama sistem state'i sessizce kaybetmemelidir.

Yeni thread fallback'i log'da açıkça görünmelidir:

```text
RESUME_FALLBACK_NEW_THREAD
```

---

# 21. Git Entegrasyonu

Supervisor kod değişikliğini sahiplenmemeli, fakat çalışma ağacını kaydetmelidir.

Checkpoint sırasında:

```bash
git rev-parse --abbrev-ref HEAD
git rev-parse HEAD
git status --porcelain
git diff --stat
```

kaydedilebilir.

İsteğe bağlı:

```bash
git diff
```

ayrı patch dosyasına alınabilir:

```text
.codex-resume/worktree.patch
```

Ancak supervisor otomatik commit atmamalıdır.

Varsayılan:

```yaml
git:
  auto_commit: false
  save_diff_patch: true
```

---

# 22. Dosya Yapısı

Proje dizini:

```text
project/
│
├── .codex/
│
├── .codex-resume/
│   ├── state.json
│   ├── handoff.md
│   ├── worktree.patch
│   ├── events.ndjson
│   └── logs/
│       └── 2026-10-04.log
│
├── .cqs.yml
│
└── ...
```

Global veri:

Windows:

```text
%USERPROFILE%\.cqs\
```

macOS/Linux:

```text
~/.cqs/
```

İçerik:

```text
~/.cqs/
├── jobs.db
├── scheduler.db
├── logs/
└── config.yml
```

---

# 23. SQLite State Store

SQLite uygun seçimdir.

Tablolar:

```text
jobs
job_runs
quota_snapshots
checkpoints
scheduled_runs
events
```

### jobs

```text
id
name
project_path
thread_id
goal
status
created_at
updated_at
last_turn_id
resume_mode
resume_at
```

### quota_snapshots

```text
id
job_id
limit_id
window_minutes
used_percent
remaining_percent
reset_at
captured_at
```

### checkpoints

```text
id
job_id
type
path
git_head
thread_id
turn_id
created_at
```

---

# 24. CLI Tasarımı

Ana komut:

```bash
cqs
```

## Yeni iş

```bash
cqs run
```

veya:

```bash
cqs run --goal GOAL-06.md
```

---

## Durum

```bash
cqs status
```

Örnek:

```text
Job: vira-goal-06
State: RUNNING

Codex
5h      8.2% remaining   reset 01:32
weekly  42.4% remaining  reset Thu 16:10

Thread
thr_01...

Checkpoint
prepared 22:14

Policy
CHECKPOINT_PREPARE
```

---

## Manuel pause

```bash
cqs pause
```

Akış:

```text
checkpoint
↓
state = PAUSED
↓
Codex work stop
```

---

## Manuel resume

```bash
cqs resume
```

---

## Belirli saatte devam

```bash
cqs resume --at "2026-10-05 08:30"
```

---

## Belirli süre sonra

```bash
cqs resume --in 4h
```

---

## Kota açılınca

```bash
cqs resume --when-quota-available
```

---

## Job listesi

```bash
cqs jobs
```

---

## Job detay

```bash
cqs job vira-goal-06
```

---

## İptal

```bash
cqs cancel vira-goal-06
```

---

# 25. `cqs run --quota-aware`

Tam otomatik mod:

```bash
cqs run --quota-aware
```

Akış:

```text
Codex çalışır
↓
quota izlenir
↓
checkpoint hazırlanır
↓
kritik seviyede yeni iş durur
↓
reset beklenir
↓
quota yeniden kontrol edilir
↓
thread resume edilir
↓
iş devam eder
↓
goal tamamlanana kadar sürer
```

Bu ürünün ana kullanım şekli bu olmalıdır.

---

# 26. Codex Goal ile Entegrasyon

Bir job başlatıldığında supervisor:

```text
thread/goal/get
```

ile goal olup olmadığını kontrol eder.

Goal verilmişse:

```text
thread/goal/set
```

kullanılabilir.

Goal state supervisor DB'sine de yazılmalıdır.

Neden iki yerde?

```text
Codex Goal
+
Supervisor Job Goal
```

çünkü Codex state bozulsa bile supervisor amacı kaybetmemelidir.

---

# 27. Codex İçin Proje Talimatı

Projeye aşağıdaki gibi bir talimat eklenebilir:

```markdown
## CQS / Quota-aware çalışma

Bu proje Codex Quota Supervisor altında çalıştırılabilir.

`.codex-resume/` dizini çalışma devamlılığı içindir.

Eğer supervisor checkpoint isterse:
- yeni kapsam başlatma,
- yapılan işleri özetle,
- mevcut işi belirt,
- test durumunu kaydet,
- açık işleri listele,
- devam edildiğinde ilk yapılacak adımı açıkça yaz.

Resume sırasında:
- `.codex-resume/handoff.md` dosyasını oku,
- git çalışma ağacını doğrula,
- tamamlanmış işi tekrarlama,
- "Next Best Action" noktasından devam et.

Checkpoint sırasında kapsam dışı refactor yapma.
```

Bu talimat `AGENTS.md` içine veya ayrı supervisor instruction olarak eklenebilir.

---

# 28. Scheduler Tasarımı

Platform bağımsız olması için ana scheduler uygulamanın içinde bulunmalıdır.

Yanlış yaklaşım:

```text
Windows Task Scheduler'a tamamen bağımlı ol
```

veya:

```text
cron'a tamamen bağımlı ol
```

Doğru yaklaşım:

```text
CQS internal scheduler
```

Platform scheduler'ları yalnızca bootstrap için kullanılmalıdır.

---

# 29. Makine Yeniden Başlarsa

CQS servis/daemon modunda çalışabilir:

```bash
cqs daemon
```

Başlangıçta:

```text
SQLite oku
↓
WAITING_QUOTA / SCHEDULED job'ları bul
↓
resume_at kontrol et
↓
gerekirse scheduler'a yeniden yükle
```

İş kaybolmamalıdır.

---

# 30. Platform Desteği

Hedef:

```text
Windows
macOS
Linux
```

Teknoloji önerisi:

```text
.NET 10
Worker/Console application
SQLite
System.Text.Json
Process API
```

Neden .NET:

- cross-platform,
- tek executable publish,
- Windows service desteği,
- systemd entegrasyonu,
- macOS launchd ile çalıştırılabilir,
- sağlam process yönetimi,
- SQLite desteği,
- uzun ömürlü worker için uygun.

---

# 31. Proje Mimarisi

```text
CodexQuotaSupervisor.sln

src/
├── Cqs.Cli
├── Cqs.Core
├── Cqs.Codex
├── Cqs.Scheduler
├── Cqs.Storage
├── Cqs.Git
└── Cqs.Host

tests/
├── Cqs.Core.Tests
├── Cqs.Codex.Tests
├── Cqs.Scheduler.Tests
└── Cqs.IntegrationTests
```

---

# 32. Modüller

## Cqs.Cli

Komutlar:

```text
run
status
pause
resume
schedule
jobs
cancel
daemon
doctor
```

---

## Cqs.Codex

Sorumluluk:

```text
app-server process
JSON-RPC
initialization
account
rate limits
thread lifecycle
goal lifecycle
turn lifecycle
notifications
```

---

## Cqs.Core

```text
JobEngine
QuotaPolicy
CheckpointPolicy
ResumePolicy
StateMachine
```

---

## Cqs.Scheduler

```text
scheduled jobs
quota reset wakeups
retry/backoff
clock abstraction
```

---

## Cqs.Storage

```text
SQLite repositories
state serialization
migration
```

---

## Cqs.Git

```text
branch
HEAD
status
diff
workspace integrity
```

---

# 33. QuotaPolicy

Örnek:

```csharp
public sealed record QuotaPolicy(
    double PrepareCheckpointAtRemainingPercent = 10,
    double StopNewWorkAtRemainingPercent = 5,
    double EmergencyAtRemainingPercent = 2,
    TimeSpan ResetGracePeriod = default
);
```

Policy evaluator:

```text
if remaining <= 2
    Emergency

else if remaining <= 5
    StopNewWork

else if remaining <= 10
    PrepareCheckpoint

else if remaining <= 15
    Watch

else
    Normal
```

Birden fazla pencere varsa en kötü durum esas alınır.

---

# 34. ResumePolicy

Resume için üç koşul birlikte sağlanmalıdır:

```text
1. schedule zamanı gelmiş olmalı
2. blocking quota kalmamış olmalı
3. job resume edilebilir durumda olmalı
```

Pseudo-code:

```text
CanResume(job):

    if job.status not in
       [WaitingQuota, Scheduled, ResumePending]:
       return false

    quota = ReadQuota()

    if quota.hasBlockingWindow:
       Reschedule(max(resetTimes) + grace)
       return false

    return true
```

---

# 35. Retry Politikası

Reset sonrası Codex hemen açılmayabilir veya network hatası olabilir.

Exponential backoff:

```text
30 sec
1 min
2 min
5 min
10 min
```

Maksimum:

```text
30 min
```

Ancak rate limit hâlâ blokluyorsa backoff yerine yeni reset zamanı kullanılmalıdır.

---

# 36. Güvenlik

Tam otomatik resume şu anlama gelmemelidir:

```text
Codex'e sınırsız makine yetkisi ver
```

Varsayılan sandbox:

```text
workspace write
```

olmalıdır.

Network:

```text
configurable
```

olmalıdır.

Destructive işlemler kullanıcı onayı gerektirmeye devam etmelidir.

Önerilmeyen varsayılan:

```bash
--yolo
```

Supervisor gözetimsiz çalışacağı için bu özellikle risklidir.

---

# 37. Approval Bekleyen İşler

Codex bir kullanıcı approval'ında kalmışsa supervisor bunu quota waiting ile karıştırmamalıdır.

Job:

```text
BLOCKED_APPROVAL
```

durumuna geçmelidir.

Bu durumda otomatik resume edilmemelidir.

---

# 38. Kullanıcı Girdisi Gereken Durum

Codex şu tip bir blokere gelirse:

```text
API key gerekli
ürün kararı gerekli
geri döndürülemez migration kararı gerekli
external authentication gerekli
```

job:

```text
BLOCKED_USER
```

olmalıdır.

Quota reset sonrası otomatik devam etmek doğru değildir.

---

# 39. Hata Sınıfları

```text
QuotaBlocked
NetworkFailure
CodexServerFailure
ThreadResumeFailure
WorkspaceMissing
GitStateChanged
ApprovalRequired
UserInputRequired
ProcessCrash
StateCorruption
```

Her hata retry edilmemelidir.

---

# 40. Workspace Değişmişse

Resume öncesi:

```text
stored git HEAD
stored branch
current git HEAD
current branch
```

karşılaştırılmalıdır.

Başka biri proje üzerinde değişiklik yaptıysa:

```text
WORKSPACE_CHANGED
```

durumu oluşturulmalıdır.

Supervisor kör biçimde eski handoff'tan devam etmemelidir.

---

# 41. Handoff Geçerlilik Kontrolü

Resume sırasında:

```text
handoff git HEAD == current git HEAD
```

değilse:

1. mevcut workspace yeniden analiz edilir,
2. handoff yalnızca referans kabul edilir,
3. eski dosya varsayımlarına kör güvenilmez.

---

# 42. Event Log

Tüm state geçişleri NDJSON olarak kaydedilebilir.

Örnek:

```json
{"ts":"2026-10-04T22:10:00+03:00","event":"quota.updated","remaining":9.8}
{"ts":"2026-10-04T22:11:02+03:00","event":"checkpoint.requested"}
{"ts":"2026-10-04T22:12:40+03:00","event":"checkpoint.created"}
{"ts":"2026-10-04T22:15:22+03:00","event":"quota.hard_stop","remaining":4.8}
{"ts":"2026-10-04T22:15:25+03:00","event":"job.waiting_quota"}
```

Bu log debug için kritik olacaktır.

---

# 43. Manuel Scheduling

Quota'dan bağımsız zamanlama da aynı engine'i kullanmalıdır.

Örnek:

```bash
cqs schedule --at "tomorrow 07:00" \
  --goal GOAL-07.md
```

veya:

```bash
cqs pause --resume-at "2026-10-05 09:00"
```

Bu durumda job:

```text
SCHEDULED
```

olur.

Saat geldiğinde quota tekrar kontrol edilir.

Quota uygun değilse çalışma başlamaz.

---

# 44. Kullanıcının Verdiği Komutu İleri Zamana Taşıma

Örnek:

```bash
cqs run-later \
  --at "2026-10-05 02:00" \
  -- "GOAL-06 işine devam et ve kalan testleri tamamla"
```

State:

```json
{
  "command": "GOAL-06 işine devam et ve kalan testleri tamamla",
  "notBefore": "2026-10-05T02:00:00+03:00"
}
```

Scheduler zamanı geldiğinde:

```text
quota check
↓
thread resume
↓
turn start(command)
```

yapar.

---

# 45. Codex İçinden Kullanım

İleride Codex skill/command eklenebilir.

Örneğin:

```text
/checkpoint
```

mantıksal olarak:

```bash
cqs checkpoint
```

çağırabilir.

Benzer şekilde:

```text
/schedule-resume 02:00
```

CQS'ye job planlatabilir.

Ancak ilk sürümde bu entegrasyon şart değildir.

İlk sürümün kontrol düzlemi CLI olmalıdır.

---

# 46. Health Check

```bash
cqs doctor
```

şunları kontrol etmeli:

```text
Codex kurulu mu?
Codex version?
app-server açılıyor mu?
auth var mı?
account/read çalışıyor mu?
rateLimits/read çalışıyor mu?
SQLite yazılabilir mi?
project path erişilebilir mi?
Git var mı?
scheduler sağlıklı mı?
```

Örnek:

```text
Codex CLI             OK
App Server            OK
Authentication        OK
Rate Limits API       OK
SQLite                OK
Git                   OK
Scheduler             OK

CQS ready.
```

---

# 47. MVP

İlk sürümde gereksiz özellik eklenmemeli.

## MVP-1

Mutlaka:

```text
Codex app-server bağlantısı
account/rateLimits/read
thread/start
thread/resume
turn/start
turn/completed
SQLite job state
mekanik checkpoint
handoff.md
quota policy
reset scheduler
automatic resume
CLI
Windows/macOS/Linux
```

---

# 48. MVP Dışında Bırakılacaklar

İlk sürümde:

```text
GUI
cloud sync
multi-user
web dashboard
mobile app
existing Desktop session adoption
distributed workers
team quota analytics
automatic git commit
automatic PR creation
```

olmamalıdır.

Bunlar çekirdek problemi çözmez.

---

# 49. Phase 2

Daha sonra:

```text
Codex native Goal derin entegrasyonu
existing thread adoption
system tray
job dashboard
notifications
GitHub entegrasyonu
multiple concurrent jobs
quota-aware job priority
```

eklenebilir.

---

# 50. Birden Fazla Job

İlk sürümde aynı Codex hesabında paralel işler risklidir.

Çünkü hepsi aynı quota havuzunu tüketebilir.

Bu yüzden MVP:

```text
1 active quota-consuming job
```

kuralıyla başlamalıdır.

Bekleyen job'lar queue'da olabilir.

---

# 51. Job Priority

Phase 2:

```text
P0
P1
P2
P3
```

Quota azsa:

```text
P0 devam
P3 bekle
```

gibi policy uygulanabilir.

Ama MVP için gereksizdir.

---

# 52. Örnek Uçtan Uca Senaryo

Başlangıç:

```bash
cd D:\Projects\ViraTYS

cqs run --goal docs/goals/GOAL-06.md
```

CQS:

```text
Job created: vira-goal-06
Thread: thr_82...
5h remaining: 41%
weekly remaining: 62%
State: RUNNING
```

Codex çalışır.

Bir süre sonra:

```text
5h remaining: 9.4%
```

CQS:

```text
State: CHECKPOINT_PENDING
```

Codex bir turn bitirir.

Supervisor checkpoint turn'ü başlatır.

```text
handoff.md created
```

Daha sonra:

```text
5h remaining: 4.6%
```

CQS:

```text
State: STOP_NEW_WORK
```

Mevcut turn biter.

Mekanik state yazılır.

```text
5h reset: 01:32
weekly remaining: 58%
```

CQS:

```text
State: WAITING_QUOTA
Resume scheduled: 01:34
```

01:34:

```text
account/rateLimits/read
```

çalışır.

Quota uygun:

```text
5h remaining: 100%
```

CQS:

```text
thread/resume thr_82...
```

sonra:

```text
turn/start
```

Resume prompt:

```text
Önceki goal'a kaldığın noktadan devam et.
handoff.md ve mevcut workspace'i doğrula.
Next Best Action adımından ilerle.
```

Job:

```text
State: RUNNING
```

Codex GOAL-06 tamamlanana kadar devam eder.

---

# 53. Kabul Kriterleri

Sistem başarılı sayılabilmesi için:

### Quota

- 5 saatlik pencere algılanabilmeli.
- haftalık pencere algılanabilmeli.
- remaining percent hesaplanabilmeli.
- reset timestamp saklanabilmeli.
- birden fazla blocking pencere doğru ele alınmalı.

### Checkpoint

- thread ID kaydedilmeli.
- aktif goal kaydedilmeli.
- Git state kaydedilmeli.
- mekanik checkpoint LLM çağrısı olmadan üretilebilmeli.
- semantik handoff desteklenmeli.

### Scheduler

- reset zamanı sonrası uyanmalı.
- app restart sonrası schedule kaybolmamalı.
- zamanı gelince quota yeniden kontrol edilmeli.

### Resume

- aynı thread ID ile resume denenmeli.
- başarısızsa kontrollü fallback uygulanmalı.
- workspace değişikliği kontrol edilmeli.
- aynı iş gereksiz yere baştan yapılmamalı.

### Cross-platform

Test:

```text
Windows 11
macOS
Linux
```

---

# 54. En Önemli Mimari İlkeler

## 1. Reset etmeye çalışma

Sunucu kotasını manipüle etmeye çalışma.

```text
observe → wait → resume
```

---

## 2. UI scraping kullanma

Mümkün olduğunda:

```text
account/rateLimits/read
```

kullan.

CLI ekranındaki yüzdeyi parse etmeye bağımlı olma.

---

## 3. Thread ID'yi ana state olarak sakla

Resume başarısının temelidir.

---

## 4. Checkpoint'i kota bitmeden hazırla

```text
%10 prepare
%5 stop
```

---

## 5. Checkpoint yalnızca LLM özeti olmasın

Her zaman mekanik state de bulunsun.

---

## 6. Scheduler reset zamanına kör güvenmesin

Uyandıktan sonra quota'yı tekrar doğrulasın.

---

## 7. Supervisor kod yazmasın

Kodlama Codex'in işi.

Supervisor:

```text
control plane
```

olmalı.

---

## 8. Güvenlik yetkileri minimum olsun

Gözetimsiz resume:

```text
minimum necessary permissions
```

ile çalışmalı.

---

# 55. Önerilen İlk Geliştirme Sırası

## GOAL-01 — Codex App Server Adapter

- process başlat
- initialize
- JSON-RPC
- account/read
- rateLimits/read
- event reader

## GOAL-02 — Job State

- SQLite
- jobs
- state machine
- event log

## GOAL-03 — Thread Lifecycle

- thread/start
- turn/start
- completion
- thread/resume

## GOAL-04 — Quota Engine

- window detection
- remaining calculation
- threshold policy
- reset calculation

## GOAL-05 — Checkpoint Engine

- mechanical checkpoint
- Git snapshot
- semantic handoff

## GOAL-06 — Scheduler

- waiting
- resume_at
- restart recovery
- grace period

## GOAL-07 — Automatic Resume

- quota recheck
- thread resume
- continuation prompt
- fallback

## GOAL-08 — Cross-platform Packaging

- Windows
- macOS
- Linux
- daemon/service bootstrap

---

# 56. Sonuç

Geliştirilecek sistemin özü:

```text
CODING AGENT
     │
     ▼
CODEX
     │
     ▼
CQS SUPERVISOR
 ├─ quota monitor
 ├─ job state
 ├─ checkpoint
 ├─ scheduler
 └─ resume engine
```

Doğru davranış:

```text
Çalış
→ kalan kotayı izle
→ kota düşerken checkpoint hazırla
→ kritik seviyede yeni işi durdur
→ state'i kalıcılaştır
→ gerçek reset zamanını bekle
→ kotayı yeniden kontrol et
→ aynı thread'i resume et
→ handoff'tan devam et
```

Bu tasarım "limiti aşmaya" çalışmaz.

Codex'in mevcut kullanım modeliyle uyumlu biçimde, uzun süreli geliştirme işlerinin quota nedeniyle kopmasını önleyen bir orchestration katmanı oluşturur.

---

# 57. Güncel Teknik Kaynaklar

Bu dokümandaki Codex entegrasyon kararları 4 Ekim 2026 itibarıyla aşağıdaki kaynaklara göre hazırlanmıştır.

### OpenAI — Codex usage limits

https://help.openai.com/tr-tr/articles/20001516-managing-usage-with-gpt-6-astra-in-work-and-codex

Önemli davranış:

- plana göre 5 saatlik ve haftalık limit uygulanabilir,
- ikisi de uygulanıyorsa her ikisinde de kullanım hakkı olması gerekir.

### OpenAI Codex repository — Rate limit protocol

https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/src/protocol/v2/account.rs

https://github.com/openai/codex/blob/main/codex-rs/protocol/src/protocol.rs

Kullanılan alanlar:

```text
used_percent
window_minutes
resets_at
```

### OpenAI Codex repository — Thread resume

https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/src/protocol/v2/thread.rs

`thread/resume` mevcut thread'in ID üzerinden devam ettirilmesini destekler.

### OpenAI Developers — Codex app-server

https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server

Temel akış:

```text
initialize
initialized
thread/start
turn/start
turn/completed
thread/resume
```

### OpenAI Developers — Using Goals in Codex

https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex

Interactive Goal yaşam döngüsü:

```text
/goal
/goal pause
/goal resume
/goal clear
```

---

## Not

Codex protokolü aktif olarak geliştirilmektedir.

Bu nedenle implementation sırasında app-server'ın mevcut JSON schema/protocol tipleri source of truth kabul edilmeli; method veya payload yapıları uygulama içine gereksiz biçimde hard-code edilmemelidir.
