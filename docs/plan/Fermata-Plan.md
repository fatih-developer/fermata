# Fermata — ResetMe'den kota bilinçli job supervisor'a

## Context

ResetMe bugün Codex limitlerini izliyor, onayla reset kredisi kullanıyor ve reset sonrası `usageLimited` Codex goal'lerini sürdürüyor. İki tasarım taslağı (`docs/codex-quota-supervisor-design.md`, `docs/claude-code-quota-supervisor.md`) bunu genişletiyor: uzun işleri kota bitmeden checkpoint'e almak, reset zamanını beklemek, zamanlanmış devam (`--at/--in`), reboot sonrası kurtarma, Codex **ve** Claude Code için.

Kararlar (kullanıcıyla):
- Ürün adı **Fermata** (dur → bekle → kaldığın yerden devam). Yeniden adlandırma **tam ve ilk adımda**.
- MVP **Codex + Claude Code birlikte**.
- Claude tarafında hem `claude --bg` ile başlatılan job'lar hem de mevcut oturumu sahiplenme (`adopt`).
- Ayrı ürün değil: mevcut monitör, adapter, daemon bağlantısı, tray, bildirim, autostart ve güncelleme altyapısı üzerine **job katmanı**.

Doğrulanmış gerçekler (taslaklardaki yanlışları düzelten):
- Codex: goal `usageLimited` → `thread/goal/set active` yapılınca daemon turu **kendisi** başlatır (0 token ile test edildi). Fermata turu kendisi başlatmaz; böylece onaylar kullanıcının istemcisine gider. "Blokluyor mu?" sorusunun cevabı `ordinaryUsageAllowed`; yüzdeler yalnızca önleyici politikada kullanılır ve tam sayı gelir.
- Claude Code 2.1.289:
  - `rate_limits` yalnızca **çalışan oturumun status line girdisinde** var. Oturum dışında kota okuyan bir API yok.
  - Native auto-resume var. Notification türleri `quota_auto_resume_fired/stale/disabled`. Reset 24 saatten uzaksa auto-resume kapanıyor; uykuda kaçırılan reset "stale" oluyor.
  - `claude --bg/agents/attach/logs/stop/respawn` var.
  - Onay ve girdi sinyalleri: `permission_prompt`, `agent_needs_input`, `elicitation_dialog`.
  - Claude'da da reset kredileri var ("Full reset", "5-hour reset", süreli; yeni bir özellik). Ancak kredi **yalnızca claude.ai web ayarlarındaki "Limit resets" sayfasından** kullanılabiliyor. Claude Code CLI'da bunun için bir komut ya da API yok (2.1.289 binary'sinde kontrol edildi).

## Mimari

```
Fermata.Core       Job modeli, QuotaSnapshot, QuotaPolicy, JobPolicy (saf karar fonksiyonu), portlar
Fermata.Platform   JSON job store + events.ndjson, CheckpointWriter (git), JobScheduler host, Claude/Codex kurulumcuları
Fermata.Codex      app-server istemcisi (mevcut) + CodexDaemonClient + CodexJobProvider
Fermata.Claude     YENİ: ClaudeLocator, ClaudeJobProvider, status line/hook komut işleyicileri
Fermata.Cli        `fermata` (mevcut komutlar + run/adopt/jobs/job/pause/resume/cancel/checkpoint, claude install)
Fermata.Desktop    `FermataApp` tray: flyout'ta job satırları, Details'ta Jobs bölümü
```

### Sağlayıcıdan bağımsız çekirdek (Fermata.Core/Jobs)
- `Job` record:
  - Kimlik ve köken: `Id` (slug), `Provider` (Codex | Claude), `Origin` (Started | Adopted), `Cwd`, `SessionRef` (Codex threadId / Claude sessionId + bg kısa id).
  - İçerik: `Objective`.
  - Durum: `Status`, `ResumePlan` (`QuotaAvailable` | `At(time)`), `BlockReason`.
  - Kayıtlar: `LastCheckpoint`, `LastQuota`, `HandoffRequestedAt`, zaman damgaları.
- `JobStatus`: Scheduled, Running, Checkpointing, WaitingQuota, Paused, BlockedApproval, BlockedUser, BlockedWorkspace, Completed, Failed, Cancelled. Taslaklardaki geçici durumlar (ResumePending/Resuming) ayrı durum olmayacak, event olarak kaydedilecek.
- `QuotaSnapshot` (sağlayıcıdan bağımsız):
  - Alanlar: pencereler [`kind` (süreden: 300 → 5h, 10080 → weekly), `usedPercent`, `resetsAt`], `UsageAllowed?`, `Source`, `CapturedAt`.
  - Codex: mevcut `CodexUsage` / `StatusSnapshot`'tan eşlenir. Claude: status line snapshot'ından eşlenir.
- `QuotaPolicy` (saf):
  - Seviyeler: Normal / Prepare (kalan ≤ `prepare_remaining`, varsayılan 10) / StopNewWork (≤ `stop_remaining`, 5) / Blocked.
  - Blocked kararı: Codex'te `UsageAllowed == false`; Claude'da kalan 0 ya da limit olayı.
  - `ResumeAt = max(bloklayan resets_at) + grace_seconds` (90).
  - %2 "emergency" seviyesi yok, çünkü tam sayı yüzdelerle bu çözünürlük güvenilir değil.
- `JobPolicy.Decide(job, quota, sessionState, now) → JobAction` (saf, en çok test edilen parça). Aksiyonlar: RequestHandoff, PauseAtTurnBoundary, WriteCheckpoint, WaitUntil(t), Resume, MarkBlocked(reason), Complete, None.
  - Native mekanizmayla yarışmaz: Claude'da auto-resume kuruluysa ve süreç yaşıyorsa → None. Codex'te goal kendiliğinden devam ediyorsa → None.
- Port `IJobProvider`: `StartAsync`, `AdoptAsync`, `GetSessionStateAsync` (Running / Idle / WaitingApproval / WaitingInput / NativeWaiting / Completed / Gone), `PauseAsync`, `ResumeAsync(job, prompt)`, `GetQuota()`.

### Depolama, checkpoint, zamanlayıcı (Fermata.Platform)
- Job deposu: `%APPDATA%\Fermata\jobs\<id>\job.json`. Atomik yazma; `JsonResetStateStore` kalıbı yeniden kullanılır (temp + move + .bak). Yanında `events.ndjson`. SQLite yok (birkaç job için gereksiz; AOT ve JSON kalıbıyla tutarlı).
- `CheckpointWriter` (LLM'siz):
  - Kaydedilenler: `git rev-parse --abbrev-ref HEAD`, `HEAD`, `status --porcelain`, `diff --stat` (`ProcessRunner` ile), sessionRef, kota, zaman damgaları.
  - Hedef: `jobs/<id>/checkpoints/<ts>.json`.
  - İsteğe bağlı patch: `[jobs] save_patch`.
- Semantik handoff: proje içinde `.fermata/handoff.md`. Ajan sandbox'ı repo dışına yazamayabileceği için repo içinde tutulur. `.fermata/` satırı `.git/info/exclude`'a otomatik eklenir; commit'i kirletmez.
- Workspace kontrolü: resume öncesi kayıtlı branch/HEAD ile mevcut olan karşılaştırılır. Fark varsa → BlockedWorkspace + bildirim (`fermata resume --force` ile geçilir).
- `JobScheduler`:
  - Desktop'ta (`DesktopHost`) ve `fermata daemon`'da çalışır. Tek host garantisi için `jobs.lock` kullanılır (`FileResetLock` yeniden kullanılır).
  - 30 saniyelik **duvar saati** döngüsü: `Task.Delay`'e güvenmez, böylece uyku sonrası kaçırılan zamanlar yakalanır.
  - Başlangıçta açık job'ları yükler (reboot kurtarma). Geçici hatalarda backoff: 30s → 1m → 2m → 5m → 10m, en fazla 30m.

### Codex sağlayıcısı (Fermata.Codex)
- `CodexSessionResumer`'daki bağlantı kodu `CodexDaemonClient`'a çıkarılır (`DaemonTransport` + `JsonRpcConnection`). Metotlar: `initialize`, `thread/loaded/list`, `thread/start`, `thread/read`, `thread/goal/get|set`, `thread/turns/list`, `thread/status/changed` aboneliği.
- `run`:
  - Daemon çalışmıyorsa `codex app-server daemon start` (ProcessRunner) ile açılır.
  - `thread/start {cwd, sandbox: workspace-write}` → `thread/goal/set {objective, status: active}`. Turları goal yürütür.
  - `[jobs.codex] approvals = "user" | "auto_review"` (Codex'in `approvalsReviewer` alanı).
- Durum eşlemesi:
  - `activeFlags` `waitingOnApproval` → BlockedApproval, `waitingOnUserInput` → BlockedUser.
  - Goal `usageLimited` → WaitingQuota; `complete` → Completed.
  - Bildirimde "`codex resume <id>` ile bağlan" yönergesi verilir.
- Pause: tur bitince (`idle`) goal → `paused`. Resume: goal → `active`.
- Limit dolunca "bekle mi, kredi mi kullan" kararını mevcut `RateLimitMonitor` + mod verir. Job motoru yalnızca `UsageAllowed`'ı bekler.
- `adopt <threadId|--latest>`: yalnızca yüklü bir thread için. Goal yoksa `--objective` ile goal kurulur. Goal'süz sahiplenme yok, çünkü `turn/start` onayları Fermata'ya yönlendirir.
- Mevcut `continue_after_reset` davranışı (job olmayan goal'ler) korunur ve motorun içinde çalışır.

### Claude Code sağlayıcısı (Fermata.Claude, yeni)
- `fermata claude install|uninstall|status` (`CodexIntegration` kalıbında):
  - **Status line sarmalayıcı**: `~/.claude/settings.json` içindeki `statusLine`, `fermata claude statusline` olur. Önceki komut Fermata config'inde saklanır, stdin'i ona aktarılır ve çıktısı aynen basılır. Sarmalayıcı `rate_limits` + `session_id` + `cwd`'yi `claude-quota.json` snapshot'ına yazar.
  - **Hook'lar**: `SessionStart` / `SessionEnd` (süreç yaşıyor mu), `Notification` (quota_auto_resume_*, permission_prompt, agent_needs_input, elicitation_dialog → `jobs/<id>/events.ndjson`), `Stop` (aşağıda).
  - `settings.json` düzenlemesi `CodexHooksInstaller` mantığıyla yapılır: diğer içerik korunur, `.fermata.bak` yedeği alınır, parse edilemeyen dosyaya dokunulmaz.
- **Checkpoint enjeksiyonu (Stop hook)**:
  - Koşullar: oturum bir job'a aitse, seviye ≥ Prepare ise, handoff henüz istenmemişse ve `stop_hook_active` false ise.
  - Çıktı: `{"decision":"block","reason":"<.fermata/handoff.md şablonunu yaz, yeni iş başlatma>"}`.
  - StopNewWork seviyesinde durmaya izin verilir ve mekanik checkpoint yazılır.
- `run`: `claude --bg --session-id <uuid> [--permission-mode default] "<objective + talimat>"`. Kısa id ve session id saklanır. Varsayılan izin modu asla `--dangerously-skip-permissions` değildir.
- `adopt <sessionId|--latest>`: hook'lar session'ı zaten görüyor; job kaydı oluşturulur. Etkileşimli süreç ölürse devam `--bg --resume` ile yapılır.
- Resume kararı (native mekanizmayla yarışmadan):
  - Auto-resume kuruluysa ve süreç yaşıyorsa → bekle.
  - Native ateşlenirse (`fired`) → Running.
  - `disabled` (reset 24 saatten uzak), `stale`, süreç ölmüş, kullanıcı iptal etmiş ya da `--at` zamanı gelmiş durumlarında: `resets_at + grace` zamanında, yaşayan oturum varsa `claude stop <id>`, ardından `claude --bg --resume <session_id> "<resume prompt: handoff'u oku, git'i doğrula, Next Best Action>"`.
  - Kota tekrar doğrulanamaz. Hâlâ limitteyse Claude native auto-resume'u yeniden kurar ve Fermata bunu Notification'dan görür.
- **Kredi: destekli manuel.**
  - Ne zaman: limit dolduğunda ve bekleme süresi `[jobs.claude] suggest_reset_after` (varsayılan 2 saat) değerinden uzunsa.
  - Ne yapılır: "Claude limitte. Kredin varsa Limit resets sayfasında 'Reset for free' ile kullanabilirsin" bildirimi gönderilir. Flyout'ta "Open Claude limit resets" düğmesi çıkar; URL config'ten gelir (`[jobs.claude] limit_resets_url`), varsayılanı spike'ta doğrulanır.
  - Kullanıcı reset yaptıktan sonra: status line snapshot'ı düşen kullanımı gösterir. Native auto-resume ya da Fermata'nın resume yolu devam ettirir. `fermata resume <id> --now` ile de elle tetiklenebilir.
  - Kredi sayısı ve son kullanım tarihi programatik olarak okunamıyor; bu yüzden UI "kredin olabilir" der, sayı göstermez.
  - Çerez ya da gayriresmî web API'siyle otomatik kullanım yapılmaz: kırılgan, hesap güvenliği açısından riskli, kullanım şartlarına aykırı olabilir.
  - Claude ileride resmî bir yol sunarsa sağlayıcı yeteneği (`ResetCredits = Api`) açılır ve Codex'teki mod mantığı (manual/confirm/automatic) aynen uygulanır.
- Sağlayıcı yeteneği çekirdekte tutulur: `IJobProvider.ResetCredits` ∈ {`Api` (Codex), `ManualWeb` (Claude), `None`}. `JobPolicy` "bekle / kredi kullan / kullanıcıya önerip bekle" kararını bu yeteneğe göre verir.

### CLI ve UI
- Yeni komutlar:
  - `fermata run --provider codex|claude (--objective "…" | --goal FILE) [--cwd] [--name] [--at T | --in D]`
  - `fermata adopt --provider … [id|--latest] [--objective]`
  - `fermata jobs`, `fermata job <id>`
  - `fermata pause <id> [--resume-at T | --resume-in D]`
  - `fermata resume <id> [--at T | --in D | --when-quota-available] [--force]`
  - `fermata cancel <id>`, `fermata checkpoint <id>`
- Mevcut komutlar korunur. `doctor` claude, git ve jobs kontrollerini de yapar.
- MCP: salt okunur `fermata_jobs` aracı eklenir. Skill'ler (Codex ve Claude) job komutlarını anlatır.
- Desktop: flyout'ta aktif job satırları (ad, sağlayıcı, durum, bir sonraki devam zamanı, Pause/Resume). Details penceresinde Jobs listesi ve event'ler. Job durum değişikliklerinde bildirim.

## Uygulama sırası

**M0 — Yeniden adlandırma (ResetMe → Fermata)**
- Projeler, namespace'ler ve `.slnx`: `ResetMe.*` → `Fermata.*`. Exe adları `fermata` ve `FermataApp`. `Directory.Build.props` sürümü 0.5.0.
- Sabitler ve adlar:
  - `AppPaths` (`%APPDATA%\Fermata`, `FERMATA_HOME`; `RESETME_HOME` geri uyum için okunur), `Autostart.ValueName` / launchd etiketi / systemd adı, `SingleInstance` adı, bildirim app id'si.
  - `UpdateChecker.DefaultFeed`, asset adları (`fermata-<rid>.zip`), `UpdateInstaller` düzeni, `scripts/package.sh`, `.github/workflows/*`, `app.manifest`.
- **Göç** (ilk açılışta):
  - `%APPDATA%\ResetMe` varsa ve Fermata dizini yoksa `config.toml` ile `state.json` (+.bak) kopyalanır. Bekleyen idempotency anahtarı korunmalı.
  - Eski ResetMe süreci çalışıyorsa (`resetme-desktop` tek-örnek adı) göç durur ve kullanıcıdan kapatması istenir. Aksi halde iki farklı lock dosyası çift reset riski yaratır.
  - Eski autostart kaydı silinir.
- Codex entegrasyonu: hook eşleyici hem `resetme` hem `fermata` adını tanır ve yeniden kurulumda eskisini değiştirir. MCP `resetme` → `fermata`, skill dizini `resetme` → `fermata`.
- GitHub repo adının değişmesi kullanıcının yapacağı bir adım. GitHub API eski URL'yi yönlendiriyor; eski 0.3/0.4 istemcileri yeni asset adını bulamaz ve release sayfasını gösterir (tek seferlik elle kurulum yeterli).

**M1 — Job çekirdeği**: `Job`, `QuotaSnapshot`, `QuotaPolicy`, `JobPolicy`, `IJobProvider`; JSON deposu + events; `CheckpointWriter` + `.git/info/exclude`; `JobScheduler` (DesktopHost ve DaemonCommand içinde); `jobs/job/pause/resume/cancel/checkpoint` komutları (sahte sağlayıcıyla uçtan uca).

**M2 — Codex sağlayıcısı**: `CodexDaemonClient` çıkarımı; `run`, `adopt`, pause/resume (goal), durum eşlemesi, approvals ayarı; `continue_after_reset`'in motora katlanması.

**M3 — Claude sağlayıcısı**: `ClaudeLocator`; `claude install` (status line sarmalayıcı + hook'lar); snapshot; Notification ve Stop hook işleyicileri; `run --bg`, `adopt`, resume yedek yolu.

**M4 — UI ve teslim**: flyout ve Details'ta job'lar, `fermata_jobs` MCP aracı, skill'ler, README/PRD güncellemesi (iki taslak doküman tek bir `docs/Fermata-Design.md`'de birleşir), paketleme/CI, yerel kurulumun Fermata'ya taşınması.

**Spike'lar (ilgili milestone'dan önce, maliyetsiz ya da çok düşük maliyetli):**
- Codex (geçersiz model adıyla, 0 token):
  - Yeni thread + `goal/set active` turu başlatıyor mu?
  - Abonesiz thread'de onay isteği `waitingOnApproval` olarak görünüyor mu?
  - `codex resume <id>` daemon thread'ine bağlanıyor mu?
- Claude (`--model` geçersiz):
  - `claude --bg --session-id X "prompt"` ve `--bg --resume` birlikte çalışıyor mu?
  - `claude agents` makine-okunur çıktı veriyor mu?
  - Stop hook'un `decision: block` davranışı `--bg` oturumunda çalışıyor mu?
  - Status line sarmalayıcısı `rate_limits` alıyor mu? (Bu, gerçek bir oturumda tek ve kısa bir turla doğrulanır.)
  - claude.ai "Limit resets" sayfasının doğrudan URL'si.
  - Kredi kullanıldıktan sonra `rate_limits` snapshot'ı ve native auto-resume nasıl davranıyor? (Kullanıcının gerçek bir kredi kullanımında gözlenir; test için kredi harcanmaz.)

## Yeniden kullanılacak mevcut parçalar
- `src/ResetMe.Codex/AppServer/DaemonTransport.cs`, `CodexSessionResumer.cs` → `CodexDaemonClient`
- `src/ResetMe.Codex/JsonRpc/JsonRpcConnection.cs` (WebSocket adaptörleriyle)
- `src/ResetMe.Platform/Codex/CodexHooksInstaller.cs`, `CodexIntegration.cs` → Claude `settings.json` kurulumcusu için kalıp
- `src/ResetMe.Platform/JsonResetStateStore.cs` (atomik JSON), `FileResetLock.cs`, `Processes/ProcessRunner.cs`
- `src/ResetMe.Core/Monitoring/StatusSnapshot.cs`, `SnapshotObserver.cs`, `RateLimitMonitor.cs` (Codex kotası ve kredi kararı)
- `src/ResetMe.Cli/Commands/HookCommand.cs` / `McpCommand.cs` (hook ve MCP iskeleti)
- `src/ResetMe.Desktop/Views/FlyoutWindow.axaml`, `Services/DesktopHost.cs`

## Doğrulama
- **Birim**:
  - `QuotaPolicy`: pencere sınıflama, iki bloklayan pencerede `max(reset)` + grace, eşik sınırları.
  - `JobPolicy`: tüm durum × olay tablosu; native bekliyorken None; workspace değişti → Blocked; Stop hook döngü koruması.
  - Store atomikliği, göç (pending anahtarın korunması, eski süreç çalışırken durma), `settings.json` birleştirme (diğer anahtarlar ve önceki statusLine korunur).
- **Entegrasyon**:
  - Codex: mevcut `FakeAppServer` + `scripts/fake-codex` genişletilir (goal, status, activeFlags).
  - Claude: yeni `scripts/fake-claude` (`--bg`/`--resume`/`agents`/`stop` taklidi, hook çağrıları); zamanlayıcı sahte saatle (uyku atlaması dahil).
- **Canlı uçtan uca**:
  - Codex: geçersiz model adıyla `fermata run` → goal → `usageLimited` simülasyonu → resume (0 token).
  - Claude: sahte claude ile tam akış; gerçek `claude` ile yalnızca spike'lar.
- **Komutlar**:
  - `dotnet test` (Core/Codex yerelde; Desktop dahil tüm çözüm `scripts/dotnet-in-docker.sh test ResetMe.slnx` ile, 4 tekrar).
  - Yeniden adlandırma sonrası `scripts/package.sh win-x64` → yerel kurulumun yedeklenip Fermata'ya taşınması → `fermata doctor`, `fermata codex status`, `fermata claude status`.
