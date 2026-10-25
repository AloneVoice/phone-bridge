# PhoneBridge — plan projektu dla Claude Code

> Cel: darmowa aplikacja open source łącząca Windows 11 i Android 13+, działająca w sieci lokalnej bez konta i serwera pośredniczącego. Użytkownik wysyła pliki z menu kontekstowego Windows lub systemowego „Udostępnij” Androida, bez ręcznego uruchamiania interfejsu aplikacji. W kolejnej fazie przegląda udostępnione foldery drugiego urządzenia jak dysk.

## 1. Zasady i ograniczenia

- **MVP: Windows 11 + Android 13+, Wi-Fi/LAN, 1 komputer i 1 telefon**, z możliwością rozszerzenia na wiele urządzeń.
- Brak chmury, telemetrii, kont i obowiązkowego Internetu. Transfer wyłącznie bezpośredni.
- Bez roota, ADB, sterowników Androida i uprawnień administratora Windows w podstawowej wersji.
- Otwarte źródła: licencja **GPL-3.0-or-later** (przed publikacją sprawdzić licencje zależności).
- **Nie obiecywać nieprzerwanej pracy w tle na Androidzie**: ograniczenia Doze, baterii i usług pierwszoplanowych. Zaprojektować stany dostępności i mechanizm ponawiania.
- Aplikacja Android korzysta z systemowego Storage Access Framework (SAF); nie uzyskuje nieograniczonego dostępu do plików.
- Pierwsza wersja ma być mała, bezpieczna i użyteczna, zamiast zawierać wszystkie funkcje naraz.

## 2. Zakres wersji

### MVP (v0.1)

- [ ] Instalator Windows uruchamiający agenta automatycznie po zalogowaniu.
- [ ] Integracja Windows: „Wyślij na telefon” dla plików i folderów, początkowo przez klasyczne menu kontekstowe (Windows 11: „Pokaż więcej opcji”).
- [ ] Aplikacja Android obsługująca `ACTION_SEND` i `ACTION_SEND_MULTIPLE` (systemowe „Udostępnij”).
- [ ] Wykrywanie urządzeń w LAN (mDNS/NSD), z możliwością ręcznego wpisania adresu IP.
- [ ] Bezpieczne parowanie kodem jednorazowym/QR, z weryfikacją tożsamości obu urządzeń.
- [ ] Przesyłanie plików w obu kierunkach, postęp, anulowanie, komunikaty o błędach.
- [ ] Potwierdzenie odbioru i wskazanie folderu docelowego przez użytkownika; zapamiętywanie udzielonych uprawnień.
- [ ] Integralność transferu (SHA-256), ochrona przed nadpisaniem i kolizjami nazw.
- [ ] Kolejka zadań i ponowienie przerwanego transferu (minimum od początku pliku; wznawianie blokowe później).
- [ ] Logi diagnostyczne bez nazw i zawartości prywatnych plików w domyślnym trybie.

### v0.2 — wygoda i stabilność

- [ ] Wznawianie dużych transferów od ostatniego zweryfikowanego bloku.
- [ ] Obsługa wielu urządzeń, wybór urządzenia docelowego w menu.
- [ ] Foldery, wiele plików, drag-and-drop do aplikacji, kolejka i powiadomienia.
- [ ] Automatyczne aktualizacje Windows (podpisane wydania) oraz sprawdzanie nowej wersji APK z systemowym potwierdzeniem instalacji.
- [ ] Testy odporności na uśpienie, zmianę sieci, utratę połączenia, duże pliki i brak miejsca.

### v0.3 — przeglądanie plików i dysk

- [ ] Udostępnianie wybranych folderów komputera i telefonu tylko sparowanym urządzeniom.
- [ ] Przeglądarka zdalnych plików w aplikacji Android, oparta o SAF/DocumentsProvider lub własny interfejs.
- [ ] Przeglądanie plików telefonu w Eksploratorze Windows: najpierw bezpieczny prototyp WebDAV, potem rozważyć WinFsp dla mapowania jako dysk `P:`.
- [ ] Domyślnie **tylko odczyt**; zapis/usuwanie dopiero po osobnym włączeniu i z potwierdzeniem.
- [ ] Obsługa rozłączeń, buforowania, błędów I/O, ograniczeń nazw plików i wielkości plików.
- [ ] Nie zakładać, że Windows WebDAV obsłuży własne uwierzytelnianie i TLS bez dodatkowych testów. Preferować lokalny adapter dysku i bezpieczny protokół aplikacji, zamiast udostępniać niezabezpieczony WebDAV w LAN.

### Poza zakresem na start

- Internet/WAN, przekierowanie portów, serwery relay, synchronizacja dwukierunkowa, iOS/macOS/Linux, pełny dostęp do pamięci Androida, zdalne wykonywanie poleceń.

## 3. Proponowany stos technologiczny

| Część | Technologia | Powód |
|---|---|---|
| Windows agent | C# / .NET 10 (LTS), aplikacja użytkownika | Autostart, tray, integracja z Explorerem |
| Windows UI | WPF lub WinUI 3; na MVP preferuj WPF | Mniejsza złożoność wdrożenia |
| Windows context menu | Rejestracja per-user, klasyczne menu | Bez uprawnień admina i bez shell extension DLL na start |
| Android | Kotlin, Android SDK, Jetpack | Natywny share sheet, SAF, NSD |
| Protokół | HTTPS + REST/streaming, opcjonalnie WebSocket | Przenośność i prostota diagnostyki |
| Discovery | mDNS / Android NSD | Wykrywanie w LAN |
| Tożsamość | Klucze per-device + wzajemne uwierzytelnianie | Brak zaufania do samego IP |
| Testy | xUnit (.NET), JUnit (Android), testy integracyjne | Automatyczna weryfikacja |
| CI/CD | GitHub Actions | Budowanie i publikacja wydań |

**Ważne:** szczegóły kryptografii wybrać z dojrzałych bibliotek i istniejących standardów (np. TLS z przypiętą tożsamością urządzenia). Nie projektować własnego algorytmu szyfrowania. Sam kod QR nie może zastąpić weryfikacji klucza drugiej strony.

## 4. Architektura repozytorium

```text
phonebridge/
├── README.md
├── LICENSE
├── docs/
│   ├── architecture.md
│   ├── protocol.md
│   ├── security.md
│   ├── android-background.md
│   └── roadmap.md
├── windows/
│   ├── PhoneBridge.Agent/
│   ├── PhoneBridge.UI/
│   ├── PhoneBridge.Core/
│   └── PhoneBridge.Tests/
├── android/
│   └── app/
├── protocol/
│   └── schemas/
├── scripts/
└── .github/workflows/
```

Nie tworzyć na siłę wspólnej biblioteki w języku, którego obie platformy nie używają. Wspólnym kontraktem powinien być **wersjonowany protokół** (JSON/OpenAPI + test vectors).

## 5. Protokół i model transferu

1. **Discovery:** urządzenie ogłasza identyfikator i endpoint, ale informacje z mDNS traktujemy jako niezaufane.
2. **Pairing:** użytkownik zatwierdza urządzenie i porównuje krótki kod wyprowadzony z uzgodnionej tożsamości kryptograficznej; aplikacje zapisują zweryfikowane klucze.
3. **Handshake:** połączenie szyfrowane, sprawdzenie sparowanego klucza i zgodności wersji protokołu.
4. **Offer:** nadawca przekazuje metadane pliku: nazwę, rozmiar, typ, identyfikator transferu, hash (jeżeli dostępny).
5. **Accept:** odbiorca potwierdza transfer i wskazuje miejsce zapisu zgodnie z uprawnieniami systemu.
6. **Stream:** transfer strumieniowy bez ładowania całego pliku do RAM; limit równoległych transferów i rozmiaru kolejki.
7. **Verify:** odbiorca sprawdza długość i SHA-256, zapisuje plik atomowo tam, gdzie pozwala na to API systemu, i zgłasza wynik.
8. **Cleanup:** usuwanie plików tymczasowych po błędzie, anulowaniu lub przekroczeniu czasu.

### Minimalne endpointy (propozycja, do uzgodnienia przed kodowaniem)

```text
GET  /v1/health
POST /v1/pair/start
POST /v1/pair/confirm
POST /v1/transfers/offer
POST /v1/transfers/{id}/accept
PUT  /v1/transfers/{id}/content
POST /v1/transfers/{id}/complete
POST /v1/transfers/{id}/cancel
```

Endpointy transferowe muszą być dostępne **tylko po uwierzytelnieniu**, z limitami rozmiaru, czasu, liczby żądań i ścisłą walidacją nazw/ścieżek. Nie pozwalać klientowi podawać dowolnej ścieżki docelowej na dysku odbiorcy.

## 6. Android: szczególne ograniczenia

- Odbieranie z menu „Udostępnij” działa przez `ACTION_SEND` / `ACTION_SEND_MULTIPLE` bez wcześniejszego otwierania UI.
- Dostęp do folderów docelowych: `ACTION_OPEN_DOCUMENT_TREE` i utrwalone uprawnienia URI tam, gdzie to możliwe.
- Dla transferów rozpoczętych przez użytkownika stosować odpowiedni mechanizm pracy długotrwałej/foreground service zgodny z aktualnymi wymaganiami Androida i typem usługi.
- **Nasłuchiwanie 24/7 nie jest gwarantowane**: proces może zostać zatrzymany, a urządzenie uśpione. W MVP zapewnić odbiór, gdy aplikacja/agent jest aktywny, oraz czytelny stan „telefon niedostępny”.
- Dla późniejszej poprawy niezawodności rozważyć zgodny z systemem tryb ciągłego połączenia z widocznym powiadomieniem, bez nadużywania wyłączeń optymalizacji baterii.
- Testować Android 13, 14, 15 i nowsze oraz urządzenia różnych producentów.

## 7. Windows: integracja

- Uruchomienie agenta przy logowaniu użytkownika (per-user startup lub Harmonogram zadań), z opcją wyłączenia.
- Menu kontekstowe per-user dla `*` i `Directory`, przekazujące ścieżki do działającego agenta przez lokalne IPC; poprawna obsługa spacji, Unicode i wielu plików.
- Agent nie powinien otwierać wielu instancji: pojedynczy proces + IPC + kolejka.
- Tray: status połączenia, lista urządzeń, historia transferów, ustawienia i wyjście.
- Nie uruchamiać agenta jako SYSTEM, jeśli nie ma takiej potrzeby.
- Docelowo rozważyć natywną integrację z nowym menu Windows 11, ale nie blokować nią MVP.

## 8. Bezpieczeństwo: wymagania obowiązkowe

- [ ] Brak nieszyfrowanych transferów, nawet w zaufanej sieci Wi-Fi.
- [ ] Klucze prywatne przechowywane w Android Keystore i Windows DPAPI / odpowiednim magazynie kluczy.
- [ ] Wyraźna zgoda na parowanie i cofnięcie zaufania do urządzenia.
- [ ] Ograniczenie dostępu do folderów, zasada minimalnych uprawnień.
- [ ] Obrona przed path traversal, symlink attacks, zip bombs (jeżeli kiedyś pojawią się archiwa), DoS i nadpisaniem plików.
- [ ] Walidacja metadanych i nazwy pliku po obu stronach.
- [ ] Bez wykonywania otrzymanych plików lub komend.
- [ ] Ograniczenie portu do odpowiedniego interfejsu sieciowego; brak automatycznego UPnP i otwierania na Internet.
- [ ] Threat model i przegląd bezpieczeństwa przed publiczną betą.

## 9. Plan realizacji dla Claude Code

### Sprint 0 — dokumentacja i szkielet

- [ ] Przejrzyj ten plan i wskaż ryzyka oraz niespójności.
- [ ] Utwórz strukturę repozytorium, README, LICENSE i dokumenty architektury.
- [ ] Zdefiniuj wersjonowany protokół i schematy wiadomości.
- [ ] Skonfiguruj build/test w CI (Windows + Android).
- **Kryterium akceptacji:** obie aplikacje budują się, testy bazowe przechodzą, a protokół jest opisany.

### Sprint 1 — połączenie i parowanie

- [ ] Agent Windows i aplikacja Android wykrywają się w jednej sieci.
- [ ] Parowanie z potwierdzeniem kodu i trwałym zapisem zaufanej tożsamości.
- [ ] Uwierzytelnione, szyfrowane połączenie i diagnostyka błędów.
- **Kryterium:** niesparowane urządzenie nie może odczytać danych ani przesłać pliku.

### Sprint 2 — transfer w obie strony

- [ ] Transfer pojedynczego pliku i wielu plików; streaming, hash, postęp, anulowanie.
- [ ] Android share sheet, odbieranie do wybranego folderu.
- [ ] Windows: proste okno wyboru pliku i odbiór do folderu użytkownika.
- **Kryterium:** pliki 1 KB, 10 MB i 2 GB przechodzą z identycznym SHA-256; błędy są czytelne.

### Sprint 3 — integracja systemowa

- [ ] Windows: menu kontekstowe „Wyślij na telefon”, autostart i tray.
- [ ] Android: udostępnianie bez otwierania aplikacji przed wyborem pliku.
- [ ] Kolejka, powiadomienia, odzyskiwanie po utracie połączenia.
- **Kryterium:** po restarcie Windows wysyłanie działa z menu kontekstowego; Android jasno sygnalizuje, kiedy jest dostępny.

### Sprint 4 — instalatory i publiczna beta

- [ ] Podpisany APK (klucz wydawniczy przechowywany bezpiecznie, nie w repozytorium).
- [ ] Instalator Windows (np. WiX/Inno Setup) z czystą deinstalacją.
- [ ] GitHub Releases, sumy SHA-256, changelog i instrukcje instalacji.
- [ ] Testy bezpieczeństwa, aktualizacji, odinstalowania i ograniczeń baterii.
- **Kryterium:** nowy użytkownik instaluje oba programy i przesyła plik bez terminala.

### Sprint 5 — wirtualny dysk (oddzielny milestone)

- [ ] Udostępnianie wybranych folderów Androida z SAF i wybranych folderów Windows.
- [ ] Zdalne listowanie, pobieranie i podgląd plików.
- [ ] Proof of concept mapowania jako dysku w Windows; porównać WebDAV z WinFsp.
- [ ] Testy odłączenia telefonu, opóźnień, blokad plików i ograniczeń zapisu.
- **Kryterium:** Eksplorator pokazuje wybrany folder telefonu, a odłączenie nie zawiesza Windows.

## 10. Publikacja bez sklepów

1. Publiczne repozytorium GitHub, licencja GPL-3.0-or-later, README PL/EN.
2. GitHub Actions buduje artefakty; sekrety podpisujące są poza repozytorium.
3. GitHub Releases udostępnia `.apk`, `.exe` / `.msi`, changelog i sumy SHA-256.
4. Własna strona projektu z linkami do wydań, opisem uprawnień i instrukcją instalacji.
5. Android: instalacja APK z zewnętrznego źródła wymaga zgody użytkownika i może podlegać dodatkowym wymaganiom platformy/regionu; przed wydaniem zweryfikować aktualne zasady dystrybucji poza Google Play.
6. Windows: niepodpisany instalator może wyświetlać ostrzeżenia SmartScreen; rozważyć podpis kodu i programy dla open source.
7. Aktualizacje nie mogą wykonywać pobranego kodu bez weryfikacji podpisu/autentyczności wydania.

## 11. Definicja ukończenia MVP

- [ ] Android → Windows z systemowego menu „Udostępnij”.
- [ ] Windows → Android z menu kontekstowego pliku/folderu.
- [ ] Jednorazowe parowanie, bez ponownego wpisywania kodu przy każdym transferze.
- [ ] Transfery szyfrowane, weryfikowane i odporne na typowe błędy.
- [ ] Autostart Windows; jasna informacja o ograniczeniach działania Androida w tle.
- [ ] Działa bez Internetu i bez zewnętrznego konta.
- [ ] Gotowe APK i instalator Windows, dokumentacja i testy.

## 12. Pierwsze polecenie do Claude Code

> Przeczytaj `PHONEBRIDGE_PLAN.md`. Chcę zbudować PhoneBridge jako darmowy projekt open source. Nie implementuj od razu całego systemu. Najpierw: (1) przeprowadź krytyczny przegląd planu pod kątem Android 13+ i Windows 11, szczególnie ograniczeń pracy w tle, bezpieczeństwa parowania i menu kontekstowego; (2) zaproponuj konkretne decyzje architektoniczne i uzasadnij je; (3) utwórz szkielet monorepo, README, dokumentację protokołu i testy bazowe; (4) przygotuj pierwszy mały, kompilowalny milestone. Nie implementuj własnej kryptografii. Po każdym etapie uruchom dostępne testy i pokaż wyniki, ograniczenia oraz następne kroki. Nie deklaruj działania na urządzeniach, których nie testowałeś.
