# Budżet domowy

Program dla Windows 11 do wpisywania przychodów i wydatków oraz pilnowania budżetu domowego. Pracują w nim dwie osoby na dwóch komputerach na wspólnych danych. Interfejs jest po polsku, walutą bazową jest złoty.

Wersja **0.3.0**.

## Uruchomienie

1. Rozpakuj archiwum. W jego głównym katalogu jest jeden plik: `BudzetDomowy.exe`. Niczego nie trzeba instalować (ani .NET).
2. Uruchom go dwukrotnym kliknięciem. Windows może przy pierwszym uruchomieniu pokazać ostrzeżenie SmartScreen, bo plik nie jest podpisany cyfrowo: kliknij „Więcej informacji” → „Uruchom mimo to”.
3. Przy pierwszym uruchomieniu kreator poprosi o **folder danych** i nazwę komputera (patrz niżej).

Katalog `Source` w archiwum zawiera wszystkie źródła programu.

## Jak działają dane i synchronizacja

Dane leżą w **folderze synchronizowanym przez OneDrive**. Każdy komputer zapisuje zmiany tylko do **własnego** pliku `journal_<identyfikator>.jsonl` i nigdy nie zmienia plików drugiego komputera, dzięki czemu OneDrive nie ma jak zrobić konfliktu zapisu. Każdy komputer co kilkanaście sekund (oraz po zmianie plików) wczytuje nowe wpisy z plików drugiego i buduje z nich swoją **lokalną bazę** (`%LocalAppData%\BudzetDomowy\budzet.db`). Lokalna baza nie leży w OneDrive i zawsze można ją odbudować z samych dzienników.

Wskaźnik w lewym dolnym rogu okna pokazuje stan: zsynchronizowano, trwa synchronizacja albo problem. Kliknięcie otwiera szczegóły.

### Konfiguracja drugiej osoby (drugi komputer)

1. Pierwsza osoba tworzy folder w OneDrive, np. `BudzetDomowy_dane`, i wskazuje go w kreatorze.
2. Pierwsza osoba **udostępnia ten folder** drugiej (Eksplorator → prawy przycisk na folderze → *Udostępnij* albo na stronie onedrive.com; uprawnienie: **może edytować**).
3. Druga osoba otwiera zaproszenie i wybiera **„Dodaj skrót do Mojego OneDrive”**. Folder pojawi się u niej w OneDrive.
4. Na obu komputerach kliknij folder w Eksploratorze prawym przyciskiem i wybierz **„Zawsze zachowaj na tym urządzeniu”**. Bez tego OneDrive może zamieniać pliki na wersje „tylko w chmurze”, których program nie może odczytać (program ostrzeże o tym w kreatorze i na wskaźniku synchronizacji).
5. Druga osoba uruchamia program i w kreatorze wskazuje ten sam folder. Kreator wykryje dane pierwszej osoby i je wczyta; domyślne kategorie utworzy tylko wtedy, gdy nikt ich jeszcze nie utworzył.

### Próba na jednym komputerze

Drugą kopię programu możesz uruchomić jako „drugi komputer”: `BudzetDomowy.exe --profile B`. Profil B ma własną lokalną bazę i własną nazwę urządzenia; w obu kreatorach wskaż ten sam folder.

## Codzienne używanie

Pasek **szybkiego dodawania** u góry jest widoczny na każdym ekranie: Data, Kwota, Waluta, Kategoria, Miejsce, Notatka, Wydarzenie, rodzaj (Wydatek/Przychód).

* **Enter** przechodzi do następnego pola, a Enter na przycisku **Dodaj** zapisuje wpis i wraca na Kwotę. **Ctrl+Enter** zapisuje z dowolnego pola.
* Kategoria **filtruje się podczas pisania**; Tab lub Enter przyjmuje pierwszą pasującą.
* Wpisz **Miejsce** (np. „Lewiatan”): program zapamiętuje, do jakiej kategorii je przypisałeś, i następnym razem podpowie ją sam.
* Kwotę w obcej walucie wpisz z kodem, np. `12,50 EUR`. Kurs średni NBP (tabela A) z ostatniego dnia roboczego przed datą wpisu pobierze się sam; możesz go zmienić ręcznie (F4). Bez internetu program użyje ostatniego znanego kursu albo poprosi o kurs.
* Po zapisie pojawia się komunikat z przyciskiem **Cofnij** (też Ctrl+Z).
* **Alt+←** i **Alt+→** przesuwają ekran Transakcje, Pulpit, Budżety i Raporty do poprzedniego i następnego tygodnia, miesiąca lub roku.
* W tabeli Transakcje: **Esc** odznacza zaznaczone wpisy (a gdy nic nie jest zaznaczone – czyści filtry), **Del** usuwa zaznaczone, **Ctrl+K** zmienia ich kategorię (to samo jest pod prawym przyciskiem myszy).
* Podwójne kliknięcie w polu z kwotą zaznacza całą kwotę, razem z groszami.
* Klawisze skrótów można zmienić: Ustawienia → Skróty klawiszowe → *Zmień…* (program nie pozwoli przypisać klawisza, który ma już inne polecenie albo który jest zarezerwowany, np. Ctrl+C).
* Pełna lista skrótów: **F1** albo Ustawienia → Skróty klawiszowe.

Ekrany: **Pulpit** (miesiąc, kafelki, wykres kategorii z podkategoriami, paski budżetów, ostatnie wpisy, trwające wydarzenie), **Transakcje** (edycja w miejscu, filtry, przeglądanie tygodniowe, miesięczne, roczne, całe (*Wszystko*) albo w dowolnym zakresie, domyślnie bieżący miesiąc; na dole sumy wszystkich pasujących wpisów, a obok po prawej zaznaczonych; ikona Excela w prawym górnym rogu eksportuje to, co widać), **Budżety** (limity miesięczne z historią zmian), **Wydarzenia** (np. wakacje: koszty osobno, koszt na dzień, porównanie z planem), **Wpisy cykliczne** (osobny ekran: dodawanie i edycja; pole *Potwierdzaj wykonanie* sprawia, że przy każdym terminie program pyta „czy to miało miejsce?”: Tak dodaje wpis, Nie pomija ten termin na zawsze, Później pyta przy następnym uruchomieniu; pytania pokazują się tylko, gdy okno programu jest widoczne), **Raporty** (zakładki: *Przegląd* – miesiąc lub rok, porównanie z poprzednim okresem, trend, średnie na dzień, tydzień i miesiąc dla wybranej kategorii; *Porównania* – lata obok siebie miesiąc po miesiącu albo wybrane miesiące obok siebie kategoria po kategorii, osobno dla wydatków i przychodów, wykres słupkowy, liniowy albo *Średnie* (dla każdego roku średnia miesięczna narastająco: styczeń, średnia stycznia i lutego, średnia trzech pierwszych miesięcy…); *Miejsca* – sumy według miejsc w wybranym okresie), **Ustawienia** (wygląd, kategorie z wyborem ikony z listy i koloru, import i eksport, diagnostyka, skróty klawiszowe).

Skróty ekranów: Ctrl+1 … Ctrl+7 (Pulpit, Transakcje, Budżety, Wydarzenia, Wpisy cykliczne, Raporty, Ustawienia).

### Wygląd

Ustawienia → **Wygląd**: motyw, skala interfejsu (mały / średni / duży, działa od razu), symbol koloru kategorii (kółko, kwadrat albo pionowa kreska), pokazywanie znaku „−” przy wydatkach i kolorowanie kwot (czerwone wydatki, zielone przychody). W palecie kolorów kategorii ptaszkiem są oznaczone kolory, które już ma jakaś kategoria.

### Aktualizacje

Ustawienia → Ogólne → *Automatycznie sprawdzaj aktualizacje*: kilka sekund po starcie program sprawdza najnowsze wydanie (Release) w repozytorium GitHub `NepciorLab/BudzetDomowy`. Gdy jest nowsze, pyta, czy je pobrać; po zgodzie pobiera `BudzetDomowy.exe` (albo archiwum `.zip` z tym plikiem), podmienia uruchomiony plik (stary zostaje chwilowo jako `BudzetDomowy.exe.old`) i uruchamia się ponownie. Przycisk *Sprawdź teraz* robi to samo na żądanie. Wydanie musi mieć załącznik `BudzetDomowy.exe` lub `BudzetDomowy-<wersja>.zip` i znacznik wersji w rodzaju `v0.3.0`.

Repozytorium prywatne nie jest widoczne bez uwierzytelnienia: wpisz w tym samym miejscu token dostępu GitHub (Fine-grained, tylko odczyt zawartości); jest zapisywany w postaci chronionej dla Twojego konta Windows.

### Tabele i okno

* Okno programu pamięta położenie i rozmiar (także stan zmaksymalizowany) i startuje z ostatnimi; jeśli monitor zniknął, okno wraca na widoczny ekran.
* Prawy przycisk myszy na nagłówku tabeli pozwala wybrać, które kolumny są widoczne (wybór jest zapamiętywany osobno dla każdej tabeli).
* Ustawienia → Ogólne → *Automatycznie dopasuj szerokość kolumn* dopasowuje kolumny do zawartości.

### Zmiana folderów

Ustawienia → Ogólne → **Folder danych** / **Folder lokalny** → *Zmień…*. Nowy folder danych jest sprawdzany tak jak w kreatorze pierwszego uruchomienia; można skopiować do niego dotychczasowe pliki zapisów (istniejących plików nie nadpisuje). Po zmianie program proponuje restart, a lokalna baza jest odbudowywana z dzienników nowego folderu. Folder lokalny (baza podręczna) musi leżeć poza OneDrive.

Transakcje z wydarzeniem są normalnie wliczane do podsumowań miesięcznych według kategorii; wydarzenie to dodatkowy sposób ich oglądania.

### Import z Excela

Ustawienia → **Import i eksport** → *Importuj z Excela*. Kreator wczytuje pliki `.xlsx` i `.xlsm` (makra nie są uruchamiane), rozpoznaje arkusze i kolumny, pozwala dopasować kategorie (wybór jest zapamiętywany) i pokazuje podgląd z listą problemów. Powtórny import tego samego pliku niczego nie podwaja: wpis o tej samej dacie, kwocie, miejscu i notatce, który już jest w programie, jest pomijany (powtarzające się w pliku zakupy są zachowane).

### Eksport

Transakcje do **Excela** (ikona w prawym górnym rogu ekranu Transakcje: wpisy pasujące do filtrów i okresu; w Ustawieniach – wszystkie), raport miesiąca lub roku do Excela.

### Szybkie dodawanie spoza programu (opcjonalne)

Ustawienia → Ogólne → *Włącz globalny skrót szybkiego dodawania i ikonę w zasobniku* (domyślnie Ctrl+Alt+B, można zmienić w Ustawieniach → Skróty klawiszowe). Skrót i kliknięcie ikony otwierają małe okno szybkiego dodania. Domyślnie wyłączone.

## Kopia zapasowa

Dzienniki w folderze OneDrive **są** kopią zapasową: z samych plików `journal_*.jsonl` program odtworzy wszystko na nowym komputerze (wskaż folder w kreatorze). OneDrive przechowuje też poprzednie wersje plików. Dodatkowo:

* skopiuj od czasu do czasu cały folder danych w bezpieczne miejsce (pliki są tylko dopisywane, więc takie kopie nie wchodzą sobie w drogę),
* użyj Ustawienia → Import i eksport → *Wszystkie transakcje do Excela* jako czytelnej kopii.

## Rozwiązywanie problemów

Ustawienia → **Diagnostyka** pokazuje urządzenia, liczbę zdarzeń i rozmiary dzienników, listę problemów z opisem, co zrobić, a także przyciski *Synchronizuj teraz*, *Odbuduj bazę z dzienników* i *Otwórz folder dzienników*.

| Objaw | Co zrobić |
|---|---|
| „Plik … jest dostępny tylko w chmurze” | Kliknij folder danych prawym przyciskiem → **Zawsze zachowaj na tym urządzeniu**, poczekaj na pobranie plików. |
| „Kopia konfliktowa OneDrive” (plik `journal_…-NAZWA-KOMPUTERA.jsonl`) | Program jej nie wczytuje. Sprawdź zawartość i usuń ją, gdy nie jest potrzebna; w razie wątpliwości zachowaj kopię poza folderem. |
| „Zaktualizuj program” | W folderze są dane zapisane przez nowszą wersję programu. Do czasu aktualizacji zapisywanie jest wyłączone, żeby nic nie uszkodzić. |
| „Folder danych jest niedostępny” | Sprawdź, czy dysk/OneDrive działa i czy folder nie został przeniesiony. |
| Dane wyglądają na niepełne albo dziwne | Diagnostyka → **Odbuduj bazę z dzienników** (bezpieczne; dziennik się nie zmienia). Przycisk jest zablokowany, dopóki są pliki tylko w chmurze lub folder jest niedostępny. |
| Błąd, którego nie rozumiesz | Komunikat jest krótki, szczegóły są w pliku dziennika: `%LocalAppData%\BudzetDomowy\logs` (przycisk w Diagnostyce). Przechowywane jest 14 dni. |

## Znane ograniczenia

* Plik `.exe` nie jest podpisany cyfrowo (ostrzeżenie SmartScreen), nie ma instalatora ani automatycznych aktualizacji.
* Synchronizacja była sprawdzana testami na dwóch folderach i na dwóch kopiach programu na jednym komputerze, nie na prawdziwych dwóch maszynach z OneDrive.
* Aktualizacja z GitHuba nie była sprawdzona na prawdziwym wydaniu (repozytorium nie ma jeszcze wydań); logika jest sprawdzona testami na sztucznych odpowiedziach serwera.
* Konflikty rozstrzyga reguła „ostatnia zmiana wygrywa” na poziomie całego wpisu: jednoczesna edycja różnych pól tej samej transakcji na dwóch komputerach zachowa tylko późniejszą wersję. Usunięcie i edycja: wygrywa późniejsze z nich.
* Kursy walut pochodzą z tabeli A NBP (tylko waluty z tej tabeli). Zmiana daty już zapisanej transakcji w obcej walucie nie zmienia jej kursu; walutę wpisu zmienia się przez usunięcie i dodanie.
* Dzielenie paragonu na kilka kategorii i odczyt paragonów przez AI nie są zaimplementowane (model danych jest na to przygotowany).
* Import z Excela: nie obsługuje wierszy w walucie obcej, plików `.xls` ani CSV; przychody mogą być wczytane z jednej oznaczonej komórki arkusza.
* Cofnąć można tylko ostatnią operację, dopóki widać komunikat.
* Pliki dzienników tylko rosną (nie ma kompaktowania); przy typowym użyciu to megabajty rocznie.
* Wykresy (LiveCharts2) używają wersji kandydującej do wydania; ich wygląd w żywym oknie nie był sprawdzany automatycznie.
* Interfejs jest tylko po polsku.

## Budowanie ze źródeł

Wymagany jest .NET 8 SDK (Windows).

```powershell
dotnet build BudzetDomowy.sln
dotnet test BudzetDomowy.sln                       # testy logiki i testy interfejsu (bez otwierania okien)
.\tools\publish.ps1                                # dist\BudzetDomowy.exe i dist\BudzetDomowy-<wersja>.zip
.\tools\MakeIcon.ps1                               # ikona .ico z BudzetDomowy.App\Assets\BudzetDomowy.png
```

Struktura: `BudzetDomowy.Core` (domena, dziennik zdarzeń, synchronizacja, bazy, usługi, import/eksport, bez WPF), `BudzetDomowy.App` (WPF, MVVM, zasoby tekstów w `Resources\Strings.resx`), `BudzetDomowy.Tests` (xUnit, w tym symulacje dwóch urządzeń), `BudzetDomowy.UiTests` (budują ekrany z prawdziwego XAML i rysują je do obrazów w pamięci, bez pokazywania okien). Specyfikacja projektu: `CLAUDE.md`.
