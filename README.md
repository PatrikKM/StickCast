# 📺 StickCast

**StickCast** je moderní desktopová aplikace pro Windows (.NET 8, WPF / C#) sloužící jako univerzální ovladač a streamovací centrum pro **Smart TV (Samsung DLNA/UPnP)**, **WiFi Display / EZMira / MiraScreen HDMI dongly**, **lokální monitory notebooku či PC** a zrcadlení **Android telefonů** (Xiaomi HyperOS, Motorola Hello UI / Smart Connect).

---

## ✨ Hlavní funkce

### 1. 🎯 Výběr cílového zařízení (TV, HDMI Stick i Monitor notebooku/PC)
- **Automatické vyhledávání v síti (SSDP / UPnP):** Multi-interface M-SEARCH vyhledávání s deduplikací podle UDN a odolným XML parserem pro levné čínské HDMI sticky i Smart TV Samsung.
- **Lokální monitor notebooku / PC jako cílové zařízení (`💻 Tento počítač – Monitor 1…`):** Aplikace funguje i **zcela bez připojené TV** (např. na cestách na notebooku). Všechny připojené monitory se automaticky objeví v horním seznamu **Zařízení** a přehrávají videa, YouTube streamy, titulky, IPTV i fotky ve vestavěném přehrávači s podporou celé obrazovky (`F11` nebo dvojklik).
- **Ruční připojení přes IP:** Možnost zadat IP adresu přijímače napřímo, pokud router blokuje UDP multicast.

### 2. 🌐 YouTube vyhledávání, URL streamy, Historie a Oblíbené
- **Vyhledávání na YouTube s posuvným stránkováním:** Vyhledá 10 výsledků na stránku s tlačítky **`◀ Předchozí`** a **`Další ▶`** (s lokální mezipamětí stránek pro okamžitý návrat zpět).
- **Kompatibilita s TV Samsung i HDMI sticky:** Preferuje progresivní HTTP/HTTPS MP4 streamy a směruje je přes **lokální DLNA HTTP Range proxy** (`/p/{id}/media.mp4`) s plnými DLNA hlavičkami (`contentFeatures.dlna.org`, `transferMode.dlna.org`, `CaptionInfo.sec`) a automatickým `ffmpeg` MPEG-TS fallbackem.
- **🕓 Historie a ⭐ Oblíbené:** Přehledné podzáložky s názvem videa, URL a časem přehrání, změnou pořadí, přímým přehráním dvojklikem a kontextovým menu.
- **📋 Zkopírovat URL & Vložit a přehrát:** Rychlá práce se schránkou jedním kliknutím na všech seznamech (YouTube, Historie, Oblíbené, Playlist).
- **IPTV M3U / M3U8 playlisty:** Automatické rozpoznání seznamu televizních kanálů oproti HLS manifestu a načtení kanálů do playlistu.

### 3. 💬 Automatické české titulky (normální i A.I.) + Vynucené žluté titulky
- **4stupňové automatické získání českých titulků (`YtDlp` + `Subtitles`):**
  1. Ručně nahrané české titulky (`cs`, `cs-CZ`, `cz`).
  2. YouTube A.I. automatické české titulky (`automatic_captions`).
  3. YouTube A.I. automatický překlad do češtiny (`tlang=cs`).
  4. Záložní automatický A.I. dávkový překlad anglických/původních titulků do češtiny + převod WebVTT → SRT s odstraněním duplicitních rolujících řádků.
- **🔥 Vždy žluté, vysoce čitelné titulky:**
  - Při streamování na TV nebo HDMI stick se titulky vypalují přes `ffmpeg` (`libass` `force_style`) **velkým tučným jasně žlutým písmem (`PrimaryColour=&H0000FFFF`) s výrazným černým obrysem a stínem**.
  - Při přehrávání na monitoru notebooku/PC se vykreslují v nativním vysoce kontrastním žlutém překryvu (`#FFFF00`) se zachováním okamžitého přetáčení na časové ose.

### 4. 📁 Playlist lokálních souborů z PC
- Podpora **Drag & Drop** pro videa (`.mp4`, `.mkv`, `.avi`, `.mov`, `.webm`, `.ts`), hudbu (`.mp3`, `.flac`, `.wav`, `.m4a`, `.aac`), fotky (`.jpg`, `.png`, `.webp`, `.gif`, `.bmp`) a `.m3u` playlisty.
- Automatická detekce lokálních `.srt` a `.vtt` titulků ve stejné složce (včetně `.cs.srt`, `.cz.srt`).
- Režimy opakování (`Neopakovat`, `Opakovat vše`, `Opakovat jeden`), náhodné pořadí (`🔀 Náhodně`), slideshow fotek (`5 s` / `10 s`) a gapless přednačítání (`SetNextAVTransportURI`).

### 5. 🖥 Přenos obrazovky PC & 📱 Zrcadlení telefonu
- **Obrazovka PC:** Rychlé vyvolání systémového Miracastu (`Win+K`) nebo živý DLNA H.264 + AAC MPEG-TS stream vybraného monitoru přes `ffmpeg` (`gdigrab` + `dshow`) s pevnou synchronizací hodin (`-use_wallclock_as_timestamps 1`, `-fps_mode cfr`, `aresample=async=1:first_pts=0`).
- **Telefon (Xiaomi 15T Pro HyperOS / Motorola Edge 50 Fusion Hello UI):**
  - Přímý návod pro bezdrátový Miracast na TV.
  - Vestavěné párování a připojení **Bezdrátového ladění (Wireless ADB)** + spuštění **`scrcpy`** jedním klikem pro nízkou latenci.
  - Tlačítko **`🔌 Odpojit ADB (uvolnit Wi-Fi)`** a inteligentní vypínání DLNA pollingu v nečinnosti, aby nedocházelo k rušení Wi-Fi kanálu při přímém zrcadlení telefonu na TV.

### 6. 📌 Mini-ovladač, Tray ikona a Globální zkratky
- Plovoucí **Mini-ovladač** (`Always-on-Top`) s názvem přehrávaného média, stavem, tlačítky `⏮ / ⏯ / ⏹ / ⏭` a hlasitostí.
- Minimalizace do oznamovací oblasti Windows (**System Tray**) s rychlým kontextovým menu.
- Globální klávesové zkratky: `Ctrl+Alt+Mezerník` (Play/Pauza), `Ctrl+Alt+←` (Předchozí), `Ctrl+Alt+→` (Další).

---

## 🏗 Architektura projektu

```text
StickCast/
├── App.xaml / App.xaml.cs          # Vstupní bod WPF aplikace a globální zachytávání výjimek
├── AppSettings.cs                  # Atomické ukládání nastavení, historie a oblíbených do %APPDATA%\StickCast\settings.json
├── MainWindow.xaml                 # Hlavní uživatelské rozhraní (5 záložek + spodní přehrávací lišta + log)
├── MainWindow.xaml.cs              # Inicializace, správa oken, nastavení a životního cyklu
├── MainWindow.Discovery.cs         # SSDP/DLNA vyhledávání v síti + registrace lokálních monitorů PC
├── MainWindow.UrlTab.cs            # Přehrávání URL, YouTube stránkované vyhledávání, Historie a Oblíbené
├── MainWindow.PlaylistTab.cs       # Lokální playlist souborů, IPTV M3U a fotogalerie
├── MainWindow.Playback.cs          # Ovládání přehrávání, přetáčení, hlasitost a chytrý DLNA polling
├── MainWindow.ToolsTab.cs          # Stream obrazovky PC, Wireless ADB + scrcpy, diagnostika sítě a stahování nástrojů
├── LocalMonitorWindow.xaml(.cs)    # Vestavěný přehrávač pro monitory notebooku/PC s vynucenými žlutými titulky
├── MiniPlayerWindow.xaml(.cs)      # Plovoucí kompaktní mini-ovladač
├── TrayIcon.cs                     # Win32 Shell_NotifyIcon integrace do systémové lišty bez závislosti na WinForms
├── Net/
│   ├── HttpClients.cs              # Sdílené, správně nakonfigurované instance HttpClient
│   ├── NetUtil.cs                  # Výběr lokální IPv4 adresy podle cílové podsítě přijímače
│   ├── Ssdp.cs                     # Multi-NIC SSDP M-SEARCH klient s UDN deduplikací
│   ├── DlnaRenderer.cs             # UPnP AVTransport + RenderingControl SOAP klient a lokální monitor adaptér
│   ├── MediaServer.cs              # HTTP/1.1 Range & Keep-Alive server (/m/, /live/, /p/ DLNA proxy) s LRU cache
│   ├── Mime.cs                     # Mapování MIME typů a DLNA.ORG_PN / DLNA.ORG_FLAGS profilů
│   ├── YtDlp.cs                    # Wrapper pro yt-dlp (stránkované hledání, progresivní MP4, titulky)
│   ├── Subtitles.cs                # 4stupňové získávání a A.I. překlad českých titulků, VTT->SRT parser
│   ├── ScreenStream.cs             # Živý H.264 + AAC MPEG-TS stream plochy PC přes ffmpeg
│   ├── TranscodeStream.cs          # Překódování videa v reálném čase s vypálením žlutých titulků (libass)
│   ├── M3uParser.cs                # Parser a generátor M3U / M3U8 IPTV playlistů
│   ├── StickAdmin.cs               # Detekce a správa EZMira / MiraScreen webové administrace (192.168.203.1)
│   ├── NetworkDiag.cs              # Kontrola a konfigurace Windows Firewallu, Wi-Fi hotspotu a portů
│   └── ToolDownloader.cs           # Stahování yt-dlp.exe a ffmpeg.exe s atomickou výměnou souborů
└── Services/
    ├── Win32Helpers.cs             # Enumerace monitorů, simulace Win+K a globální mediální klávesové zkratky
    └── AdbController.cs            # Wrapper pro adb.exe a scrcpy.exe (Wireless ADB pair/connect/disconnect)
```

---

## 🚀 Sestavení a spuštění

### Požadavky
- **Windows 10 / 11 (x64)**
- **.NET 8.0 SDK** (`net8.0-windows`)
- Volitelné externí nástroje (lze je stáhnout **automaticky jedním kliknutím přímo v aplikaci** v záložce `⚙ Stick`):
  - `yt-dlp.exe` – pro vyhledávání a přehrávání z YouTube a automatické titulky
  - `ffmpeg.exe` – pro vypalování žlutých titulků do obrazu na TV, překódování nekompatibilních formátů a DLNA stream obrazovky
  - `scrcpy.exe` & `adb.exe` – pro zrcadlení obrazovky Android telefonu na PC

### Sestavení z příkazové řádky

```powershell
# Sestavení projektu
dotnet build StickCast.csproj -c Release

# Publikace jednoho spustitelného souboru (StickCast.exe)
dotnet publish StickCast.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

---

## 🔒 Bezpečnost
- Lokální mediální HTTP server (`MediaServer`) zpřístupňuje pouze explicitně zaregistrované soubory přes náhodné kryptografické identifikátory s omezenou LRU kapacitou a je chráněn proti *Path Traversal* útokům.
- Veškerá uživatelská nastavení, historie a oblíbené položky se ukládají lokálně do `%APPDATA%\StickCast\settings.json` (mimo repozitář).

---

## 📄 Licence
Tento projekt je licencován pod licencí [MIT](LICENSE).
