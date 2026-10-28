# Dane i synchronizacja

Dane leżą w **folderze synchronizowanym przez OneDrive**. Każdy komputer zapisuje zmiany tylko do **własnego pliku** (`journal_<identyfikator>.jsonl`) i nigdy nie zmienia plików drugiego komputera, więc OneDrive nie ma jak zrobić konfliktu zapisu. Co kilkanaście sekund (i zaraz po zmianie plików) program wczytuje nowe wpisy drugiego komputera i buduje z nich **lokalną bazę** w folderze `Data` obok programu. Tę bazę można w każdej chwili odbudować z samych dzienników.

Co z tego wynika dla Ciebie:

- Wpis dodany na jednym komputerze pojawia się na drugim po kilkunastu sekundach, o ile OneDrive zdążył przesłać plik.
- Gdy oba komputery zmienią **ten sam wpis** naraz, wygrywa późniejsza zmiana.
- Dzienniki są jednocześnie kopią zapasową. Dodatkowo program robi własne kopie: [BackUp](settings.backup).
- Stan synchronizacji widać na dole okna: [Wskaźnik synchronizacji](main.sync).

## Drugi komputer

1. Pierwsza osoba **udostępnia** folder danych drugiej (w Eksploratorze: prawy przycisk → *Udostępnij*; uprawnienie: może edytować).
2. Druga osoba w zaproszeniu wybiera **Dodaj skrót do Mojego OneDrive**.
3. Na obu komputerach kliknij folder prawym przyciskiem i wybierz **Zawsze zachowaj na tym urządzeniu**. Bez tego OneDrive może zamieniać pliki na wersje „tylko w chmurze”, których program nie odczyta.
4. Druga osoba uruchamia program i w [kreatorze pierwszego uruchomienia](intro.firstrun) wskazuje ten sam folder.

> Gdy dane zapisała nowsza wersja programu, starsza sama sprawdzi, czy jest aktualizacja, i zaproponuje jej zainstalowanie. Zapisywanie jest wtedy wyłączone, żeby niczego nie uszkodzić.
