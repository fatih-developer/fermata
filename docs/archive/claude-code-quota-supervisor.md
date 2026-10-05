# Claude Code Quota Supervisor
## Kota Bilinçli Checkpoint, Otomatik Devam ve İleri Zamanlama Sistemi

**Doküman türü:** Ürün + teknik tasarım  
**Tarih:** 4 Ekim 2026  
**Kapsam:** Claude Code  
**Durum:** Uygulanabilir taslak

---

# 1. Amaç

Claude Code ile uzun süreli geliştirme işleri yürütülürken 5 saatlik ve haftalık kullanım limitleri nedeniyle oturum yarıda kalabilir.

Bu sistemin amacı limiti aşmak veya resetlemek değildir.

Amaç:

> Claude Code kullanım kotası kritik seviyeye düşmeden mevcut işi güvenli bir checkpoint'e almak, session bilgisini ve proje durumunu kalıcılaştırmak, reset zamanını beklemek ve kullanım hakkı yeniden açıldığında aynı session üzerinden işe otomatik devam etmek.

Sistem ayrıca:

- manuel pause,
- belirli saatte resume,
- belirli süre sonra resume,
- quota açılınca resume,
- terminal kapandıktan sonra resume,
- bilgisayar yeniden başladıktan sonra resume,
- headless çalışma

senaryolarını desteklemelidir.

---

# 2. Temel Ürün Tanımı

Bu uygulama bir "Claude limit reset aracı" değildir.

Doğru tanım:

> **Claude Code Session Supervisor**

Ana görevleri:

1. Claude Code kota durumunu izlemek.
2. Aktif session kimliğini takip etmek.
3. Kalan kullanım kritik seviyeye geldiğinde checkpoint üretmek.
4. Yeni büyük işi durdurmak.
5. Session ve Git state'i kaydetmek.
6. Reset zamanını saklamak.
7. Reset sonrası quota durumunu tekrar doğrulamak.
8. Aynı Claude Code session'ını resume etmek.
9. Handoff dokümanından kaldığı noktadan devam etmek.
10. Manuel ileri zamanlanmış işleri başlatmak.

---

# 3. Claude Code'un Kullanılacak Native Özellikleri

Claude Code tarafında bazı özellikler zaten yerleşiktir.

Bu nedenle sistem bunları tekrar geliştirmek yerine kullanmalıdır.

---

## 3.1 Rate Limit Bilgisi

Claude Code custom status line mekanizmasına makine-okunabilir JSON gönderir.

Bu JSON içinde kullanılabilecek alanlar:

```json
{
  "session_id": "...",
  "transcript_path": "...",
  "rate_limits": {
    "five_hour": {
      "used_percentage": 23.5,
      "resets_at": 1738425600
    },
    "seven_day": {
      "used_percentage": 41.2,
      "resets_at": 1738857600
    }
  }
}
```

Supervisor şu hesabı yapar:

```text
remaining = 100 - used_percentage
```

Örnek:

```text
5h used: 91%
5h remaining: 9%

7d used: 64%
7d remaining: 36%
```

Bu yöntem UI scraping kullanımından daha güvenlidir.

---

# 4. Session Resume

Claude Code session'ları doğrudan resume edilebilir.

Temel kullanım:

```bash
claude --resume <session-id>
```

Alternatifler:

```bash
claude --continue
claude --resume <name>
claude --resume <transcript-path>
```

Supervisor açısından en önemli veri:

```text
session_id
```

olmalıdır.

Bu bilgi state store içinde kalıcı saklanmalıdır.

---

# 5. Headless Resume

Headless çalışma için:

```bash
claude -p "..." --output-format json
```

kullanılabilir.

Çıktıdan:

```json
{
  "session_id": "..."
}
```

alınır.

Sonrasında:

```bash
claude -p \
  --resume "$SESSION_ID" \
  "Önceki işe devam et"
```

ile session sürdürülebilir.

Kesilmiş bir turn varsa:

```bash
CLAUDE_CODE_RESUME_INTERRUPTED_TURN=1
```

ortam değişkeni kullanılabilir.

---

# 6. Native Auto-Resume

Claude Code interactive kullanımda limit dolduğunda native olarak bekleyip reset sonrasında devam edebilir.

Örnek davranış:

```text
Usage limit reached
Continuing automatically at 15:45
```

Bu özellik varsa supervisor bunu devre dışı bırakmamalıdır.

Doğru yaklaşım:

```text
Native auto-resume çalışabiliyorsa:
    Claude Code'a bırak

Native auto-resume mümkün değilse:
    Supervisor devreye gir
```

---

# 7. Supervisor'ın Gerekli Olduğu Durumlar

Claude Code native auto-resume tüm problemleri çözmez.

Supervisor şu durumları yönetmelidir:

```text
terminal kapandı
Claude Code process kapandı
bilgisayar restart oldu
bilgisayar uzun süre sleep'te kaldı
haftalık reset çok ileride
headless çalışma kullanılıyor
native auto-resume kapalı
native auto-resume başarısız
manuel ileri zamanlama istendi
session ileride belirli saatte açılacak
```

---

# 8. Threshold Politikası

Önerilen varsayılan değerler:

```yaml
quota:
  watch_remaining: 15
  checkpoint_remaining: 10
  hard_stop_remaining: 5
  emergency_remaining: 2
```

Durumlar:

| Kalan kota | Durum |
|---|---|
| > %15 | NORMAL |
| <= %15 | WATCH |
| <= %10 | CHECKPOINT_PREPARE |
| <= %5 | STOP_NEW_WORK |
| <= %2 | EMERGENCY |
| %0 | WAITING_QUOTA |

---

# 9. Neden %5 Beklenmemeli?

Claude Code turn maliyeti sabit değildir.

Tek bir ağır çalışma:

```text
remaining 8%
→
remaining 0%
```

şeklinde bitebilir.

Bu nedenle semantic checkpoint:

```text
%10 civarında
```

hazırlanmalıdır.

---

# 10. Checkpoint Yapısı

Checkpoint iki parçadan oluşmalıdır.

---

## 10.1 Mekanik Checkpoint

LLM çağrısı gerektirmez.

Dosya:

```text
.claude-resume/state.json
```

İçerik:

```text
job_id
session_id
transcript_path
cwd
git branch
git HEAD
git status
changed files
quota
reset times
job state
resume policy
```

Örnek:

```json
{
  "jobId": "vira-goal-06",
  "status": "waiting_quota",

  "sessionId": "550e8400-e29b-41d4-a716-446655440000",

  "transcriptPath": "...",

  "cwd": "D:/Projects/ViraTYS",

  "git": {
    "branch": "feature/goal-06",
    "head": "31a4fc0",
    "dirty": true
  },

  "quota": {
    "fiveHourUsed": 97.8,
    "fiveHourResetAt": "2026-10-05T01:32:00+03:00",

    "sevenDayUsed": 74.4,
    "sevenDayResetAt": "2026-10-08T16:10:00+03:00"
  },

  "resume": {
    "mode": "quota",
    "notBefore": "2026-10-05T01:34:00+03:00"
  }
}
```

---

# 11. Semantic Handoff

Dosya:

```text
.claude-resume/handoff.md
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
Failing Playwright reconciliation testini çalıştır.

## Do Not
- kapsam dışı refactor yapma
- veri modelini yeniden tasarlama
```

---

# 12. Checkpoint Prompt

Quota %10 seviyesine geldiğinde Claude'a:

```text
Kalan kullanım kotası kritik seviyeye yaklaşıyor.

Yeni geliştirme başlatma.

Mevcut çalışmanın durumunu checkpoint için özetle.

Şunları yaz:
1. aktif hedef
2. tamamlanan işler
3. devam eden iş
4. açık işler
5. önemli teknik kararlar
6. değiştirilen dosyalar
7. test sonuçları
8. blocker
9. devam edildiğinde ilk yapılacak iş

Çıktıyı:
.claude-resume/handoff.md
dosyasına yaz.
```

verilebilir.

---

# 13. %5 Seviyesinde Davranış

```text
remaining <= 5%
```

olduğunda yeni kapsam başlatılmamalıdır.

Supervisor:

1. mevcut turn bitmesini bekler,
2. yeni büyük prompt göndermez,
3. mekanik checkpoint oluşturur,
4. handoff varsa korur,
5. reset zamanını kaydeder,
6. job'ı WAITING_QUOTA durumuna alır.

---

# 14. %2 Seviyesinde Davranış

Emergency mod:

```text
NO NEW MODEL WORK
```

uygulanmalıdır.

Yalnızca lokal bilgiler saklanmalıdır:

```text
session_id
transcript_path
git status
git HEAD
quota
scheduler state
timestamps
```

---

# 15. Claude Code Hooks Entegrasyonu

Claude Code hooks sistemi supervisor için önemli avantaj sağlar.

Kullanılabilecek eventler:

```text
SessionStart
UserPromptSubmit
PreToolUse
PostToolUse
PostToolBatch
Stop
StopFailure
PreCompact
PostCompact
SessionEnd
Notification
```

En önemli olanlar:

```text
SessionStart
Stop
StopFailure
SessionEnd
Notification
```

---

# 16. SessionStart

Claude session başladığında:

```text
session_id
cwd
transcript_path
```

state'e yazılır.

Job daha önce varsa:

```text
existing job
→
session eşleştir
```

yapılabilir.

---

# 17. Stop Hook

Turn tamamlandığında:

```text
quota snapshot kontrol et
```

yapılır.

Eğer:

```text
remaining <= 10%
```

ise:

```text
checkpoint required
```

işaretlenebilir.

Bu nokta checkpoint için güvenli sınırdır.

---

# 18. Notification Hook

Claude Code quota auto-resume eventleri hook üzerinden izlenebilir.

Örnek event tipleri:

```text
quota_auto_resume_fired
quota_auto_resume_stale
quota_auto_resume_disabled
```

Supervisor bunları kendi state machine'i ile eşleştirebilir.

Örneğin:

```text
quota_auto_resume_fired

WAITING_QUOTA
→
RUNNING
```

---

# 19. StatusLine Entegrasyonu

Claude ayarına:

```json
{
  "statusLine": {
    "type": "command",
    "command": "claude-supervisor statusline",
    "refreshInterval": 30
  }
}
```

eklenebilir.

StatusLine scripti stdin'den JSON alır.

Supervisor:

```text
session_id
transcript_path
rate_limits
cwd
```

alanlarını çıkarır.

---

# 20. Quota Monitoring

Önerilen politika:

```yaml
quota_monitor:
  refresh_seconds: 30
  stale_after_seconds: 120
```

Status line update geldiğinde snapshot güncellenir.

Ayrıca session eventlerinde state güncellenebilir.

---

# 21. Reset Zamanı Hesaplama

Birden fazla limit blokluyorsa gerçek resume zamanı:

```text
max(blocking_reset_times)
```

olmalıdır.

Örnek:

```text
5h remaining: 0%
reset: 01:30

7d remaining: 0%
reset: Thursday 16:00
```

Resume:

```text
Thursday 16:00
```

olmalıdır.

---

# 22. Grace Period

Reset timestamp ile tam aynı anda session başlatılmamalıdır.

Öneri:

```yaml
scheduler:
  grace_seconds: 90
```

Resume:

```text
reset_at + 90 saniye
```

olarak planlanabilir.

---

# 23. Resume Algoritması

```text
scheduler wake
      ↓
job state oku
      ↓
quota yeniden doğrula
      ↓
quota available?
   /          \
 hayır         evet
  │             │
reschedule     session resume
                │
                ▼
          claude --resume
                │
                ▼
             RUNNING
```

---

# 24. Resume Komutu

Interactive:

```bash
claude --resume "$SESSION_ID"
```

İlk prompt:

```text
Önceki işe kaldığın noktadan devam et.

.claude-resume/handoff.md dosyasını kontrol et.

Önce mevcut Git çalışma ağacını ve test durumunu doğrula.

Tamamlanmış işleri tekrar yapma.

Next Best Action adımından ilerle.
```

---

# 25. Headless Resume

```bash
claude -p \
  --resume "$SESSION_ID" \
  "Önceki işe kaldığın noktadan devam et. .claude-resume/handoff.md dosyasını kontrol et." \
  --output-format stream-json
```

Kesilmiş turn varsa:

```bash
CLAUDE_CODE_RESUME_INTERRUPTED_TURN=1
```

kullanılabilir.

---

# 26. Native Auto-Resume ile Çakışma Yönetimi

Supervisor native Claude auto-resume ile yarışmamalıdır.

Policy:

```text
Claude process aktif
+
native auto-resume bekliyor
→
Supervisor yeni process başlatmaz
```

Supervisor sadece eventleri gözlemler.

Native resume başarısız veya stale olursa:

```text
fallback resume
```

başlatılır.

---

# 27. Process Detection

Supervisor aktif Claude Code process olup olmadığını takip etmelidir.

State:

```text
process_id
session_id
started_at
last_seen
```

ile tutulabilir.

Ama yalnızca PID'ye güvenilmemelidir.

Ana kimlik:

```text
session_id
```

olmalıdır.

---

# 28. Terminal Kapanırsa

Terminal kapandıysa:

```text
process gone
+
job unfinished
+
session_id exists
```

durumunda job:

```text
RESUME_PENDING
```

olabilir.

Resume zamanı geldiğinde yeni Claude process başlatılır.

---

# 29. Bilgisayar Restart Olursa

Supervisor daemon/worker başlangıçta SQLite'ı okur.

```text
WAITING_QUOTA
SCHEDULED
RESUME_PENDING
```

job'larını bulur.

Sonra scheduler yeniden oluşturulur.

---

# 30. Manuel Pause

CLI:

```bash
claude-supervisor pause
```

Akış:

```text
semantic checkpoint
↓
mechanical checkpoint
↓
state = PAUSED
```

---

# 31. Manuel Resume

```bash
claude-supervisor resume
```

---

# 32. Belirli Saatte Resume

```bash
claude-supervisor resume --at "2026-10-05 08:30"
```

---

# 33. Belirli Süre Sonra Resume

```bash
claude-supervisor resume --in 3h
```

---

# 34. Quota Açılınca Resume

```bash
claude-supervisor resume --when-quota-available
```

---

# 35. İleri Zamanda Komut Çalıştırma

```bash
claude-supervisor run-later \
  --at "2026-10-05 07:00" \
  -- "GOAL-06 işine devam et ve kalan testleri tamamla"
```

---

# 36. Claude Code Routines ile Fark

Claude Code future scheduling için kendi routine sistemi sunabilir.

Ancak routine:

```text
mevcut local session'ı resume etmek
```

ile aynı şey değildir.

Routine yeni cloud session oluşturabilir.

Bizim kullanımımız:

```text
aynı proje
+
aynı session
+
aynı context
+
aynı Git worktree
```

olduğu için supervisor gereklidir.

---

# 37. Git State

Checkpoint sırasında:

```bash
git rev-parse --abbrev-ref HEAD
git rev-parse HEAD
git status --porcelain
git diff --stat
```

saklanmalıdır.

İsteğe bağlı:

```bash
git diff > .claude-resume/worktree.patch
```

---

# 38. Workspace Değişikliği

Resume öncesi:

```text
stored HEAD
stored branch
current HEAD
current branch
```

karşılaştırılır.

Değişiklik varsa:

```text
WORKSPACE_CHANGED
```

durumu oluşturulmalıdır.

Supervisor kör biçimde eski handoff üzerinden devam etmemelidir.

---

# 39. Dosya Yapısı

```text
project/
│
├── .claude/
│
├── .claude-resume/
│   ├── state.json
│   ├── handoff.md
│   ├── worktree.patch
│   ├── events.ndjson
│   └── logs/
│
└── ...
```

Global state:

Windows:

```text
%USERPROFILE%\.claude-supervisor\
```

macOS/Linux:

```text
~/.claude-supervisor/
```

---

# 40. SQLite Yapısı

Tablolar:

```text
jobs
sessions
quota_snapshots
checkpoints
scheduled_runs
events
```

---

# 41. jobs

```text
id
name
project_path
session_id
transcript_path
status
goal
created_at
updated_at
resume_mode
resume_at
```

---

# 42. quota_snapshots

```text
id
job_id
window_type
used_percent
remaining_percent
reset_at
captured_at
```

---

# 43. State Machine

```text
IDLE
 │
 ▼
STARTING
 │
 ▼
RUNNING
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
 └────► RUNNING
```

Ek durumlar:

```text
PAUSED
SCHEDULED
BLOCKED
FAILED
COMPLETED
CANCELLED
```

---

# 44. Job Status

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

# 45. Teknoloji Önerisi

Cross-platform uygulama için:

```text
.NET 10
Console / Worker
SQLite
System.Text.Json
Process API
```

uygun seçimdir.

Destek:

```text
Windows
macOS
Linux
```

---

# 46. Mimari

```text
ClaudeQuotaSupervisor.sln

src/
├── Supervisor.Cli
├── Supervisor.Core
├── Supervisor.Claude
├── Supervisor.Scheduler
├── Supervisor.Storage
├── Supervisor.Git
└── Supervisor.Host

tests/
├── Supervisor.Core.Tests
├── Supervisor.Claude.Tests
├── Supervisor.Scheduler.Tests
└── Supervisor.IntegrationTests
```

---

# 47. Claude Adapter

Sorumluluklar:

```text
statusLine parser
hook events
session ID handling
transcript handling
process execution
claude --resume
headless resume
native auto-resume state
```

---

# 48. Core

```text
JobEngine
QuotaPolicy
CheckpointPolicy
ResumePolicy
StateMachine
```

---

# 49. Scheduler

```text
reset wakeups
manual schedules
restart recovery
retry/backoff
```

---

# 50. Güvenlik

Supervisor otomatik resume yaptığında Claude Code'a gereksiz geniş yetki verilmemelidir.

Özellikle:

```text
--dangerously-skip-permissions
```

varsayılan olmamalıdır.

Gözetimsiz çalışma:

```text
minimum required permissions
```

ile yapılmalıdır.

---

# 51. Kullanıcı Girdisi Gerektiren Durumlar

Şu durumlarda otomatik devam durmalıdır:

```text
API key gerekli
credential gerekli
destructive migration approval gerekli
ürün kararı gerekli
external login gerekli
geri döndürülemez işlem gerekli
```

State:

```text
BLOCKED_USER
```

olmalıdır.

---

# 52. Approval Bekleyen Durum

Claude Code permission approval bekliyorsa:

```text
BLOCKED_APPROVAL
```

durumu kullanılmalıdır.

Quota reset geldi diye otomatik devam edilmemelidir.

---

# 53. Event Log

```json
{"ts":"2026-10-04T22:10:00+03:00","event":"quota.updated","remaining":9.8}
{"ts":"2026-10-04T22:11:02+03:00","event":"checkpoint.requested"}
{"ts":"2026-10-04T22:12:40+03:00","event":"checkpoint.created"}
{"ts":"2026-10-04T22:15:22+03:00","event":"quota.hard_stop","remaining":4.8}
{"ts":"2026-10-04T22:15:25+03:00","event":"job.waiting_quota"}
```

---

# 54. Retry Politikası

Resume başarısız olursa:

```text
30 sec
1 min
2 min
5 min
10 min
```

backoff uygulanabilir.

Rate limit hâlâ blokluyorsa backoff yerine yeni reset zamanı kullanılmalıdır.

---

# 55. Doctor Komutu

```bash
claude-supervisor doctor
```

Kontroller:

```text
Claude CLI installed?
Claude version?
Authenticated?
StatusLine integration?
Hooks integration?
Session resume?
SQLite writable?
Git available?
Scheduler healthy?
```

---

# 56. MVP

İlk sürümde:

```text
statusLine quota parser
session_id storage
transcript storage
mechanical checkpoint
semantic handoff
Git snapshot
SQLite state
quota policy
reset scheduler
claude --resume
headless resume
CLI
restart recovery
Windows/macOS/Linux
```

olmalıdır.

---

# 57. MVP Dışında

İlk sürümde:

```text
GUI
cloud dashboard
multi-user
distributed workers
team analytics
automatic PR creation
automatic commit
multiple simultaneous Claude jobs
```

olmamalıdır.

---

# 58. Geliştirme Sırası

## GOAL-01 — Claude Integration

```text
statusLine parser
session ID
transcript
hooks
```

## GOAL-02 — Job State

```text
SQLite
state machine
event log
```

## GOAL-03 — Quota Engine

```text
5h limit
7d limit
threshold policy
reset calculation
```

## GOAL-04 — Checkpoint Engine

```text
mechanical state
Git snapshot
semantic handoff
```

## GOAL-05 — Resume Engine

```text
claude --resume
headless resume
interrupted turn
```

## GOAL-06 — Scheduler

```text
reset wait
manual schedule
restart recovery
```

## GOAL-07 — Native Auto-Resume Coordination

```text
quota_auto_resume events
stale detection
fallback
```

## GOAL-08 — Cross-platform Packaging

```text
Windows
macOS
Linux
```

---

# 59. Uçtan Uca Örnek

Kullanıcı:

```bash
claude-supervisor run
```

Supervisor:

```text
Session: 550e8400...
5h remaining: 42%
7d remaining: 67%
State: RUNNING
```

Bir süre sonra:

```text
5h remaining: 9%
```

State:

```text
CHECKPOINT_PENDING
```

Claude semantic handoff üretir.

Daha sonra:

```text
5h remaining: 4.5%
```

State:

```text
STOP_NEW_WORK
```

Mekanik checkpoint alınır.

Limit dolar:

```text
State: WAITING_QUOTA

Reset: 01:32
Resume scheduled: 01:34
```

Eğer Claude native auto-resume aktifse supervisor bekler.

Native resume olmazsa:

```bash
claude --resume 550e8400...
```

başlatılır.

İlk prompt:

```text
Önceki işe kaldığın noktadan devam et.
.claude-resume/handoff.md dosyasını doğrula.
Next Best Action adımından ilerle.
```

State tekrar:

```text
RUNNING
```

olur.

---

# 60. Ana Tasarım İlkeleri

1. Server-side kotayı manipüle etmeye çalışma.
2. UI scraping kullanma.
3. `session_id` ana kimlik olsun.
4. `%10` seviyesinde checkpoint hazırla.
5. `%5` seviyesinde yeni büyük işi durdur.
6. Semantic handoff tek başına yeterli olmasın.
7. Git state ayrıca saklansın.
8. Native Claude auto-resume varsa onu kullan.
9. Supervisor native auto-resume ile yarışmasın.
10. Terminal kapanması ve reboot senaryosu supervisor tarafından yönetilsin.
11. Resume öncesi workspace doğrulansın.
12. Gözetimsiz çalışmada minimum permission kullanılsın.

---

# 61. Sonuç

Claude Code için doğru çözüm:

```text
Claude Code
    │
    ├─ native session
    ├─ statusLine
    ├─ hooks
    ├─ native auto-resume
    └─ resume CLI
           │
           ▼
Claude Session Supervisor
    ├─ quota monitor
    ├─ checkpoint
    ├─ persistent state
    ├─ scheduler
    └─ resume engine
```

Ana akış:

```text
Çalış
→ quota izle
→ %10'da checkpoint hazırla
→ %5'te yeni işi durdur
→ session + Git state'i sakla
→ reset zamanını bekle
→ native auto-resume varsa onu kullan
→ yoksa claude --resume ile session'ı aç
→ handoff'taki Next Best Action'dan devam et
```

Bu yaklaşım Claude Code'un mevcut native yeteneklerini tekrar geliştirmez.

Onların üstüne dayanıklı, restart-safe ve zamanlanabilir bir orchestration katmanı ekler.
