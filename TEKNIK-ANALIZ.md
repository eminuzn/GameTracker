# GameTracker — Teknik analiz ve geliştirme planı

Tarih: 11 Ekim 2026 · Durum: Referans tasarım v0.3 · Alpha 0.1 kaynakları oluşturuldu. Güncel uygulama kapsamı README.md, kontroller DOGRULAMA.md içindedir.

## 1. Ürün tanımı ve varsayımlar

GameTracker, bilgisayarda kurulu oyunları herhangi bir mağaza hesabına bağlanmadan keşfeden, çalıştıran ve kullanım bilgilerini yerelde saklayan bir masaüstü launcher olacak. Oyunun hangi kaynaktan edinildiği keşif ve takip mekanizmasını değiştirmez. İndirme, crack uygulama veya DRM değiştirme bu ürünün işlevleri arasında değildir.

İlk sürüm için Windows 11 x64 varsayılmıştır. Linux desteği istenirse UI ve işletim sistemi adaptörleri yeniden değerlendirilmelidir; Windows süreç takibi Linux/Wine'a doğrudan taşınamaz.

Ana kural: Kullanıcının seçtiği ana dizinin her doğrudan alt klasörü bir oyun kaydıdır. Alt klasörler yeni oyun sayılmaz; mevcut oyunun exe ve boyut taramasına dahildir.

Örnek: `D:\Oyunlar\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe` için oyun klasörü `D:\Oyunlar\Cyberpunk 2077` olur. Başlatılacak exe derinde olabilir; silinecek dizin daima oyun klasörüdür. Ana dizindeki tek başına exe dosyaları ilk sürümde otomatik oyun sayılmaz.

## 2. İlk sürümün kapsamı

| Özellik | Davranış |
|---|---|
| Ana klasör seçimi | Yerel klasör seçici; birden çok oyun kökü desteklenir |
| Oyun keşfi | Her doğrudan alt klasör için tek kayıt; içerisi recursive taranır |
| Exe belirleme | Adayları puanla; belirsizlikte kullanıcı seçimi iste |
| Elle düzeltme | Exe, oyun adı, çalışma dizini ve argümanlar düzenlenebilir |
| Oyun listesi | Liste/ızgara geçişi; ızgarada 3/4 sütun; isim, exe durumu, boyut, son oynama ve favoriler; exe eksikliği filtresi |
| Oyun detayları | Tam klasör ve exe yolu, boyut güncelleme zamanı, oturum geçmişi |
| Başlatma | Seçili exe'yi kayıtlı çalışma dizini ve argümanlarla aç |
| Takip | Launcher çalışırken oyun süreçlerini izle; son oynama ve süreyi kaydet |
| Yenileme | Manuel tarama + değişiklik bildirimlerinden sonra arka plan taraması |
| Silme | Seçili oyunun klasörünü kullanıcı onayından sonra kaldır |
| Listeden kaldırma | Dosyaları silmeden gizle; tekrar taramada kendiliğinden geri ekleme |

MVP'de hesap, sunucu, bulut senkronizasyonu, mağaza entegrasyonu, oyun güncelleme/indirme veya kayıt dosyası yedekleme yok. Oyun adı başlangıçta klasör adından, ikon exe'den gelir. Kapak ilk keşifte ve yeni oyun eklenince otomatik bulunur; kaynak sırası bölüm 14'te tanımlıdır. Kullanıcı isterse kapağı değiştirebilir.

## 3. Teknoloji kararı

| Alan | Öneri | Gerekçe |
|---|---|---|
| Runtime | .NET 10 LTS / C# | Uzun destek süresi; mevcut C# deneyimi; Windows API erişimi |
| Masaüstü UI | WPF + MVVM | Yerel klasör seçici, tray ve Windows süreçleriyle uyumlu geliştirme |
| MVVM | CommunityToolkit.Mvvm | Observable durum ve async komutlarda tekrarları azaltır |
| Depolama | SQLite + EF Core | Yerel, sunucusuz; ilişkiler, migration ve oturum sorguları |
| OS işlemleri | System.IO, System.Diagnostics ve sınırlı Win32 interop | Tarama, başlatma, süreç yolu ve güvenli dosya işlemleri |
| Log | Microsoft.Extensions.Logging + yerel dosya sağlayıcısı | Tarama, başlatma ve silme hatalarını teşhis |
| Dağıtım | Windows x64 self-contained paket | Kullanıcıdan ayrıca .NET kurulumu istememek |

Bu teknoloji seçimi bir tasarım önerisidir. WPF yalnızca Windows'ta çalışır. Linux baştan gerekli olursa Avalonia değerlendirilebilir; ortak uygulama çekirdeği korunur, OS adaptörleri ayrı yazılır. Electron/Tauri da mümkün, ancak bu kapsam için web UI yanında ikinci teknoloji katmanı eklemeye gerek görmüyorum.

Geliştirme başlangıcında paket sürümleri uyumlulukları kontrol edilerek sabitlenmeli. MVP'de mikroservis, RabbitMQ, Hangfire veya oyun takibi için ayrı HTTP backend gerekli değil. Kullanıcıdan API anahtarı istemeden otomatik kapak sağlamak için küçük bir metadata proxy servisi ayrı bileşen olarak değerlendirilecek.

## 4. Mimari ve hedef proje yapısı

Tek masaüstü uygulaması içinde UI, kullanım senaryoları ve Windows/disk adaptörleri ayrılır. Arka plan işleri cancellable çalışır; UI thread üzerinde klasör taraması yapılmaz.

```text
GameTracker/
  TEKNIK-ANALIZ.md
  GameTracker.sln                         [geliştirmede oluşturulacak]
  src/
    GameTracker.Desktop/                 WPF ekranları, ViewModel, tray
    GameTracker.Core/                    Modeller, kullanım senaryoları, arayüzler
    GameTracker.Infrastructure/          SQLite, dosya tarama, Windows adaptörleri
  tests/
    GameTracker.Core.Tests/              Exe seçimi ve oturum kuralları
    GameTracker.Infrastructure.Tests/    Gerçek geçici klasör ve silme sınırları
```

Temel bileşenler: LibraryScanner, ExecutableResolver, SizeCalculator, GameLauncher, ProcessMonitor, SessionTracker, GameDeletionService ve SQLite repository. Tarama işleri sınırlı eşzamanlılıkla çalışır; silme ve başlatma aynı oyun üzerinde seri hale getirilir.

Veri dosyası `%LocalAppData%\GameTracker\gametracker.db` içinde tutulur; oyun klasörüne veya uygulama kurulum dizinine yazılmaz. İkon önbelleği ve loglar ayrı alt klasörlerdedir. Tek instance çalışır; ikinci açılış mevcut pencereye yönlendirilir.

## 5. Oyun keşfi ve exe tahmini

1. Seçilen kökü normalize et, erişilebilirliğini ve fiziksel klasör kimliğini kaydet. Aynı kökü veya iç içe kökleri ikinci kez eklemeyi engelle.
2. Her doğrudan alt klasör için kalıcı GameId oluştur veya mevcut kaydı bul.
3. İçeriği dosya akışıyla enumerate et; tüm dosya listesini RAM'e yükleme. Exe adayları ve boyut aynı geçişte toplanabilir.
4. Reparse point/junction/symlink üzerinden başka dizinlere geçme. Erişilemeyen alt dizinleri kaydet; bir hata tüm oyunların taramasını durdurmasın.
5. Exe adaylarının adı, göreli yolu ve FileVersionInfo alanlarından sinyal çıkar. Taramada hiçbir exe çalıştırılmaz.
6. Güven yüksekse önerilen exe'yi kaydet; belirsizse oyunu `NeedsExecutable` veya `NeedsReview` durumunda listele.

### Başlangıç puanlama önerisi

| Sinyal | Önerilen ağırlık |
|---|---:|
| Normalize exe adı oyun adıyla tam eşleşiyor (Unreal runtime suffix'i hariç) | +70 |
| Normalize exe adı oyun klasörüne benziyor | +35 |
| ProductName/FileDescription oyun adına benziyor | +20 |
| `bin`, `binaries`, `win64`, `x64` altında | +15 |
| Unreal tarzı `*-Win64-Shipping.exe` | +15 |
| Exe yanında oyun verisi/engine dosyaları var | +10 |
| Exe kök oyun dizininde | +5 |
| Launcher/bootstrapper adı | -10 |
| uninstall/setup/redist/crash reporter/patch/yama kaldırma/benchmark aracı | -150 ve otomatik seçim dışında |
| `_CommonRedist`, `redist`, `redistributables`, `support`, `installer`, `tools`, `CrashReporter`, `BattlEye` dizini | -150 ve otomatik seçim dışında |

Bu skor bir doğruluk yüzdesi değildir. En yüksek skor ≥60 ve ikinci adaydan farkı ≥20 ise otomatik öneri; yardımcı araçlar bu karşılaştırmaya alınmaz ama elle seçilebilir. Tam eşleşme ile kısmi benzerlik puanları birlikte eklenmez. Eşikler korunmuştur; gerçek AlanWake2 örneğinde ana exe 75, yama kaldırma aracı −110 puan alır. Dosya boyutu tek başına seçim kriteri olmaz.

Birden fazla güçlü aday varsa isim, göreli yol ve tahmin nedeni gösterilir. Hiç aday yoksa “Bu oyunun exe'si nerede?” dosya seçici açılır. İlk sürümde exe oyun klasörü sınırları içinde olmalı; dış launcher bağımlılığı ayrı ileri özellik sayılır. Kullanıcı isterse seçimi sonraya bırakabilir.

Kullanıcının elle seçtiği exe sonraki taramalarda değiştirilmez. Exe kaybolursa seçim geçersiz olarak işaretlenir ve yeniden sorulur. Oyun klasörü taşınırsa otomatik isim eşleştirmesiyle geçmiş aktarılmaz; kullanıcı “Klasörü yeniden bağla” işlemiyle aynı GameId'yi korur. Klasör adı aynı olsa bile farklı oyun olabilir.

## 6. Boyut hesaplama ve yenileme

MVP'de `LogicalSizeBytes = sum(FileInfo.Length)` hesaplanır. Bu, dosyaların toplam boyutudur; diskte gerçekten tahsis edilen alan ile her durumda aynı değildir. UI bunu “Dosya boyutu” olarak adlandırır. GB = byte / 1.000.000.000; GiB seçilirse byte / 1.073.741.824 kullanılır ve birim doğru yazılır.

Sıkıştırılmış/sparse dosyalar, hardlink ve cluster tahsisi nedeniyle gerçek disk kullanımı ayrı geliştirme ister. Sonraki sürümde allocation API'leri ve volume + file ID ile hardlink tekilleştirme değerlendirilir. Gösterilen toplamı “sildiğinde kesin boşalacak alan” olarak sunmayız.

Boyut taraması ilk ekleme, manuel yenileme, ilgili klasör değişikliği ve oyun kapandıktan sonra yapılır. Her ekran açılışında baştan hesaplanmaz. Boyutla birlikte hesaplama zamanı, eksik tarama ve atlanan dosya sayısı tutulur. Eksik taramada sayı “en az … GB; bazı dosyalara erişilemiyor” şeklinde gösterilir.

FileSystemWatcher yalnızca yenileme sinyalidir. Bildirimler kısa süre birleştirilir; buffer overflow veya watcher hatasında tam tarama planlanır. Açılışta da uzlaştırma taraması gerekir. Yarım kurulan oyunlar görünür ancak exe seçimi ve boyutun değişmekte olduğu belirtilir. Offline harici disk `RootUnavailable` olur; oyun kayıtları silinmez.

## 7. Başlatma ve oynama takibi

### Başlatma

Başlatmadan önce kök, oyun klasörü ve exe'nin halen aynı sınırlar içinde olduğunu kontrol et. Varsayılan WorkingDirectory exe'nin bulunduğu dizindir; kullanıcı değiştirebilir. Mutlak exe yolu kullanılır, argümanlar ayrı taşınır; `cmd.exe /c` gibi shell komutu üretilmez. Normal başlatmada `UseShellExecute=false` düşünülebilir; elevation isteyen oyunlar ayrı, kullanıcıya görünen bir akış gerektirir. Launcher normal kullanıcı haklarıyla çalışır.

`LaunchRequestedAtUtc` ile gerçek `SessionStartedAtUtc` ayrıdır. Başlatma hatası “son oynama” tarihini değiştirmez. Sürecin gerçekten oluşturulduğu/gözlendiği anda oturum başlar; birkaç saniyede kapanan oturum kısa/başarısız başlangıç olarak işaretlenebilir. Oyunun menüye ulaştığını evrensel olarak doğrulamak mümkün değildir.

### Süreç izleme

Yalnızca `Process.WaitForExit()` yeterli değil: ilk exe ikinci exe'yi başlatıp çıkabilir. MVP'de launcher tray'de çalışırken 2 saniyelik process snapshot alınır. PID + süreç başlangıç zamanı birlikte kimlik olarak kullanılır; PID tekrar kullanılabilir. Exe tam yolu, süreç başlangıcı ve mümkün olduğunda parent ilişkisiyle eşleştirme yapılır. WMI süreç olayları ileride eklenebilir; polling uzlaştırması korunur.

Kayıtlı exe ve kullanıcıca onaylanmış runtime exe'leri güçlü eşleşmedir. Başlatma penceresinde oyun klasörü içindeki yeni child süreçler yardımcı sinyaldir. Klasör içindeki her süreç otomatik “oyun çalışıyor” sayılmaz; crash reporter ve updater oturumu uzatmamalıdır. İlk exe kapanınca runtime bulunamazsa kullanıcıdan “takip edilecek oyun exe'si” ayrıca seçmesi istenir. Başlatma exe'si ve takip exe'si farklı olabilir.

Windows API'leri bazı süreçlerin yoluna erişimi engelleyebilir; tam süreç takibi her oyun için garanti edilemez. Kullanıcıya `TrackingUnavailable` durumu gösterilir; sırf takip için launcher'ın tamamı otomatik yönetici yapılmaz. Launcher üzerinden dışarıda başlatılan kayıtlı oyun da launcher açıkken yakalanabilir; önceden geçen süresi çıkarılamaz.

Oturum, onaylı oyun süreçlerinden en az biri çalıştığı sürece sürer. Son süreç kaybolduğunda kısa bir handoff beklemesi (başlangıç önerisi 10 saniye) uygulanır; son görülme zamanı bitiş kabul edilir, bekleme süreye eklenmez. Aynı oyunun paralel süreçleri ayrı ayrı süre toplamına eklenmez. Ölçüm foreground/aktif oynama değil, oyun sürecinin çalışma süresidir.

### Zaman ve kapanış

“Son oynama” en son gözlenen oturum başlangıcıdır. Yeni oyun keşfinde hiçbir launcher tarihi/oturumu yoksa, tam exe kimliğiyle eşleşen Windows Prefetch son çalıştırması bir kez başlangıç tarihi olarak kullanılabilir. Sonraki gözlenen oturum bu değeri günceller. UTC kaydedilir, UI yerel saatle gösterir. Canlı süre monotonic sayaçla ölçülür; saat değişikliklerinden etkilenmez. Uyku/hibernate aralığı süreye katılmaz; uyandığında süreçler yeniden kontrol edilir.

Her 15 saniyede kalıcı heartbeat/checkpoint yazılır. Çökme veya elektrik kesintisinden sonra açık oturum son checkpoint'te `Interrupted` olarak kapatılır; uygulamanın kapalı olduğu tüm aralık oynandı varsayılmaz. Devam eden oyun yeniden gözlenirse yeni recovery oturumu açılır. Sürelerin polling/checkpoint hassasiyeti UI'da kesin saniye garantisi olarak sunulmaz.

Pencerenin X düğmesi varsayılan olarak tray'e küçültür. “Tamamen çık” izlemeyi bitirir; çalışan oyunu kapatmaz. Launcher kapalıyken takip yapılmaz. Otomatik başlangıç isteğe bağlıdır. Dosya erişim/değişim zamanları geçmiş oynama olarak kullanılmaz. Kullanıcının güncel kararıyla yalnızca yeni keşfedilen oyunlarda bir defalık Prefetch aktarımı vardır; kayıt bulunmazsa başlangıç değeri “Henüz takip edilmedi” olur. Mevcut oyunlar, tekrar tarama ve açılışlar yeni Prefetch denemesi oluşturmaz. Geçmiş toplam süre/oturumlar üretilmez.

## 8. Oyun klasörünü silme

Silinecek hedef exe'nin parent dizini değil, kayıtlı doğrudan oyun klasörüdür. Örneğin `Cyberpunk 2077\bin\x64` değil, `Cyberpunk 2077` silinir.

1. UI oyun adı, tam hedef yol, boyut ve klasörün tüm içeriğinin kaldırılacağını gösterir. “Listeden kaldır” ayrı işlemdir.
2. Aktif oyun süreçleri varsa silme engellenir; oyun zorla kapatılmaz.
3. Hedefin kayıtlı kökün doğrudan çocuğu olduğunu path segmentleri ve fiziksel kimlik üzerinden tekrar doğrula. Sadece string `StartsWith` yeterli değildir: `D:\Oyunlar2` başka bir dizindir.
4. Kök, disk kökü, uygulama/veri klasörü ve korunan sistem klasörleri hedef olamaz. Kök olarak seçilen riskli sistem konumlarında silme özelliği kapalıdır.
5. Reparse point olan kök/hedef veya araya sonradan eklenmiş link varsa işlem reddedilir. İç linkler üzerinden dış hedefe gidilmez. Kontrol ile silme arasındaki değişim riski nedeniyle işlem öncesi kimlik tekrar doğrulanır; eşzamanlı düşmanca dosya sistemi değişikliklerine mutlak garanti verilmez.
6. Tercih edilen işlem Windows IFileOperation ile geri dönüşüm kutusuna taşımadır. Bu, disk alanını kutu boşaltılana kadar geri vermeyebilir. Kalıcı silme ayrı açık seçenek ve onaydır; kullanıcının alan açma amacı burada karşılanır.
7. Geri dönüşüm mümkün değilse sessizce kalıcı silmeye geçilmez. İptal/hata durumu korunur. Doğrudan kalıcı işlem sadece açık seçimin ardından yapılır.
8. Dosya işleminin gerçek sonucu kontrol edilir. Kısmi silmede kayıt `DeleteFailed` kalır, kalan dosya boyutu yenilenir. Hedef kalkınca `Deleted` işaretlenir; geçmiş ve oturumlar korunur.

Silme ve aynı oyunun başlatılması/taranması uygulama içinde kilitlenir. Yetki hatasında otomatik elevation veya izin değiştirme yapılmaz. Documents/AppData içindeki oyun save dosyaları otomatik silinmez; oyun klasörünün içinde bulunan kayıt dosyaları ise klasörle birlikte silinir. Bu özellik bir uninstaller değildir; registry ve kurulu redistributable'ları kaldırmaz.

## 9. Veri modeli

| Tablo | Temel alanlar |
|---|---|
| LibraryRoots | Id, Path, NormalizedPath, VolumeIdentity, DirectoryIdentity, Availability, LastScanAtUtc |
| Games | Id, RootId, RelativeFolderPath, FolderIdentity, DisplayName, LaunchExeRelativePath, WorkingDirectoryRelativePath, Arguments, SelectionSource, ResolutionState, AvailabilityState, LogicalSizeBytes, SizeState, SizeCalculatedAtUtc, LastPlayedAtUtc, IsHidden |
| ExecutableCandidates | Id, GameId, RelativePath, Score, Reasons, LastSeenAtUtc |
| TrackedExecutables | Id, GameId, RelativePath, SelectionSource |
| PlaySessions | Id, GameId, Source, StartedAtUtc, EndedAtUtc, LastHeartbeatAtUtc, DurationMs, Status, EndReason |
| DeletionOperations | Id, GameId, TargetPathSnapshot, RequestedAtUtc, Mode, Status, Error |
| Settings | Key, Value; tema, tray, otomatik başlangıç ve boyut birimi |

GameId GUID olur, yoldan türetilmez. Yol benzersizliği kök + normalize göreli yol üzerinden sağlanır. Göreli yollar taşıma/yeniden bağlama için kullanılır; çalıştırma anında kökle birleştirilip sınır kontrolü tekrar yapılır. Exe seçim durumu, klasör erişilebilirliği ve oturum durumu ayrı tutulur; tek enum her kombinasyonu temsil etmeye çalışmaz.

Toplam süre tamamlanmış ve kesintili oturumların DurationMs toplamından üretilir; cache tutulursa transaction içinde güncellenir. Son oynama canlı takipte oturumla aynı transaction içinde değiştirilir; yeni keşfin bir defalık Prefetch aktarımı ayrı kayıt güncellemesidir. `InitialPlayHistoryPending` eski kayıtlarda varsayılan false, yeni keşifte true olur ve denemeden önce kalıcı olarak false'a alınır. `LastPlayedFromPrefetch` tarih kaynağını belirtir; canlı takip başladığında false olur. Açık oturumlar yeniden açılışta uzlaştırılır. Log ve silme geçmişi sınırlı saklama politikasına sahip olmalıdır.

## 10. Ekranlar

**İlk açılış:** Ana oyun klasörünü seç → keşif ilerlemesini gör → belirsiz exe seçimlerini tamamla veya ertele → kütüphaneye geç.

**Kütüphane:** Arama; isim/son oynama/boyut sıralaması; toplam oyun sayısı ve hesaplanmış toplam boyut. Her kartta ikon, isim, boyut, son oynama ve Oyna düğmesi. Belirsiz kayıtta “Exe seç”, erişilemeyende “Klasör bulunamadı” gösterilir.

**Detay:** Oyna, exe değiştir, takip exe'si seç, klasörü aç, boyutu yenile, yeniden bağla, listeden kaldır, oyunu sil. Son oturumlar ve toplam çalışma süresi gösterilir. Exe seçim ekranı aday nedenlerini ve göreli yolu gösterir.

## 11. Geliştirme sırası ve kabul ölçütleri

| Aşama | Teslim | Tamamlanma ölçütü |
|---|---|---|
| 1 — Temel | WPF shell, SQLite, kök seçimi, manuel oyun/ exe | Kapanıp açılınca kayıtlar korunur; oyun doğru working directory ile başlar |
| 2 — Keşif | Alt klasör tarama, boyut, exe skorları ve kullanıcı seçimi | Aynı tarama duplicate üretmez; belirsiz aday kullanıcıya sorulur |
| 3 — Takip | Snapshot monitor, tray, sessions, checkpoint | Bootstrapper sonrası runtime takip edilir; crash sonrası kapalı aralık sayılmaz |
| 4 — Silme | Klasör sınır kontrolü, recycle/kalıcı mod, hata durumları | Sadece seçili doğrudan oyun klasörü kaldırılır; parent ve kardeşler korunur |
| 5 — Sağlamlaştırma | Watcher, offline root, yeniden bağlama, paket | Büyük kütüphanede UI donmaz; temiz Windows'ta paket çalışır |

İlk geliştirilecek dikey parça: kök seç → bir oyun keşfet → exe'yi onayla → boyutu göster → başlat → kapanınca son oynama kaydet. Exe tahmini ve runtime eşleşmesi gerçek oyun örnekleriyle doğrulanmadan süre ve takvim için kesin taahhüt verilmemelidir.

### Kritik doğrulama senaryoları

- Kök exe, derin bin/x64 exe, iki güçlü aday, yalnızca uninstall.exe, hiç exe olmayan klasör.
- Manuel seçimin tekrar taramada korunması; exe güncellemede silinmesi; Türkçe/boşluklu/uzun yollar.
- Parent exe kapanıp child'ın devam etmesi; farklı runtime exe; crash reporter'ın açık kalması; PID yeniden kullanımı.
- İki oyun aynı anda; aynı oyunun birden fazla süreci; launch hatası; dışarıdan başlatma; uyku/uyanma.
- Harici disk çıkarılması; erişim engeli; watcher overflow; iptal edilen ve yarım kalan tarama.
- `Oyunlar`/`Oyunlar2` sınırı; hedefin ana kök olması; junction/symlink; silme sırasında link değiştirilmesi.
- İptal edilen geri dönüşüm, recycle desteklemeyen konum, açık/kilitli dosya ve kısmi silme.
- Silme testleri yalnızca testin oluşturduğu geçici dizinlerde ve sibling sentinel dosyalarıyla yapılır; gerçek oyun kütüphanesinde otomatik silme testi yapılmaz.

Windows entegrasyon kontrolleri gerçek Windows ortamında yapılmalı. Bu analiz Linux ortamında hazırlanmıştır; launcher veya Windows API davranışları burada çalıştırılıp doğrulanmadı.

## 12. Açık kararlar

İlk uygulamada Windows 11 x64, yerel kütüphane, birden fazla kök, tray açıkken takip ve manuel runtime eşleştirme esas alınır. Kullanıcının sonraki tercihiyle değişebilecek kararlar: Linux desteği, kapak/tasarım stili, çalışma süresi yerine foreground aktif süre, taşınabilir veri modu ve kalıcı silmenin varsayılan olup olmayacağı. Bunlar analizi hazırlamayı engellemez.

## 13. Resmî teknik kaynaklar

Kaynaklar 11 Ekim 2026 tarihinde kontrol edildi. Mimari, eşikler, polling ve checkpoint değerleri bu projeye özel tasarım önerileridir; Microsoft belgelerinin doğruluk garantisi değildir.

- .NET destek politikası: https://dotnet.microsoft.com/en-us/platform/support/policy
- WPF kapsamı ve Windows bağımlılığı: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/
- Win32_Process, parent PID ve CreationDate: https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-process
- FileSystemWatcher buffer taşması: https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.internalbuffersize?view=net-10.0
- FileInfo.Length: https://learn.microsoft.com/en-us/dotnet/api/system.io.fileinfo.length
- Diskte kullanılan alan için GetCompressedFileSizeW: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getcompressedfilesizew
- IFileOperation: https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation
- Geri dönüşüm ve dosya işlem bayrakları: https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags


## 14. Otomatik kapak bulma — kabul edilen ürün kararı

Kapak için kaynak sırası **Steam → SteamGridDB → IGDB → RAWG → Google Shopping → placeholder** olur. Steam bir mağaza kataloğudur; SteamGridDB ayrı bir görsel veritabanıdır ve Steam dışındaki oyunlar için de aranır. Kullanıcının mağaza hesabı bağlaması veya oyuna o mağazada sahip olması eşleştirmenin koşulu değildir. Google Shopping son alternatif olarak oyun/sürüm adıyla eşleşen ilk kullanılabilir ürün görselini seçer; servis tarafındaki SerpApi anahtarıyla çalışır. Anahtar yapılandırılmamışsa atlanır; kullanıcı manuel kapak seçebilir veya tarayıcıda Shopping aramasını açabilir.

Steam'de güvenilir oyun eşleşmesi yoksa veya eşleşen oyunun kullanılabilir kapağı bulunamazsa sıradaki sağlayıcıya geçilir. Alternatif sağlayıcıların her birinde aynı kural uygulanır. Steam katalog araması ve kapak erişimi için kullanılacak belgelenmiş/izin verilen teknik yöntem geliştirme öncesi doğrulanacak; tahmini CDN URL'leri kalıcı API sözleşmesi kabul edilmeyecek.

### Otomatik akış

1. Klasör adı ve exe ürün bilgisinden aday oyun adı çıkar. Sürüm/repack/dağıtım eklerini temizle; devam oyunu numarası, remake/remastered ayrımı ve anlamlı alt başlığı koru.
2. Kaynakları sırayla sorgula. Güvenilir kimlik eşleşmesi olmadan ilk arama sonucunu kullanma. DLC, demo ve aynı adlı farklı oyunları ayır.
3. Uygun statik dikey kapak varsa indir, doğrula ve yerel önbelleğe atomik kaydet. UI resim yüklenene kadar placeholder gösterir; tarama ve oyun başlatma beklemez.
4. Eşleşme veya görsel yoksa diğer sağlayıcıya geç. Timeout/429/servis hatası ile gerçek “bulunamadı” sonucunu ayrı tut; geçici hatalar backoff ile tekrar denenebilir.
5. Hiçbir kaynakta güvenilir kapak bulunamazsa oyun adı ve mevcut exe ikonu içeren tutarlı bir placeholder kartı göster. Kullanıcıdan zorunlu seçim veya resim yükleme istenmez.

İlk taramada ve yeni oyun keşfinde sadece eksik kapaklar arka plan kuyruğuna alınır. Yerelde mevcut kapak her taramada indirilmez. Başarısız aramalar sınırlı süre önbelleğe alınır; örneğin 7 gün sonra, oyun adı düzeltildiğinde veya kullanıcı “Kapağı yeniden ara” dediğinde denenir. Kullanıcının elle seçtiği kapak otomatik işlemle değiştirilmez. İnternet yokken mevcut kapaklar ve placeholder ile tüm yerel işlevler devam eder.

### Teknik bileşenler ve kayıtlar

`ICoverProvider` adaptörleri: SteamCoverProvider, SteamGridDbCoverProvider, IgdbCoverProvider, RawgCoverProvider. CoverResolver kaynak sırasını, kimlik eşleşmesini ve fallback'i; CoverFetchQueue sınırlı eşzamanlılık, iptal ve retry'ı yönetir.

`GameArtwork`: GameId, Provider, ProviderGameId, ArtworkId, SourceUrl, LocalCachePath, MatchScore, Status, FetchedAtUtc, LastAttemptAtUtc, NextRetryAtUtc, IsUserOverride, Attribution. MatchScore bir doğruluk yüzdesi değildir. Sağlayıcı kimlikleri sağlayıcıya özeldir; aynı sayı farklı veritabanlarında aynı oyunu temsil etmez.

API anahtarları dağıtılan masaüstü exe'ye ortak secret olarak gömülmez. Kullanıcıdan anahtar istememe hedefi için küçük proxy servisi önerilir; yalnızca temizlenmiş arama adı/sağlayıcı kimliği gönderilir, tam yerel klasör yolları gönderilmez. Servis kimlik bilgileri, kota ve ortak sorgu cache'ini yönetir. Görsel cache/dağıtımı sağlayıcı koşullarına uygun kurulmalıdır. Servis veya internet çalışmasa da launcher yerel oyunları başlatır ve takip eder.

### Kabul ölçütleri

- Steam'de bulunan ve kapağı olan oyun otomatik kapak alır.
- Steam'de olmayan oyun alternatif sağlayıcılarda aranır.
- Oyun eşleşse bile kapak eksikse sıradaki kaynak denenir.
- Hiçbirinde bulunamayan veya belirsiz oyun placeholder alır; kullanıcıya zorunlu soru sorulmaz.
- Yeni oyun kapak kuyruğuna girer; mevcut kapak tekrar indirilmez.
- Offline/429/timeout durumunda UI donmaz ve hatalı oyun kapağı atanmaz.
- Elle seçilmiş kapak korunur; yanlış otomatik eşleşme düzeltilebilir.

Sağlayıcı belgeleri: https://www.steamgriddb.com/api/v2 · https://api-docs.igdb.com/ · https://rawg.io/apidocs
