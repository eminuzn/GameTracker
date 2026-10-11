# Doğrulama

Son kontrol: 11 Ekim 2026. Windows, .NET SDK 10.0.401 / runtime 10.0.12. Bu belge kaynakların doğrulamasını anlatır; kişisel dosya yolları, oyun envanteri, veritabanı veya tanılama çıktıları repoya dahil değildir.

## Tekrarlanabilir kontroller

```powershell
dotnet build GameTracker.sln -c Release -m:1
dotnet run --project tests/GameTracker.Tests -c Release --no-build
```

- Release solution build: **0 hata / 0 uyarı**.
- Console harness: **147 kontrol geçti**. Başlık/sequel/sürüm ayrımı, genel exe keşfi ve yardımcı araç dışlama, path/link/klasör kimliği, süreç devri/oturum toparlama, SQLite kalıcılığı, filtre/sıralama tercihleri, Prefetch formatları ve tek seferlik aktarım, kapak fallback/cache/manuel import ve silme sınırları kapsanır.
- Testler kendilerinin oluşturduğu geçici klasör ve veritabanlarını kullanır. Windows kimlik/silme ve native Prefetch decompression kontrolleri Windows gerektirir. Diğer işletim sistemlerindeki atlamalar harness çıktısında belirtilir.
- HTTP sağlayıcı testleri kontrollü fixture yanıtları kullanır; bütün gerçek sağlayıcıların canlı doğrulandığı anlamına gelmez.

İsteğe bağlı, salt okunur oyun tanılaması:

```powershell
dotnet run --project tests/GameTracker.Tests -c Release --no-build -- --inspect-game '<oyun-klasorunun-tam-yolu>'
```

Bu komut exe adaylarını ve kapak akışını inceler; oyunu başlatmaz/silmez. Tanılama veritabanı ve indirilen kapaklar sistemin geçici dizininde ayrı oluşturulur. Çıktı yerel yollar içerebilir; paylaşmadan önce gözden geçirilmelidir.

## WPF kontrolleri

Ayrı geçici kütüphane ile **66 WPF runtime kontrolü** geçti. Bunlar repo dışındaki yerel QA harness'iyle yapıldı; yukarıdaki console komutunun parçası değildir.

- Liste/ızgara geçişi, 3/4 sütun, favoriler, exe filtresi ve boş sonuç mesajı.
- 123 oyunla yalnızca görünür/tampon satırların oluşturulması, container geri dönüşümü, uzak satırlara kaydırma ve filtre sonrası offset sınırı.
- ScrollIntoView, en başa/sona gitme; yatay/dikey özel scrollbar sayfa komutları ve thumb sürükleme.
- Sıralama/yön, eski/geçersiz ayarların uyumluluğu, manuel kapak işlemleri, dropdown ve checkbox görünümü.
- Özel pencere başlığı, native sürükleme/kenar hit testleri, küçültme/büyütme/geri yükleme, ana/ikinci monitör çalışma alanı, minimum 1020×680 boyut ve tray'e kapanma.
- Yeni oyun keşfinde tek seferlik tarih aktarımı ve sonraki taramada tekrar sorgulanmaması. Tarih okuyucu ve kapak istekleri burada fixture kullandı.

Liste ve 3/4 sütun görünümü, minimum pencere boyutu ile görsel olarak incelendi. Uygulama ikonu exe, WPF pencere/görev çubuğu ve tray için ortak çok boyutlu ICO kaynağıdır; ikon eklendikten sonra masaüstü projesi 0 hata/0 uyarıyla derlendi.

## Sınırlar

- Gerçek oyun süreçlerini başlatma, yükseltilmiş süreçlere erişim ve geri dönüşüm kutusu dialog akışları ayrıca kullanıcı denemesi gerektirir. Kalıcı silme yalnızca testin oluşturduğu fixture klasöründe doğrulandı.
- Steam/SteamGridDB canlı arama ve görsel indirme denemeleri yapıldı. IGDB/RAWG/Shopping için gerçek servis anahtarları sağlanmadı; bu kaynaklar kontrollü HTTP testleriyle doğrulandı.
- Prefetch erişimi Windows izinlerine bağlıdır. Okuma engelliyse uygulama geçmiş tarihi boş bırakır; Windows ayarlarını/izinlerini değiştirmez. Public MIT Windows 10 fixture'ı bağımsız beklenen 2016-01-13 18:06:55.3344577 UTC zamanı ile eşleşti; kaynak/lisans `tests/GameTracker.Tests/Fixtures` altında bulunur.
- Farklı DPI ekranlar arasında elle pencere sürükleme ayrıca denenmelidir. Web kapak sağlayıcılarının yanıtları zamanla değişebilir.

Bu doğrulama için kullanıcı oyunları veya uygulamanın asıl veritabanı silinmedi. Repo kaynak kodunu içerir; installer/publish paketi içermez.
