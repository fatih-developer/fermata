# ResetMe

Codex'in 5 saatlik veya haftalık kullanım limiti dolduğunda bunu tespit eden ve **sizin onayınızla** bir reset kredisi kullanan, yerel çalışan bir yardımcı araç.

- Ürün tanımı: [`docs/ResetMe-PRD.md`](docs/ResetMe-PRD.md)
- Codex App Server protokolü (Faz 0 spike): [`docs/CODEX_INTEGRATION.md`](docs/CODEX_INTEGRATION.md)

## Durum

MVP-1 komutları hazır: `status`, `watch`, `reset`, `doctor`, `config`, `logs`. Sıradaki: MVP-2 (arka plan servisi, yerel bildirimler, tray).

## Gereksinimler

- .NET 10 SDK
- Giriş yapılmış bir Codex CLI (`codex login`). ResetMe API key istemez, Codex'in mevcut oturumunu App Server üzerinden kullanır.

## Kullanım

```bash
dotnet run --project src/ResetMe.Cli -- status
dotnet run --project src/ResetMe.Cli -- watch          # izler, limit dolunca [y/N] sorar
dotnet run --project src/ResetMe.Cli -- status --json
dotnet run --project src/ResetMe.Cli -- doctor
dotnet run --project src/ResetMe.Cli -- config --init
dotnet run --project src/ResetMe.Cli -- reset          # onay ister
dotnet run --project src/ResetMe.Cli -- logs -n 100    # son log kayıtları (--json, --path)
```

`watch` her limit olayı için en fazla bir kez sorar. "Hayır" derseniz aynı limit için tekrar sormaz. Soru beklerken izleme duraklar. `mode = "automatic"` ayarıyla onay istemeden reset yapar; günlük ve haftalık sınırlar ile cooldown geçerlidir.

`reset` yalnızca Codex gerçekten limitteyken kredi kullanır. Limit 15 dakikadan kısa sürede kendiliğinden açılacaksa teklif etmez (`--force` ile geçilebilir).

Loglar veri dizinindeki `logs/resetme-YYYY-MM-DD.log` dosyalarına JSON satırları olarak yazılır. Varsayılan saklama süresi 14 gündür, seviye `[logging]` bölümünden ayarlanır. E-posta adresleri, token'lar, JWT'ler ve API key'ler yazılmadan önce temizlenir.

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
scripts/test-in-docker.sh        # Linux konteynerinde (Smart App Control engelinden etkilenmez)
```

### Gerçek kredi harcamadan uçtan uca deneme

`scripts/fake-codex/`, limitte olan bir hesabı taklit eden sahte bir `codex app-server` içerir (Node.js gerekir). `config.toml`:

```toml
[codex]
executable = "C:/path/to/repo/scripts/fake-codex/codex.cmd"   # macOS/Linux: .../fake-codex/codex
```

`FAKE_CODEX_STATE` durumu çalıştırmalar arasında saklar, `FAKE_CODEX_LOG` her `consume` çağrısını bir satır olarak yazar.
