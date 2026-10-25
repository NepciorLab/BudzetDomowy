# Projekt: Budżet domowy (Windows 11)

## Cel
Desktopowa aplikacja do wpisywania przychodów i wydatków oraz monitorowania budżetu domowego. Używana przez dwie osoby (dwa komputery) na wspólnych danych. Ma być szybka, intuicyjna i wymagać minimum kliknięć. Język interfejsu: polski. Waluta bazowa: PLN.

## Stos technologiczny
- C# / .NET 8, WPF, wzorzec MVVM (CommunityToolkit.Mvvm), wstrzykiwanie zależności (Microsoft.Extensions.DependencyInjection).
- Lokalna baza SQLite (Microsoft.Data.Sqlite, ewentualnie Dapper). Baza leży w folderze Data obok programu (awaryjnie w %LocalAppData%\BudzetDomowy\, gdy tam nie można zapisywać), NIGDY w folderze OneDrive.
- Wykresy: LiveCharts2 (LiveChartsCore.SkiaSharpView.WPF).
- Excel: ClosedXML (import i eksport .xlsx).
- Testy: xUnit.
- Publikacja: single-file, self-contained, win-x64 (jeden plik .exe, bez instalatora).
- Wszystkie teksty UI w plikach zasobów (Resources.resx), żeby dało się je łatwo zmienić.
- Kod i komentarze po angielsku, teksty dla użytkownika po polsku.

## Najważniejszy wymóg: praca dwóch osób na tych samych danych przez OneDrive
OneDrive nie obsługuje bezpiecznie jednoczesnego zapisu do jednego pliku, dlatego architektura jest oparta na dzienniku zdarzeń (event log):
- Użytkownik przy pierwszym uruchomieniu wskazuje folder synchronizowany (np. C:\Users\X\OneDrive\BudzetDomowy_dane). Drugi użytkownik wskazuje ten sam folder udostępniony przez OneDrive na swoim komputerze.
- Każda instalacja ma unikalny DeviceId (GUID, zapisany lokalnie) oraz przyjazną nazwę urządzenia. Każda instalacja zapisuje WYŁĄCZNIE do własnego pliku dziennika: `journal_<DeviceId>.jsonl` (append-only, jedno zdarzenie = jedna linia JSON). Nigdy nie modyfikuje plików innych urządzeń. Dzięki temu nie ma konfliktów zapisu.
- Każda zmiana (utworzenie, edycja, usunięcie transakcji, kategorii, wydarzenia, budżetu, reguły itd.) jest zapisywana jako zdarzenie: EventId (GUID), DeviceId, Timestamp (UTC, z licznikiem logicznym, np. hybrid logical clock), EntityType, EntityId, Operation (Upsert/Delete), Payload (JSON), SchemaVersion.
- Lokalna baza SQLite jest „widokiem zmaterializowanym”: powstaje przez odtworzenie zdarzeń ze wszystkich dzienników. Musi być możliwe całkowite odbudowanie bazy z samych plików dziennika (np. na nowym komputerze lub po uszkodzeniu bazy).
- Aplikacja co kilkanaście sekund (oraz przez FileSystemWatcher) wczytuje nowe linie z dzienników innych urządzeń, pamiętając pozycję (offset) w każdym pliku. Zastosowanie zdarzeń musi być idempotentne (ten sam EventId stosowany dwa razy nie zmienia wyniku).
- Rozwiązywanie konfliktów: ostatnia zmiana wygrywa (po Timestamp, remis rozstrzyga DeviceId), na poziomie encji. Usunięcia to „nagrobki” (tombstone), żeby późniejsze zdarzenie z drugiego urządzenia nie wskrzesiło usuniętego wpisu bez wiedzy użytkownika.
- Odporność na realia OneDrive:
  - czytanie plików innych urządzeń z FileShare.ReadWrite, z ponawianiem prób;
  - częściowo zsynchronizowana, ostatnia linia (niepełny JSON) ma być ignorowana i wczytana ponownie później;
  - wykrywanie plików „tylko w chmurze” (atrybut recall on data access) i pokazanie czytelnego komunikatu z instrukcją „Zawsze zachowaj na tym urządzeniu” dla folderu;
  - wykrywanie plików konfliktowych OneDrive (np. nazwy z dopiskiem nazwy komputera) i ostrzeżenie w UI zamiast cichego ignorowania;
  - wersja schematu zdarzeń: starsza wersja aplikacji, która trafi na nowszy schemat, ma się zatrzymać z komunikatem „zaktualizuj aplikację”, a nie psuć dane.
- W stopce okna stały wskaźnik stanu synchronizacji (zsynchronizowano / trwa / problem + czas ostatniej synchronizacji, kliknięcie pokazuje szczegóły).
- Kopia zapasowa: dzienniki w folderze OneDrive są jednocześnie kopią zapasową. Dodatkowo funkcja ręcznego eksportu.

## Model danych (każda encja ma GUID jako Id)
- Transaction: Id, Date, Type (Expense/Income), Amount (w walucie wpisanej), CurrencyCode, ExchangeRateToPln, AmountPln (przeliczone i zapisane), CategoryId, Note, EventId (opcjonalne), CreatedAt, CreatedByDeviceId. Model ma od początku dopuszczać pozycje podrzędne (TransactionLine: kategoria + kwota + opis), aby w przyszłości można było dzielić paragon na kilka kategorii i podpiąć odczyt paragonów przez AI. W pierwszej wersji UI wystarczy jedna pozycja na transakcję.
- Category: Id, Name, ParentId (kategorie i podkategorie), Type (Expense/Income/Both), Color, Icon, SortOrder, IsArchived (archiwizacja zamiast usuwania, jeśli ma transakcje).
- SpecialEvent (wydarzenie specjalne, np. wakacje): Id, Name, DateFrom, DateTo, Color, opcjonalny planowany budżet w PLN, Note. Wydarzenie to osobny wymiar obok kategorii: transakcja ma kategorię ORAZ opcjonalnie wydarzenie. Transakcje wydarzenia są normalnie wliczane do miesięcznych podsumowań według kategorii, a dodatkowo można je podsumować osobno.
- Budget: Id, CategoryId, kwota miesięczna, ważny od miesiąca (z historią zmian).
- RecurringTransaction: Id, szablon transakcji, częstotliwość (tygodniowo/miesięcznie/rocznie), dzień, data początku i końca, ostatnio wygenerowana data. Generowanie jest deterministyczne i idempotentne (żeby dwa komputery nie utworzyły podwójnych wpisów: ID wygenerowanej transakcji wyliczane z Id szablonu i daty).
- CategoryRule (uczenie się kategoryzacji): wzorzec tekstu notatki → CategoryId (+ opcjonalnie EventId), licznik użyć. Tworzona i wzmacniana automatycznie, gdy użytkownik przypisuje kategorię do wpisu z daną notatką. To ma też być fundament pod późniejsze AI do paragonów („decyzja użytkownika jest zapamiętywana”).
- Waluty: obsługa wpisywania kwoty w walucie obcej. Kurs pobierany z publicznego API NBP (tabela A, kurs z ostatniego dnia roboczego przed datą transakcji), z cache w lokalnej bazie, możliwością ręcznego nadpisania kursu oraz działaniem offline (wtedy użytkownik wpisuje kurs ręcznie lub korzysta z ostatniego znanego). Wszystkie raporty liczą w PLN (AmountPln).

## Poza zakresem (świadomie pominięte, nie implementować)
Konta i portfele, informacja kto wydał, import CSV z banku, załączniki ze zdjęciami paragonów, przypomnienia o rachunkach, cele oszczędnościowe, integracja z bankami, dostęp z telefonu. Na później (tylko zostaw punkty zaczepienia w architekturze, np. interfejs IReceiptReader): odczyt paragonów z użyciem AI z przypisywaniem kategorii do pozycji i pytaniem użytkownika przy niepewności.

## Zasady UX
- Stały pasek szybkiego dodawania u góry okna, widoczny na każdym ekranie: Data (domyślnie dziś), Kwota, Kategoria (ComboBox z filtrowaniem podczas pisania), Notatka, Wydarzenie (opcjonalne), przełącznik Wydatek/Przychód (domyślnie wydatek), waluta (domyślnie PLN). Enter zapisuje wpis, czyści pola i ustawia fokus z powrotem na kwotę. Tab przechodzi do kolejnego pola. Pełna obsługa z samej klawiatury.
- Po wpisaniu notatki, którą program zna (CategoryRule), kategoria (i wydarzenie) podpowiadają się automatycznie.
- Gdy dziś mieści się w dacie aktywnego wydarzenia, program podpowiada to wydarzenie jako domyślne (łatwo je wyłączyć).
- Po zapisaniu: dyskretny komunikat z przyciskiem „Cofnij” (zamiast okien potwierdzeń). Dla usuwania też Cofnij zamiast pytania „czy na pewno”.
- Lewy pasek nawigacji: Pulpit, Transakcje, Budżety, Wydarzenia, Raporty, Ustawienia.
- Pulpit: przełącznik miesiąca (strzałki), kafelki Przychody / Wydatki / Bilans, wykres kołowy lub pierścieniowy kategorii, paski postępu budżetów (ostrzeżenie od 80%, czerwień po przekroczeniu), ostatnie transakcje, aktywne wydarzenie z sumą.
- Transakcje: tabela z edycją w miejscu (klik w komórkę, bez okien dialogowych), filtry na górze (zakres dat, kategoria, wydarzenie, typ, tekst), suma zaznaczonych wierszy, menu kontekstowe, wielokrotne zaznaczanie (np. masowa zmiana kategorii).
- Jasny i ciemny motyw (zgodny z ustawieniem Windows), czytelna typografia, skalowanie DPI, formatowanie liczb i dat po polsku (1 234,56 zł).
- Skrót globalny (np. Ctrl+Alt+B) otwierający małe okno szybkiego dodania, plus ikona w zasobniku systemowym (opcjonalne, w ostatniej fazie).

## Raporty
Podsumowanie miesiąca i roku, porównanie z poprzednim okresem, średnie wydatki miesięczne / tygodniowe / dzienne (za dowolny zakres), trend miesięczny (wykres liniowy lub słupkowy), top kategorie, udział kategorii (z drilldownem w podkategorie), raport wydarzenia (suma, podział na kategorie, koszt na dzień, porównanie z planowanym budżetem), przychody vs wydatki w roku. Dla średnich: jasno opisać metodę (np. czy bieżący, niepełny miesiąc jest wliczany).

## Excel
- Import danych historycznych z .xlsx: kreator z krokami: wybór pliku i arkusza → mapowanie kolumn (z automatycznym rozpoznaniem) → mapowanie kategorii z pliku na kategorie programu (możliwość utworzenia nowych) → podgląd i lista problemów (błędne daty, puste kwoty, duplikaty) → import. Import ma być powtarzalny bezpiecznie (wykrywanie duplikatów po dacie+kwocie+notatce), żeby przypadkowy drugi import nie podwoił danych. Wynik importu zapisywany jako zwykłe zdarzenia w dzienniku.
- Eksport do .xlsx (transakcje wg filtra oraz raporty) i CSV.

## Zasady pracy
- Zanim zaczniesz większą fazę, krótko przedstaw plan i strukturę projektu; po fazie uruchom build i testy.
- Silnik synchronizacji piszemy jako osobny projekt biblioteki (BudzetDomowy.Core), niezależny od WPF, z testami jednostkowymi, w tym testami symulującymi dwa urządzenia na dwóch folderach tymczasowych: równoległe dodawanie, edycja tego samego wpisu, usunięcie vs edycja, niepełna ostatnia linia, powtórne wczytanie tych samych zdarzeń, odbudowa bazy od zera.
- Struktura rozwiązania: BudzetDomowy.sln → BudzetDomowy.Core (domena, zdarzenia, synchronizacja, repozytoria), BudzetDomowy.App (WPF), BudzetDomowy.Tests.
- Nie dodawaj funkcji spoza specyfikacji bez pytania. Przy niejasnościach zadaj mi pytanie zamiast zgadywać.
- Commituj do git po każdej fazie z opisową wiadomością.
