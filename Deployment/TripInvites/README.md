# Uruchomienie zaproszeń TravelSet

1. Zastosuj migrację `20261109_add_packing_lists.sql`, jeśli nie została wcześniej zastosowana, a potem `20261109_add_trip_sharing.sql`. Zrób to przed dystrybucją nowej wersji aplikacji. Starsza wersja aplikacji obsługuje zapis pozycji prywatnych wyjazdów. Po dołączeniu uczestnika edycja pozycji tego wyjazdu wymaga aktualizacji aplikacji.
2. Domyślny host to `travelset-invites.pages.dev`. Opublikuj `join/index.html` pod ścieżką `/join/` i plik `assetlinks.json` pod `/.well-known/assetlinks.json`. Plik powiązania domeny musi zwracać HTTP 200 i typ `application/json` bez przekierowania.
3. W `assetlinks.json` wstaw SHA-256 certyfikatu, którym podpisano dystrybuowaną aplikację. Dla wersji z Play App Signing użyj odcisku certyfikatu podpisywania aplikacji z Play Console. Sprawdź, czy nazwa pakietu w obu plikach odpowiada publikowanej aplikacji.
4. Zbuduj Androida, np. `dotnet build Packo.csproj -c Release -f net10.0-android36.0`. Jeśli zmienisz host, przekaż `-p:PackoInviteHost=zaproszenia.twojadomena.pl`. Ta właściwość ustawia zarówno filtr Android App Links, jak i host linku generowanego przez aplikację.
5. W projekcie Google Cloud używanym do logowania dodaj klienta OAuth typu Android dla `com.travelset.app` z SHA-1 certyfikatu podpisującego dany build. Dla dystrybucji ze sklepu użyj SHA-1 certyfikatu podpisywania aplikacji z Play Console.
6. Na dwóch kontach sprawdź dołączenie po logowaniu, edycje w trybie samolotowym, odświeżenie po ponownym połączeniu oraz usunięcie uczestnika. Na Androidzie zweryfikuj domenę poleceniem `adb shell pm get-app-links com.travelset.app`.

Zaproszenie jest ważne przez siedem dni i przyjmuje je jedno konto. Po instalacji z Google Play odbiorca ponownie otwiera zaproszenie z oryginalnej wiadomości.
