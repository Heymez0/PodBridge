# PodBridge

Windows'un tüm sesini HomePod ve HomePod mini'ye AirPlay 2 ile aktarır. Sürücü kurmaz, görev çubuğunda çalışır.

## Kullanım

1. `PodBridge.exe` dosyasını aç. Görev çubuğunun sağında bir simge belirir.
2. Simgeye tıkla ve HomePod'unu seç. Bir kez daha tıklayınca bağlantı kapanır.
3. `Ctrl+Alt+P` son kullanılan HomePod'u açıp kapatır.

Bağlıyken Windows ses tuşları ve görev çubuğundaki ses kaydırıcısı doğrudan HomePod'u kontrol eder.
Bağlantıyı kapatınca bilgisayarın eski ses seviyesi ve sessizlik durumu geri gelir.

İlk bağlantıda Windows bir kez yönetici onayı ister. HomePod saat eşitleme paketlerini UDP ile gönderdiği için
yalnızca bu program ve yalnızca yerel ağ için bir güvenlik duvarı kuralı eklenir.

## Gecikme ayarları

Ölçüm: Windows çıkışına verilen tık sesiyle HomePod mini'den çıkan ses arası, bilgisayar mikrofonuyla, Wi-Fi üzerinde.

| Ayar | Tampon | Uçtan uca gecikme |
|---|---|---|
| Düşük | 120 ms | yaklaşık 420 ms (ölçüldü) |
| Dengeli | 200 ms | yaklaşık 570 ms (ölçüldü) |
| Kararlı | 1000 ms | yaklaşık 1,4 sn (tahmin) |

Tampon sürelerini panelde "Gecikme" başlığının yanındaki **Düzenle** ile değiştirebilirsin (50 ile 2000 ms arası).
Etkin ayarın süresi değişirse akış kısa süreliğine yeniden kurulur.

Ses kesilir veya cızırtı olursa bir üst ayara geç. 50 ms tamponla yaklaşık 270 ms ölçüldü ama cızırtı duyuldu:
HomePod paketleri en az 70 ms önceden istiyor ve Wi-Fi gecikmesi dalgalanıyor.

## Davranış

- Ağ koparsa veya bilgisayar uykudan dönerse bağlantı kendiliğinden yeniden kurulur.
- iPhone gibi başka bir cihaz HomePod'u devralırsa PodBridge geri almak için uğraşmaz, bildirim gösterir.
- Windows'ta varsayılan ses aygıtı değişirse yakalama yeni aygıta geçer.
- Uygulama çökerse bilgisayar hoparlörünün durumu bir sonraki açılışta geri yüklenir.

## Bilinen sınırlar

- Windows ses listesinde ayrı bir aygıt olarak görünmez; bunun için imzalı çekirdek sürücüsü gerekir. Varsayılan çıkışa giden ses yakalanır.
- Çift HomePod stereo eşleşmesi ve çoklu oda sınanmadı.
- Yalnızca HomePod listelenir. Apple TV ve Mac PIN gerektiren başka bir eşleştirme kullanır.
- Görüntüyle tam eşzamanlı değildir. Oyun ve video için hissedilir bir gecikme kalır.
- Uygulama imzasızdır; Windows ilk açılışta uyarı gösterebilir.

## Geliştirme

.NET 10 SDK gerekir.

```bash
dotnet publish PodBridge.csproj -c Release -o dist
```

Sınama komutları:

```bash
PodBridge --selftest
PodBridge --test --ip 192.168.1.50 --seconds 30 --latency 2205 --loopback
PodBridge --measure --ip 192.168.1.50 --latency 2205 --volume 25
```

Günlük: `%LOCALAPPDATA%\PodBridge\log.txt`
