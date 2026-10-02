# ResetMe

Codex'in 5 saatlik veya haftalık kullanım limiti dolduğunda bunu tespit eden ve **sizin onayınızla** bir reset kredisi kullanan, yerel çalışan bir yardımcı araç.

- Ürün tanımı: [`docs/ResetMe-PRD.md`](docs/ResetMe-PRD.md)
- Codex App Server protokolü (Faz 0 spike): [`docs/CODEX_INTEGRATION.md`](docs/CODEX_INTEGRATION.md)

## Durum

MVP-1 ve MVP-2 tamam: CLI (`status`, `watch`, `reset`, `daemon`, `autostart`, `doctor`, `config`, `logs`), tray / menu bar uygulaması, yerel bildirimler ve açılışta başlatma. Sıradaki: MVP-3 (near-limit uyarıları, self-update, imzalı NativeAOT release'ler).

## Gereksinimler

- .NET 10 SDK
- Giriş yapılmış bir Codex CLI (`codex login`). ResetMe API key istemez, Codex'in mevcut oturumunu App Server üzerinden kullanır.

## Masaüstü uygulaması

`ResetMeApp` tray'de (Windows), menü çubuğunda (macOS) veya StatusNotifierItem olarak (Linux) yaşar:

- Penceresi 5 saatlik ve haftalık kullanımı, kredileri ve son olayları gösterir. Kapatınca tray'e gizlenir.
- Confirm modunda limit dolunca bir onay penceresi açar. "Wait" varsayılandır; kredi yalnızca "Use reset credit" ile kullanılır.
- Ayarlar: mod, kontrol aralığı, doğal açılma eşiği, bildirimler, açılışta başlatma.
- Tray olmayan masaüstlerinde uygulamayı tekrar başlatmak penceresini öne getirir.

```bash
dotnet run --project src/ResetMe.Desktop
```

## Kullanım

```bash
dotnet run --project src/ResetMe.Cli -- status
dotnet run --project src/ResetMe.Cli -- watch          # izler, limit dolunca [y/N] sorar
dotnet run --project src/ResetMe.Cli -- status --json
dotnet run --project src/ResetMe.Cli -- doctor
dotnet run --project src/ResetMe.Cli -- config --init
dotnet run --project src/ResetMe.Cli -- reset          # onay ister
dotnet run --project src/ResetMe.Cli -- logs -n 100    # son log kayıtları (--json, --path)
dotnet run --project src/ResetMe.Cli -- daemon         # başsız izleme (SSH, sunucu, systemd/launchd)
dotnet run --project src/ResetMe.Cli -- autostart enable --target desktop
dotnet run --project src/ResetMe.Cli -- doctor --notify # test bildirimi de gönderir
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
| `ResetMe.Platform` | Dosya yolları, TOML config, atomik state dosyası, süreçler arası kilit, dosya izinleri, bildirimler, açılışta başlatma, tek örnek |
| `ResetMe.Cli` | `resetme` komut satırı |
| `ResetMe.Desktop` | `ResetMeApp`: Avalonia tray / menu bar uygulaması |

## Paketleme

```bash
scripts/package.sh win-x64      # artifacts/package/resetme-win-x64.zip
scripts/package.sh linux-x64    # artifacts/package/resetme-linux-x64.tar.gz
scripts/package.sh osx-arm64    # artifacts/package/resetme-macos-arm64.tar.gz (ResetMe.app)
```

CI her push'ta üç platformda paket üretir, paketlenmiş `resetme daemon`'ı sahte Codex'e karşı çalıştırır ve paketleri artifact olarak saklar.

## Windows notu

Windows 11'de Smart App Control açıksa imzasız geliştirme build'leri engellenebilir (`FileLoadException ... Uygulama Denetimi ilkesi bu dosyayı engelledi`). Karar binary hash'ine bağlıdır; örneğin `dotnet build -p:Version=0.1.2` ile hash'i değiştirerek yeniden derleyin. Avalonia'nın imzasız build görevi masaüstü projesinin derlenmesini de engelleyebilir; o durumda `scripts/dotnet-in-docker.sh` ile Linux konteynerinde derleyin (`publish ... -r win-x64` Windows paketi üretir). Release'ler imzalı NativeAOT olacaktır.

## Test

```bash
dotnet test
scripts/test-in-docker.sh        # Linux konteynerinde (Smart App Control engelinden etkilenmez)
scripts/dotnet-in-docker.sh ...  # herhangi bir dotnet komutu; /out -> ./artifacts
```

### Gerçek kredi harcamadan uçtan uca deneme

`scripts/fake-codex/`, limitte olan bir hesabı taklit eden sahte bir `codex app-server` içerir (Node.js gerekir). `config.toml`:

```toml
[codex]
executable = "C:/path/to/repo/scripts/fake-codex/codex.cmd"   # macOS/Linux: .../fake-codex/codex
```

`FAKE_CODEX_STATE` durumu çalıştırmalar arasında saklar, `FAKE_CODEX_LOG` her `consume` çağrısını bir satır olarak yazar.
