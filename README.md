# PhoneBridge

Przesyłanie plików między Windows 11 a Androidem 13+ w sieci lokalnej, bez chmury i bez kont.

- **Komputer → telefon:** prawy przycisk na pliku lub folderze → *Pokaż więcej opcji* → **Wyślij na telefon** (albo *Wyślij do* → *Telefon (PhoneBridge)*).
  - z klawiatury: **Ctrl+Alt+P** wysyła pliki zaznaczone w aktywnym oknie Eksploratora (poza Eksploratorem otwiera okno wyboru plików); w menu kontekstowym **Shift+F10 → T**.
- **Telefon → komputer:** w dowolnej aplikacji *Udostępnij* → **Wyślij na komputer**.
- Odbiorca widzi okno z pytaniem o zgodę i wyborem miejsca zapisu. Na obu urządzeniach można włączyć autoakceptację do wybranego folderu (na telefonie: *Odbieraj bez pytania*).
- Urządzenia same znajdują się w sieci. Parowanie jest jednorazowe: porównujesz ten sam 6-cyfrowy kod na obu ekranach.
- Bez limitu kierunku i rozmiaru plików; każdy plik jest weryfikowany sumą SHA-256 i nigdy nie nadpisuje istniejących plików.

Szczegóły protokołu i bezpieczeństwa: [docs/protocol.md](docs/protocol.md).

## Jak to działa

```text
Windows (agent w zasobniku)                      Android
┌─────────────────────────────┐   TLS (mTLS,    ┌──────────────────────────┐
│ serwer HTTPS + WebSocket    │◄──przypięte ────│ usługa pierwszoplanowa   │
│ :47800, discovery UDP :47801│   certyfikaty)  │ (stałe powiadomienie)    │
│ menu kontekstowe → IPC      │                 │ „Udostępnij” → upload    │
│ okna akceptacji i postępu   │                 │ powiadomienie → download │
└─────────────────────────────┘                 └──────────────────────────┘
```

Komputer jest serwerem, telefon klientem. Telefon utrzymuje połączenie z komputerem, więc komputer może w każdej chwili zaproponować mu plik, a telefon nie musi niczego nasłuchiwać (Android ogranicza pracę w tle).

## Wymagania

- Windows 11 (x64), .NET 10 SDK do budowania.
- Android 13 lub nowszy.
- Oba urządzenia w tej samej sieci Wi-Fi/LAN, oznaczonej w Windows jako **prywatna**.

## Budowanie

```powershell
# Windows: aplikacja + testy
cd windows
dotnet test PhoneBridge.Tests
dotnet publish PhoneBridge.App -c Release -r win-x64 --self-contained false -o ..\dist\windows

# Android: testy + APK (wymaga Android SDK, ścieżka w android/local.properties)
cd android
.\gradlew.bat testDebugUnitTest assembleDebug
# APK: android/app/build/outputs/apk/debug/app-debug.apk
```

## Pierwsze uruchomienie

1. **Windows:** uruchom `PhoneBridge.exe`. Przy pierwszym starcie:
   - zgódź się na dodanie reguły Zapory Windows (jedno okno UAC; reguła obejmuje tylko sieci prywatne i lokalną podsieć),
   - aplikacja doda wpis „Wyślij na telefon” do menu kontekstowego, skrót w „Wyślij do” i autostart przy logowaniu (wszystko per użytkownik, bez admina).
2. **Android:** zainstaluj APK, otwórz PhoneBridge, zezwól na powiadomienia. Na liście pojawi się komputer, stuknij go.
3. Na obu ekranach pojawi się ten sam 6-cyfrowy kod. Potwierdź na komputerze i na telefonie.

Odinstalowanie integracji z Windows: `PhoneBridge.exe --unregister` (usuwa menu kontekstowe, skrót i autostart; regułę zapory usuń poleceniem `netsh advfirewall firewall delete rule name="PhoneBridge"` jako administrator).

## Ograniczenia (świadome)

- **Telefon musi mieć włączone „Utrzymuj połączenie w tle”**, aby odbierać pliki z komputera. Wtedy w pasku widać stałe powiadomienie PhoneBridge. Niektórzy producenci (Xiaomi, Huawei, Samsung) agresywnie zamykają aplikacje w tle; w aplikacji jest przycisk wyłączenia optymalizacji baterii.
- Jeśli telefon jest niedostępny, wysyłka z komputera czeka w kolejce do jego połączenia (do zamknięcia agenta).
- Przerwany transfer trzeba wysłać ponownie (wznawianie od miejsca przerwania: planowane).
- W Windows 11 wpis menu jest w „Pokaż więcej opcji” (nowe menu wymaga podpisanej paczki MSIX).
- Gdy sieć blokuje rozgłaszanie (izolacja klientów, Wi-Fi dla gości), adres IP komputera trzeba wpisać ręcznie.

## Struktura

```text
docs/protocol.md            kontrakt protokołu v1
windows/PhoneBridge.Core    serwer, parowanie, transfery, sanityzacja nazw (testowalne bez UI)
windows/PhoneBridge.App     agent WPF: zasobnik, menu kontekstowe, okna
windows/PhoneBridge.Tests   testy jednostkowe i integracyjne (prawdziwy mTLS na loopbacku)
android/app                 aplikacja Kotlin
scripts/make-icon.ps1       generuje ikonę Windows
```

## Licencja

GPL-3.0-or-later.
