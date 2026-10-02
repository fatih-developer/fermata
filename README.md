# ResetMe

Codex'in 5 saatlik veya haftalık kullanım limiti dolduğunda bunu tespit eden ve **sizin onayınızla** bir reset kredisi kullanan, yerel çalışan bir yardımcı araç.

- Ürün tanımı: [`docs/ResetMe-PRD.md`](docs/ResetMe-PRD.md)
- Codex App Server protokolü (Faz 0 spike): [`docs/CODEX_INTEGRATION.md`](docs/CODEX_INTEGRATION.md)

## Durum

MVP-1 geliştirme aşamasında. Hazır olanlar: `status`, `reset`, `doctor`, `config`. Sıradaki: `watch`.

## Gereksinimler

- .NET 10 SDK
- Giriş yapılmış bir Codex CLI (`codex login`). ResetMe API key istemez, Codex'in mevcut oturumunu App Server üzerinden kullanır.

## Kullanım

```bash
dotnet run --project src/ResetMe.Cli -- status
dotnet run --project src/ResetMe.Cli -- status --json
dotnet run --project src/ResetMe.Cli -- doctor
dotnet run --project src/ResetMe.Cli -- config --init
dotnet run --project src/ResetMe.Cli -- reset          # onay ister
```

`reset` yalnızca Codex gerçekten limitteyken kredi kullanır. Limit 15 dakikadan kısa sürede kendiliğinden açılacaksa teklif etmez (`--force` ile geçilebilir).

Veri dizini: `%APPDATA%\ResetMe` (Windows), `~/Library/Application Support/ResetMe` (macOS), `~/.config/resetme` (Linux). `RESETME_HOME` ile değiştirilebilir.

## Proje yapısı

| Proje | Sorumluluk |
|---|---|
| `ResetMe.Core` | Domain modeli, limit/teklif kuralları (`LimitEvaluator`), çifte reset korumalı `ResetManager` |
| `ResetMe.Codex` | `codex app-server` JSON-RPC istemcisi ve protokol eşlemesi |
| `ResetMe.Platform` | Dosya yolları, TOML config, atomik state dosyası, süreçler arası kilit, dosya izinleri |
| `ResetMe.Cli` | `resetme` komut satırı |

## Windows notu

Windows 11'de Smart App Control açıksa imzasız geliştirme build'leri engellenebilir (`FileLoadException ... Uygulama Denetimi ilkesi bu dosyayı engelledi`). Karar binary hash'ine bağlıdır. Böyle bir durumda örneğin `dotnet build -p:Version=0.1.2` ile hash'i değiştirerek yeniden derleyin. Release'ler imzalı olacaktır.

## Test

```bash
dotnet test
```
