# Codex Reset Guard

Codex'in 5 saatlik veya haftalık kullanım limiti dolduğunda bunu tespit eden ve **sizin onayınızla** bir reset kredisi kullanan, yerel çalışan bir yardımcı araç.

- Ürün tanımı: [`docs/Codex-Reset-Guard-PRD.md`](docs/Codex-Reset-Guard-PRD.md)
- Codex App Server protokolü (Faz 0 spike): [`docs/CODEX_INTEGRATION.md`](docs/CODEX_INTEGRATION.md)

## Durum

MVP-1 geliştirme aşamasında. Hazır olanlar: `status`, `reset`, `doctor`, `config`. Sıradaki: `watch`.

## Gereksinimler

- .NET 10 SDK
- Giriş yapılmış bir Codex CLI (`codex login`). Reset Guard API key istemez, Codex'in mevcut oturumunu App Server üzerinden kullanır.

## Kullanım

```bash
dotnet run --project src/CodexResetGuard.Cli -- status
dotnet run --project src/CodexResetGuard.Cli -- status --json
dotnet run --project src/CodexResetGuard.Cli -- doctor
dotnet run --project src/CodexResetGuard.Cli -- config --init
dotnet run --project src/CodexResetGuard.Cli -- reset          # onay ister
```

`reset` yalnızca Codex gerçekten limitteyken kredi kullanır. Limit 15 dakikadan kısa sürede kendiliğinden açılacaksa teklif etmez (`--force` ile geçilebilir).

Veri dizini: `%APPDATA%\CodexResetGuard` (Windows), `~/Library/Application Support/CodexResetGuard` (macOS), `~/.config/codex-reset-guard` (Linux). `CODEX_RESET_GUARD_HOME` ile değiştirilebilir.

## Proje yapısı

| Proje | Sorumluluk |
|---|---|
| `CodexResetGuard.Core` | Domain modeli, limit/teklif kuralları (`LimitEvaluator`), çifte reset korumalı `ResetManager` |
| `CodexResetGuard.Codex` | `codex app-server` JSON-RPC istemcisi ve protokol eşlemesi |
| `CodexResetGuard.Platform` | Dosya yolları, TOML config, atomik state dosyası, süreçler arası kilit, dosya izinleri |
| `CodexResetGuard.Cli` | `codex-reset` komut satırı |

## Test

```bash
dotnet test
```
