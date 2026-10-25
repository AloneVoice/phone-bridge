# Protokół PhoneBridge v1

Wspólny kontrakt aplikacji Windows i Android. Zmiana niekompatybilna wstecz = nowy prefiks (`/v2`).

## Role

- **Komputer (Windows) jest serwerem**: HTTPS + WebSocket na porcie TCP `47800`, odpowiedzi discovery na UDP `47801`.
- **Telefon (Android) jest klientem**: utrzymuje połączenie WebSocket z komputerem, dopóki działa usługa pierwszoplanowa (stałe powiadomienie).
- Oba kierunki transferu inicjuje technicznie telefon (upload `PUT`, download `GET`), dzięki czemu telefon nie musi nasłuchiwać.

## Tożsamość i szyfrowanie

- Każde urządzenie ma własny klucz ECDSA P-256 i samopodpisany certyfikat X.509.
  - Windows: certyfikat + klucz w pliku konfiguracji, zaszyfrowane DPAPI (CurrentUser).
  - Android: klucz w Android Keystore (nieeksportowalny).
- **Odcisk (fingerprint)** = SHA-256 z DER certyfikatu, zapisany jako 64 znaki hex, małe litery.
- Połączenie: TLS 1.2/1.3, HTTP/1.1, **wzajemne uwierzytelnianie** (telefon zawsze przedstawia certyfikat klienta).
- Telefon akceptuje serwer wyłącznie, gdy odcisk certyfikatu = zapamiętany odcisk (pinning). Nazwa hosta nie jest sprawdzana.
- Serwer akceptuje żądania (poza `/v1/pair`) wyłącznie od certyfikatów z listy sparowanych urządzeń.
- Serwer odrzuca połączenia spoza sieci lokalnej (prywatne IPv4, link-local, IPv6 ULA, loopback).

## Parowanie (porównanie kodu, bez QR)

Model jak „numeric comparison” w Bluetooth: obie strony pokazują ten sam 6-cyfrowy kod, a schemat commit/reveal uniemożliwia atakującemu w środku dopasowanie kodu przez przeszukiwanie kluczy (szansa powodzenia ataku: 1 na 10⁶ na próbę).

Telefon znajduje komputer przez discovery (niżej) i łączy się po TLS **bez** przypinania (jeszcze nie zna klucza). W tej samej sesji TLS:

1. `POST /v1/pair/start` `{"deviceName","commitment"}`, gdzie `commitment = sha256hex(nP)`, `nP` = 32 losowe bajty telefonu.
   → `{"pairingId","serverNonce","serverName"}` (`serverNonce` = 32 losowe bajty jako hex).
2. `POST /v1/pair/reveal` `{"pairingId","nonce": hex(nP)}`. Serwer sprawdza commitment, liczy kod i pokazuje okno „Sparuj z <telefon>? Kod: 123 456”. Żądanie czeka na decyzję użytkownika komputera (≤ 120 s).
   → `{"accepted":true|false}`.
3. Telefon od momentu wysłania `reveal` pokazuje ten sam kod i pyta użytkownika, czy się zgadza.
4. Gdy użytkownik potwierdził na telefonie i serwer zwrócił `accepted:true`: `POST /v1/pair/confirm` `{"pairingId"}` → `{"ok":true}`. Dopiero wtedy komputer zapisuje odcisk telefonu, a telefon przypina odcisk komputera.

Kod: `SAS = uint32_be(sha256("PhoneBridge-SAS-v1" ‖ fpServer ‖ fpClient ‖ nP ‖ nS)[0..4]) mod 1 000 000`, gdzie odciski to 32 bajty surowe, a `nP`/`nS` to surowe bajty nonce. Wyświetlany jako `123 456` (z zerami wiodącymi).

Ochrona przed nadużyciem: jedna sesja parowania naraz, wygasa po 2 min, po odrzuceniu 10 s przerwy dla wszystkich nowych prób.

## Discovery

Telefon szuka komputerów automatycznie: przy parowaniu (lista komputerów w sieci) i przy każdym ponownym łączeniu, gdy zapamiętany adres nie odpowiada. Wysyła broadcast UDP na port `47801` (adres `255.255.255.255` i adres rozgłoszeniowy podsieci Wi-Fi):

```json
{"pb":1,"q":"discover"}
```

Komputer odpowiada unicastem:

```json
{"pb":1,"name":"DESKTOP-ABC","port":47800,"fp":"<odcisk>"}
```

Odpowiedź jest niezaufana: telefon używa jej tylko jako podpowiedzi adresu, a i tak weryfikuje `fp` w TLS. Gdy broadcast jest blokowany przez sieć (izolacja klientów, sieć gościnna), użytkownik wpisuje adres IP ręcznie.

## Endpointy HTTPS

Wszystkie ciała JSON w UTF-8, nazwy pól camelCase. Hash = SHA-256 jako 64 znaki hex (małe litery).

| Metoda | Ścieżka | Kto | Opis |
|---|---|---|---|
| `POST` | `/v1/pair/start` | każdy certyfikat | początek parowania (patrz wyżej) |
| `POST` | `/v1/pair/reveal` | ten sam certyfikat | ujawnienie nonce, czeka na decyzję na PC |
| `POST` | `/v1/pair/confirm` | ten sam certyfikat | potwierdzenie na telefonie, zapis zaufania |
| `GET` | `/v1/ws` | sparowany | WebSocket kanału sterującego |
| `POST` | `/v1/incoming` | sparowany | oferta telefon → PC, czeka na decyzję użytkownika (≤ 120 s) |
| `PUT` | `/v1/incoming/{id}/{index}` | sparowany | treść pliku `index` (strumień, `Content-Length` = zadeklarowany rozmiar) |
| `POST` | `/v1/incoming/{id}/{index}/commit` | sparowany | `{"sha256"}` → `{"ok","savedAs"}` |
| `POST` | `/v1/incoming/{id}/cancel` | sparowany | anulowanie przez telefon |
| `GET` | `/v1/outgoing/{id}/{index}` | sparowany (adresat) | pobranie pliku `index` oferty PC → telefon |
| `POST` | `/v1/outgoing/{id}/{index}/done` | sparowany (adresat) | `{"sha256"}` (hash zapisanych danych) → `{"ok"}` |

### Telefon → PC

1. `POST /v1/incoming` `{"files":[{"name":"IMG_1.jpg","size":123}]}`.
   Komputer pokazuje okno akceptacji z wyborem folderu (lub autoakceptuje).
   Odpowiedź: `{"accepted":true,"id":"<id>"}` lub `{"accepted":false,"reason":"rejected|timeout|insufficient_space"}`.
2. Dla każdego pliku: `PUT /v1/incoming/{id}/{index}`. Serwer zapisuje do pliku tymczasowego `*.pbpart`, liczy SHA-256.
3. `POST .../commit` z hashem policzonym przez telefon. Zgodność → atomowa zmiana nazwy na nazwę unikalną w folderze (`nazwa (1).ext`), nigdy nadpisanie. Niezgodność → usunięcie pliku tymczasowego, `{"ok":false}`.

### PC → telefon

1. Komputer wysyła przez WebSocket ofertę. `name` może zawierać `/` (struktura podfolderów przy wysyłaniu folderu).
2. Telefon pokazuje powiadomienie; użytkownik wybiera folder (SAF) i akceptuje albo odrzuca.
3. Telefon pobiera pliki `GET /v1/outgoing/{id}/{index}`, liczy SHA-256 tego, co zapisał, i wysyła `POST .../done`. Serwer porównuje z hashem policzonym przy wysyłaniu; `{"ok":false}` → telefon usuwa plik.

## Wiadomości WebSocket

Ramki tekstowe, JSON z polem `type`.

Serwer → telefon:

```json
{"type":"hello","serverName":"DESKTOP-ABC","protocol":1}
{"type":"offer","id":"<id>","files":[{"name":"Zdjęcia/a.jpg","size":1024}]}
{"type":"cancel","id":"<id>"}
```

Telefon → serwer:

```json
{"type":"hello","deviceName":"Pixel 8","protocol":1}
{"type":"accept","id":"<id>"}
{"type":"reject","id":"<id>"}
{"type":"cancel","id":"<id>"}
```

Oferty niezaakceptowane w chwili rozłączenia są wysyłane ponownie po ponownym połączeniu (telefon deduplikuje po `id`).

## Walidacja i limity

- Nazwy plików są sanityzowane **po stronie odbiorcy** (znaki niedozwolone, nazwy zarezerwowane Windows, `.`/`..`, znaki sterujące, długość). Nadawca nigdy nie wskazuje ścieżki na dysku odbiorcy, tylko nazwę względną w folderze wybranym przez użytkownika.
- Max 10 000 plików w ofercie, max 32 poziomy podfolderów, nazwa segmentu ≤ 255 znaków.
- Brak limitu rozmiaru pliku; komputer sprawdza wolne miejsce przed akceptacją.
- Ciała JSON ≤ 4 MB. Strumień, który przekroczy zadeklarowany rozmiar, jest przerywany.
