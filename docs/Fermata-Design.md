# Fermata — Tasarım

**Durum:** 0.5.0 ile uygulandı. Bu belge iki taslağın yerini alır: `archive/codex-quota-supervisor-design.md` ve `archive/claude-code-quota-supervisor.md`. Uygulama planı: [`plan/Fermata-Plan.md`](plan/Fermata-Plan.md).

Fermata, Codex limit izleme ve onaylı reset kredisinin üzerine kurulmuş bir **job katmanıdır**. Uzun Codex ve Claude Code işlerini kota bitmeden durdurur, reset zamanını bekler ve aynı oturumu sürdürür. Ayrı bir ürün değildir: monitör, Codex adaptörü, daemon bağlantısı, tray, bildirim, autostart ve güncelleme altyapısını paylaşır.

## 1. İlkeler

1. **Native mekanizmayla yarışma.** Codex goal'ü kendiliğinden sürüyorsa ya da Claude'un auto-resume'u kuruluysa Fermata bekler. Yalnızca onlar devam etmeyecekse devreye girer.
2. **Tur başlatma.** Fermata Codex'te `turn/start` göndermez; thread açar, goal kurar ve thread'den aboneliğini kaldırır. Turları daemon yürütür, onay istekleri kullanıcının istemcisine gider.
3. **Kararlar saf fonksiyonda.** `JobPolicy.Decide(job, context) → JobAction` dosya, süreç ya da saat okumaz; bütün durum × olay tablosu birim testlerle kapsanır.
4. **LLM'siz checkpoint.** Mekanik checkpoint her zaman yazılır. Semantik handoff (`.fermata/handoff.md`) ajandan istenir ama ona bağımlı değildir.
5. **Kredi kararı kullanıcıda.** Codex'te mevcut mod mantığı (manual / confirm / automatic) geçerlidir. Claude'da kredi yalnızca claude.ai'den kullanılabildiği için Fermata önerir, kendisi kullanmaz.
6. **Bozuk hook işi durdurmaz.** Hook'lar ve status line her durumda 0 ile çıkar ve çıktı üretir.

## 2. Mimari

```
Fermata.Core       Job, QuotaSnapshot, QuotaPolicy, JobPolicy (saf), IJobProvider, IJobStore, JobPrompts
Fermata.Platform   JsonJobStore (+events.ndjson, requests/), CheckpointWriter (git), JobScheduler, JobNotifications, LegacyMigration
Fermata.Codex      CodexDaemonClient (paylaşılan daemon), CodexSessionResumer, CodexJobProvider
Fermata.Claude     ClaudeLocator, ClaudeCli, ClaudeJobProvider, ClaudeSettingsInstaller, ClaudeHookHandler, ClaudeStatusLine
Fermata.Cli        fermata: run/adopt/jobs/job/pause/resume/cancel/checkpoint, claude install|status|uninstall, mcp (fermata_jobs)
Fermata.Desktop    FermataApp: flyout'ta job satırları, Details'ta Jobs bölümü, "Add to Claude Code"
```

### 2.1 Job modeli

`Job`: `Id` (slug), `Provider` (Codex | Claude), `Origin` (Started | Adopted), `Cwd`, `Session` (`Id` = Codex thread / Claude session, `ShortId` = Claude arka plan id'si), `Objective`, `Status`, `ResumeMode` + `ResumeAt`, `BlockReason`, `StopReason`, `LastCheckpoint`, `LastQuota`, `HandoffRequestedAt`, `CreditSuggestedAt`, `ForceResume`, `ResumeNow`, backoff alanları ve zaman damgaları.

Durumlar: `Scheduled`, `Running`, `Checkpointing`, `WaitingQuota`, `Paused`, `BlockedApproval`, `BlockedUser`, `BlockedWorkspace`, `Completed`, `Failed`, `Cancelled`. Taslaklardaki geçici durumlar (ResumePending, Resuming) durum değildir; `events.ndjson`'a olay olarak yazılır.

`ResumeMode`: `Manual` (yalnızca kullanıcı), `QuotaAvailable` (kota izin verince), `At` (zamanında, kota izin veriyorsa).

### 2.2 Kota

`QuotaSnapshot` sağlayıcıdan bağımsızdır: pencereler (`FiveHour` 300 dk, `Weekly` 10080 dk, `Other`), `UsageAllowed?`, `LimitReported`, `Source`, `CapturedAt`.

- Codex: monitörün `status.json`'ından (yalnızca taze ise). `UsageAllowed` = `ordinaryUsageAllowed` ve otoriterdir.
- Claude: status line sarmalayıcının yazdığı `claude-quota.json` (`rate_limits.five_hour` / `seven_day`; `used_percentage` ya da `utilization`, `resets_at` Unix saniye ya da ISO). `LimitReported`, hook'ların gördüğü açık bir limit epizodudur.

`QuotaPolicy.Assess` (saf):

| Seviye | Koşul |
|---|---|
| Unknown | snapshot yok; hiçbir şey durdurulmaz |
| Prepare | kalan ≤ `prepare_remaining` (10) |
| StopNewWork | kalan ≤ `stop_remaining` (5) |
| Blocked | Codex: `UsageAllowed == false`. Claude: kalan 0 ya da açık limit epizodu |

`resets_at`'i geçmiş bir pencere sıfırlanmış sayılır. `ResumeAt = max(ilgili pencerelerin resets_at) + grace_seconds (90)`. %2 "emergency" seviyesi yok, çünkü tam sayı yüzdelerle bu çözünürlük güvenilir değil.

### 2.3 JobPolicy

Girdi: job, `QuotaAssessment`, kotanın sağlayıcıca teyidi (`UsageAllowed == true`), normalize edilmiş oturum durumu, workspace değişti mi, sağlayıcı yetenekleri (`ResetCredits`, `SupportsHandoff`), saat.

Oturum durumları: `Running`, `Idle`, `WaitingApproval`, `WaitingInput`, `NativeWaiting` (native resume kurulu), `LimitStopped` (limitte durdu, kimse sürdürmeyecek), `Interrupted` (daemon yeniden başladı, reboot, çökme), `Paused`, `Completed`, `Gone`, `Unknown`.

Özet tablo:

| Job durumu | Oturum / kota | Aksiyon |
|---|---|---|
| Running | kota ≥ Prepare, handoff istenmemiş | `RequestHandoff` (Claude) ya da mekanik checkpoint (Codex) |
| Running | kota ≥ StopNewWork | `PauseAtTurnBoundary` → Checkpointing |
| Running | NativeWaiting | WaitingQuota, beklenir |
| Running | LimitStopped | checkpoint → WaitingQuota(ResumeAt) |
| Running | Interrupted | kota ve workspace izin verince Resume |
| Running | WaitingApproval / WaitingInput | BlockedApproval / BlockedUser |
| Running | Gone (limit yok) | checkpoint → Paused (Manual) |
| Checkpointing | tur bitti | checkpoint → WaitingQuota ya da (kullanıcı pause'u) Paused |
| WaitingQuota | NativeWaiting | bekle (uzun beklemede kredi önerisi) |
| WaitingQuota | ResumeAt geldi, kota açık | workspace aynıysa Resume, değilse BlockedWorkspace |
| WaitingQuota | kota teyitli açık (Codex, kredi sonrası) | ResumeAt beklemeden Resume |
| Scheduled | zamanı geldi | kota açıksa Start, değilse WaitingQuota |
| her durum | Completed | Complete |

Kredi önerisi yalnızca `ResetCredits == ManualWeb` (Claude) için, kota bloklu ve bekleme `suggest_reset_after_minutes`'tan (120) uzunsa, epizot başına bir kez yapılır.

### 2.4 Depolama ve tek yazar

`jobs/<id>/job.json` atomik yazılır (temp + move + son geçerli kopya `.bak`). Yanında `events.ndjson` (yalnızca ekleme) ve `checkpoints/`. SQLite yok: birkaç job için gereksiz, AOT ve mevcut JSON kalıbıyla tutarlı.

Var olan bir job'u yalnızca `jobs.lock`'u tutan süreç yazar. Diğerleri (CLI, masaüstü düğmeleri) `jobs/<id>/requests/` altına istek dosyası bırakır; zamanlayıcı bunları 2 saniyede bir toplar. Hiçbir zamanlayıcı çalışmıyorsa CLI kilidi kendisi alır ve isteği doğrudan uygular. Claude hook'ları yalnızca `events.ndjson`'a ekler ve `claude-sessions/<id>.json` oturum kayıtlarını yazar.

### 2.5 Zamanlayıcı

`JobScheduler` masaüstünde (`DesktopHost`) ve `fermata daemon`'da çalışır; `jobs.lock` tek host garantisi verir. Döngü her 2 saniyede duvar saatine bakar ve 30 saniyede bir (ya da bekleyen istek varsa hemen) tick yapar; uykudan uyanınca kaçırılan zamanlar hemen yakalanır. Açılışta açık job'lar diskten yüklenir (reboot kurtarma). Geçici sağlayıcı hatalarında backoff: 30 sn → 1 dk → 2 dk → 5 dk → 10 dk → 30 dk. Kalıcı hatalar (claude yok, klasör güvenilmemiş, thread bilinmiyor) job'u `Failed` yapar.

### 2.6 Checkpoint ve handoff

`CheckpointWriter`: `git rev-parse --abbrev-ref HEAD`, `HEAD`, `status --porcelain`, `diff --stat HEAD`, isteğe bağlı `diff HEAD` (`[jobs] save_patch`), oturum, kota, handoff dosyası var mı. `.fermata/` satırı `git rev-parse --git-path info/exclude` ile bulunan dosyaya eklenir (worktree'lerde de doğru yer).

Semantik handoff repo içinde `.fermata/handoff.md`'dir; ajan sandbox'ı repo dışına yazamayabilir. Şablon: Active Goal, Completed, Current Work, Remaining, Verification, Important Decisions, Next Best Action, Do Not. Devam istemi önce bu dosyayı okumayı ve git durumunu doğrulamayı söyler.

Workspace kontrolü: devam öncesi branch/HEAD checkpoint'le karşılaştırılır (commit edilmemiş değişiklikler sayılmaz). Fark varsa `BlockedWorkspace` + bildirim; `fermata resume <id> --force` ile geçilir.

## 3. Codex sağlayıcısı

- **Bağlantı:** `CodexDaemonClient`, `$CODEX_HOME/app-server-control/app-server-control.sock` üzerinden WebSocket JSON-RPC konuşur (`DaemonTransport`). Daemon çalışmıyorsa başlatma ve devam için `codex app-server daemon start` çalıştırılır; gözlem için çalıştırılmaz (`Interrupted` döner).
- **run:** `thread/start {cwd, sandbox: workspace-write, approvalsReviewer}` → `thread/unsubscribe` → `thread/goal/set {objective, status: active}`. `[jobs.codex] approvals = "user" | "auto_review"`, `model` (boş = Codex varsayılanı).
- **Durum eşlemesi:** thread `active` + `waitingOnApproval` → WaitingApproval, `waitingOnUserInput` → WaitingInput; goal `usageLimited` → LimitStopped, `complete` → Completed, `blocked` / `budgetLimited` → WaitingInput, `paused` → (tur sürüyorsa Running, değilse) Paused; goal `active` + thread `notLoaded` / `systemError` → Interrupted.
- **pause / resume:** goal `paused` / `active`. Thread yüklü değilse önce `thread/resume` + `thread/unsubscribe`.
- **adopt:** yalnızca daemon'da yüklü thread'ler. Goal yoksa `--objective` gerekir; goal'süz sahiplenme yok, çünkü o zaman Fermata tur göndermek zorunda kalırdı.
- **Kredi:** "bekle mi, kredi mi" kararını mevcut `RateLimitMonitor` + mod verir; job motoru yalnızca kotayı izler. Kredi sonrası `UsageAllowed == true` olunca job `ResumeAt`'i beklemeden sürer.
- **continue_after_reset:** job'u olmayan `usageLimited` goal'ler reset sonrası eskisi gibi `active` yapılır; job'lara ait thread'ler atlanır (workspace kontrolünü zamanlayıcı yapar).

## 4. Claude Code sağlayıcısı

- **Kurulum** (`fermata claude install`, `~/.claude/settings.json` ya da `$CLAUDE_CONFIG_DIR`): status line `"<fermata>" claude statusline` olur, önceki status line `claude-statusline.json`'da saklanır ve kaldırmada geri konur. Hook'lar: SessionStart, SessionEnd, Notification, Stop, StopFailure → `"<fermata>" claude hook <olay>`. Diğer içerik korunur, `.fermata.bak` yedeği alınır, parse edilemeyen dosyaya dokunulmaz. Skill `~/.claude/skills/fermata/SKILL.md`, MCP `claude mcp add --scope user fermata`.
- **Status line:** `rate_limits` + `session_id` + `cwd` → `claude-quota.json`; sonra önceki komut (Windows'ta Git Bash, yoksa cmd; diğerlerinde sh) aynı stdin ile çalıştırılır ve çıktısı aynen basılır.
- **Hook'lar:** SessionStart (oturum başladı; limit epizodunu kapatır), SessionEnd (`reason`), Notification (`notification_type`: `quota_auto_resume_fired/stale/disabled`, `permission_prompt`, `worker_permission_prompt`, `agent_needs_input`, `elicitation_dialog`, `idle_prompt`, `agent_completed`), StopFailure (`error: rate_limit` → limit epizodu), Stop.
- **Stop hook enjeksiyonu:** oturum bir job'a aitse, job Running ise, seviye Prepare ise (ya da zamanlayıcı handoff istediyse), epizotta henüz istenmemişse ve `stop_hook_active` false ise `{"decision":"block","reason":"<handoff şablonu>"}` döner; `jobs/<id>/handoff-injected` işareti tekrarını engeller, devamda silinir. StopNewWork seviyesinde durmaya izin verilir ve mekanik checkpoint yazılır.
- **run:** `claude --bg --permission-mode <mode> --name "fermata: <job>" "<objective + not>"` (varsayılan izin modu asla bypass değildir). Çıktıdaki `backgrounded · <id>` okunur, tam session id `claude agents --json --all`'dan bulunur.
- **Durum:** `claude agents --json --all` (süreç yaşıyor mu, `status` busy/idle, arka plan `state` done/failed…) + hook kayıtları. Limit epizodu açık ve süreç yaşıyorsa NativeWaiting; ancak reset + 10 dk geçip auto-resume ateşlenmediyse, Claude vazgeçtiyse (`stale`/`disabled`) ya da süreç öldüyse LimitStopped. Süreç yok ve SessionEnd görülmüşse Gone, görülmemişse Interrupted.
- **Devam:** arka plan oturumu yaşıyorsa `claude stop <id>`, ardından `claude --bg --resume <session> "<devam istemi>"`. Etkileşimli oturum hâlâ açıksa devam edilmez (kullanıcıya terminalde sürdürmesi söylenir). Kota tekrar doğrulanamaz; hâlâ limitteyse Claude auto-resume'u yeniden kurar ve Fermata bunu hook'lardan görür.
- **pause:** kullanıcı pause'unda arka plan oturumu `claude stop` ile durdurulur (konuşma korunur); kota durdurmasında tur sonu beklenir.
- **Kredi:** destekli manuel. Claude kredileri yalnızca claude.ai "Limit resets" sayfasından kullanılabilir (2.1.289 binary'sinde CLI ya da API yok). Uzun beklemede bildirim + "Open Claude limit resets…" (`[jobs.claude] limit_resets_url`). Sayı ve son kullanma tarihi okunamadığından UI "kredin olabilir" der. Çerez ya da gayriresmî web API'siyle otomatik kullanım yapılmaz. Resmî bir yol gelirse sağlayıcı yeteneği `ResetCredits = Api` olur ve Codex'teki mod mantığı uygulanır.

## 5. Doğrulanmış gerçekler (spike'lar, 2026-10-05)

Codex 0.160.0, token harcamadan (geçersiz model adı):

- Yeni thread + `thread/goal/set active` → daemon turu kendisi başlatır.
- Bağlantı kapansa da, `thread/unsubscribe` sonrası da thread daemon'da yüklü kalır ve goal turu sürdürür; abonelik kalkınca turn olayları ve onay istekleri o bağlantıya gelmez.
- Başarısız tur (geçersiz model) → goal `blocked`, thread `systemError`.
- `fermata run` → goal → `usageLimited` simülasyonu → `waiting-quota` → `resumed` uçtan uca çalıştı.
- Doğrulanmadı: abonesiz thread'de onay isteğinin `waitingOnApproval` görünmesi ve `codex resume <id>`'nin daemon thread'ine bağlanması (gerçek tur gerektirir).

Claude Code 2.1.289, geçersiz modelle:

- `--bg` `--session-id`'yi yok sayar ("--bg manages the session id") ve `backgrounded · <id>` yazar; arka plan id'si session id'nin ilk 8 karakteridir.
- `--bg`, güven onayı verilmemiş klasörde çalışmaz ("Workspace not trusted").
- `claude agents --json --all` makine-okunur dizi verir: etkileşimli (`pid`, `status`) ve arka plan (`id`, `state`, yaşıyorsa `pid`/`status`) oturumları.
- `--bg --resume <session> "prompt"` ek bayrak olmadan aynı oturumu kayıtlı seçenekleriyle uyandırır; bayrakla çağrılırsa kopya açar.
- Arka plan oturumları TUI'yi gizli bir terminalde çalıştırır ve kullanıcının status line'ı orada da işler (logs çıktısında `5h:22% 7d:63%`), yani sarmalayıcı `--bg` job'larının kotasını da görür.
- Hook girdileri: Notification `notification_type`, Stop `stop_hook_active`, StopFailure `error` (`rate_limit`), SessionEnd `reason`. "armed" bildirimi yoktur; native beklemenin varlığı limit epizodundan ve sürecin yaşamasından çıkarılır.
- Doğrulanmadı (gerçek tur ya da gerçek kredi gerektirir): Stop hook'un `--bg` oturumunda `decision: block` davranışı, status line'ın gerçek `rate_limits` şekli, kredi sonrası snapshot ve auto-resume davranışı, claude.ai "Limit resets" sayfasının kesin URL'si (varsayılan `https://claude.ai/settings/usage`, config'ten değiştirilebilir).

## 6. Yapılandırma

```toml
[jobs]
prepare_remaining = 10     # handoff iste
stop_remaining = 5         # tur sonunda dur (0 = yalnızca limitte)
grace_seconds = 90
save_patch = false

[jobs.codex]
approvals = "user"         # "user" | "auto_review"
model = ""                 # boş = Codex varsayılanı

[jobs.claude]
executable = ""            # boş = PATH, sonra ~/.local/bin
permission_mode = "default"
suggest_reset_after_minutes = 120
limit_resets_url = "https://claude.ai/settings/usage"
```

## 7. Kapsam dışı

- Codex'te goal'süz thread'leri sürdürmek (Fermata tur göndermez).
- Claude kredisini otomatik kullanmak.
- Birden çok makine arasında job taşımak.
- Kotayı Fermata'nın kendisinin ölçmesi: sağlayıcının bildirdiği değerler kullanılır.
