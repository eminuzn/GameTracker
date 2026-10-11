# GameTracker

Windows için mağaza hesabından bağımsız, yerel oyun launcher'ı. C# / .NET 10, WPF ve SQLite kullanır. Oyun indirme, crack veya DRM değiştirme işlevi içermez.

## Çalıştırma

GitHub Releases'teki `GameTracker.exe`, Windows x64 için tek dosyalı portable sürümdür. Kurulum veya .NET kurulumu gerektirmez; indirip çift tıkla. Veriler `%LOCALAPPDATA%\GameTracker` altında saklanır. Native .NET/WPF/SQLite bağımlılıkları ilk açılışta çalışma zamanı tarafından geçici dizine çıkarılır; exe'nin yanında DLL gerekmez. Dosya kod imzası içermez.

Tek dosyalı sürümü üretmek için `Build.ps1 -Publish` veya:

```powershell
dotnet publish src/GameTracker.Desktop -c Release -p:PublishProfile=Portable -m:1 -o artifacts/portable-win-x64
```

Windows ve .NET 10 SDK gerekir. Komutları proje kökünde çalıştır:

```powershell
dotnet build GameTracker.sln -c Release -m:1
dotnet run --project tests/GameTracker.Tests -c Release --no-build
dotnet run --project tests/GameTracker.Desktop.Tests -c Release --no-build
dotnet run --project src/GameTracker.Desktop
```

Alternatif: `Build.ps1` derleme ve kontrolleri, `Run.ps1` masaüstü uygulamasını çalıştırır. Test projesi console harness'tir; `dotnet test` yerine yukarıdaki `dotnet run` kullanılır. `global.json` .NET 10 SDK'yı seçer; SDK'nın terminalin PATH'inde veya IDE ayarlarında erişilebilir olması gerekir.

## Kütüphane

“Oyun klasörü ekle” ile bir ana klasör seç. Her doğrudan alt klasör tek oyun kabul edilir; içeriği recursive taranır. Oyunların hangi mağazadan veya kaynaktan kurulduğu bu mekanizmayı değiştirmez.

- Exe adayları isim, metadata ve yardımcı araç sinyalleriyle değerlendirilir. Belirsizse kullanıcı seçimi istenir; kartta kırmızı yazı/ünlem görünür.
- Liste ve ızgaradaki oyun kartını çift tıklamak oyunu başlatır. Favori düğmesini çift tıklamak oyun başlatmaz; exe eksikse seçim açılır.
- Oyun boyutu, son oynama, toplam süreç çalışma süresi ve oturumlar saklanır. Başlatıcı süreç başka bir oyun sürecine devredebilir; takip exe'leri elle de eklenebilir.
- Liste/ızgara geçişi ve ızgarada 3/4 sütun. Arama, favoriler, yalnızca exe seçilmeli filtresi; isim, son oynama ve boyut sıralaması iki yönde çalışır. Görünüm tercihleri saklanır.
- Exe'si olmayan 50.000.000 bayt altındaki klasörler süzülür; kayıt/geçmiş silinmez. Geçerli exe'si seçilmiş küçük oyunlar görünür.
- X düğmesi sistem tepsisine küçültür; takip sürer. “Tamamen çık” uygulamayı kapatır, oyunu kapatmaz. Uygulama aynı anda tek örnek çalıştırır.
- Listeden kaldırma dosyaları silmez. Oyun silme, exe'nin alt dizini yerine ana klasörün doğrudan altındaki tüm oyun klasörünü hedefler. Geri dönüşüm ve çift onaylı kalıcı silme seçenekleri vardır; link/junction ve fiziksel klasör kimliği kontrol edilir.

Yeni keşfedilen oyunda launcher geçmişi yoksa Windows Prefetch'teki tam exe yolu/volume kimliği eşleşen son çalıştırma tarihi bir kez aktarılır. Başarısız veya erişim reddedilmiş deneme de tekrar sorgulanmaz; mevcut kayıtlara geriye dönük uygulanmaz. Prefetch okumak yönetici yetkisi gerektirebilir. Tarih aktarımı geçmiş süre/oturum üretmez; sonraki canlı süreç gözlemleri tarihi günceller. Dosya değiştirme/erişim tarihleri oynama tarihi olarak kullanılmaz.

## Oyun sürümleri

Kütüphane taraması oyun kökündeki ve seçilen exe'nin üst dizinlerindeki küçük `version.txt`, `game.version`, `product.version`, `version.json`, `build.txt` dosyalarını ve exe ürün/dosya metadata'sını okur. Motor sürümü ve sıfır placeholder değerleri elenir. Dosyalarda açık sürüm yoksa bilinmiyor olarak bırakılır; tek bir evrensel oyun sürüm standardı yoktur.

Detayda oyun adının altındaki **Oyun sürümü** alanı düzenlenebilir. **Değişiklikleri kaydet** ile elle girilen değer, boş bırakılmış olsa bile sonraki taramalarda korunur. **Sürümü algıla** manuel değeri bırakıp dosyalardan yeniden okur. Liste ve ızgarada yerel sürüm görünür.

**Sürüm güncelliğini kontrol et** tüm görünen kütüphane kayıtlarını (arama/favori filtresinden bağımsız), **Sürümü kontrol et** seçili oyunu inceler. Sürümü olmayan oyunlar ağ isteği yapılmadan atlanır. Kontrol yalnızca bu düğmelerle yapılır; her açılışta Steam taranmaz. Sonuç ve zaman SQLite'ta saklanır; sürüm veya oyun adı değişirse sonuç sıfırlanır.

Steam'de oyun/edition adı tek ve kesin eşleşmeli, sonuç ana oyun olmalıdır. Anahtarsız [Steam News API](https://partner.steamgames.com/doc/webapi/ISteamNews#GetNewsForApp) üzerinden son 100 resmi geliştirici duyurusu okunur; yayımlanmış patch/update başlıklarındaki sürümler kullanılır. Gelecek/beta/platform/DLC duyuruları ve belirsiz sürümler elenir. Sayısal bileşenler aynı hassasiyette karşılaştırılır (`1.9.0 < 1.10.0`). Daha yeni resmi duyuru sürümü bulunursa **sarı ⚠ Güncel değil** görünür; eşleşme **Steam duyurusuyla eşleşiyor** olarak belirtilir. Kaynak duyurusu detaydan açılabilir.

Bu kontrol Steam'in bütün oyunlar için kesin en son binary sürümünü sağladığı anlamına gelmez. Steam build ID, exe dosya sürümü ve oyunun release numarası farklı olabilir. File metadata/build numarası release numarasıyla karşılaştırılmaz; biçimler farklıysa veya açık duyuru yoksa **Karşılaştırılamadı** gösterilir. Gerekirse oyunda görünen release sürümünü elle yaz. Herhangi bir mağazaya ait kurulum/hesap şartı yoktur; dosyalar otomatik güncellenmez ve oyun indirilmez.

## Kapaklar

Kaynak sırası: **Steam → SteamGridDB → IGDB → RAWG → Google Shopping → placeholder**. Oyun adı belirsizse veya görsel kullanılamıyorsa sıradaki kaynak denenir. Kapaklar ilk keşifte ve eksik olduğunda aranır, yerelde cache'lenir. İnternet hataları yerel oyun yönetimini engellemez. Elle kapak seçme ve otomatik kapağa dönme desteklenir.

Steam ve SteamGridDB herkese açık web aramalarıyla anahtarsız kullanılabilir. Bunlar kalıcı API sözleşmesi değildir; sağlayıcı değişiklikleri entegrasyon güncellemesi gerektirebilir. Varsayılan `src/GameTracker.Desktop/appsettings.json` içindeki `CoverServiceUrl` null'dır.

IGDB, RAWG ve Google Shopping için geliştiricinin yapılandırdığı ayrı ASP.NET Core kapak servisi gerekir. Repo gerçek API anahtarı veya çalışan genel servis adresi içermez. Anahtarlar masaüstü uygulamasına gömülmez; kullanıcıdan anahtar istenmez. Servise yerel klasör yolu yerine yalnızca oyun adı gönderilir.

Yerel servis geliştirmesinde ilgili ortam değişkenlerini **servis terminalinde**, kendi anahtarlarınla tanımla:

```powershell
$env:STEAM_API_KEY = '<steam-web-api-key>'          # İsteğe bağlı
$env:STEAMGRIDDB_API_KEY = '<steamgriddb-api-key>' # İsteğe bağlı
$env:IGDB_CLIENT_ID = '<twitch-client-id>'
$env:IGDB_CLIENT_SECRET = '<twitch-client-secret>'
$env:RAWG_API_KEY = '<rawg-api-key>'
$env:SERPAPI_API_KEY = '<serpapi-api-key>'
.\Run-CoverService.ps1
```

Anahtar olmayan sağlayıcılar atlanır. Masaüstünün `CoverServiceUrl` değerini geliştirmede `http://localhost:5188`, uzak serviste HTTPS adresi olarak ayarla. Google Shopping adaptörü SerpApi kullanır; oyun/sürümü eşleşen ilk kullanılabilir PNG/JPEG thumbnail seçilir. Anahtarsız doğrudan Google HTML scraping uygulanmaz; ayrıca tarayıcıda Shopping araması açan düğme vardır. Kapak kaynağı/attribution detayda gösterilir.

## Yerel veriler ve repo

Veriler kullanıcının `%LOCALAPPDATA%\GameTracker` dizininde tutulur: `gametracker.db`, `covers`, `errors.log`. Windows, kullanıcı ve oyun dizinleri çalışma anında belirlenir; kurulum belirli bir bilgisayar yoluna bağlı değildir.

`.gitignore` IDE kişisel ayarlarını, derleme/publish çıktılarını, veritabanlarını, cache ve logları, `.env` ve yerel ayar/anahtar dosyalarını dışlar. API anahtarlarını kaynak koduna veya sürümlenen ayarlara yazma. Testler geçici fixture dizinleri kullanır; testlerdeki sabit örnek yollar gerçek oyun kütüphanesine erişmek için kullanılmaz. Public Prefetch fixture'ın MIT lisansı kendi dizininde korunur.

## Yapı

- `src/GameTracker.Core`: modeller, tarama, eşleştirme ve klasör sınırları.
- `src/GameTracker.Infrastructure`: SQLite, süreç/Prefetch takibi, başlatma/silme ve kapak cache'i.
- `src/GameTracker.Desktop`: WPF arayüzü, sistem tepsisi ve uygulama ikonu.
- `src/GameTracker.CoverService`: sunucu tarafındaki kapak sağlayıcıları.
- `tests/GameTracker.Tests`: bağımsız console kontrolleri ve lisanslı fixture'lar.
- `tests/GameTracker.Desktop.Tests`: Windows üzerinde bellekte çalışan tekrarlı ızgara kaydırma/filtre/resize kontrolleri.

Kontrol sonuçları ve sınırları [DOGRULAMA.md](DOGRULAMA.md), teknik tasarım [TEKNIK-ANALIZ.md](TEKNIK-ANALIZ.md) içindedir. Proje alpha aşamasındadır.
