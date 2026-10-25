# Budżet domowy

Program dla Windows 11 do wpisywania przychodów i wydatków oraz pilnowania budżetu domowego. Pracują w nim dwie osoby na dwóch komputerach na wspólnych danych. Interfejs jest po polsku, walutą bazową jest złoty.

Wersja **0.6.1**.

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

U góry każdego ekranu są dwa przyciski z podpowiedzią klawiszy: **W – nowy wydatek** i **Q – nowy przychód** (klawisze działają, gdy nie piszesz w żadnym polu; można je zmienić w Ustawieniach → Skróty klawiszowe). **Nowy przychód** to proste okno: data, kwota (może być działaniem), waluta, kategoria i notatka. **Nowy wydatek** otwiera okno paragonu:

* Na górze dane całego paragonu: **data, miejsce, kwota łączna, waluta, wydarzenie** (i notatka). Poniżej **pozycje**: kategoria i kwota. Pierwsza pozycja dostaje całą kwotę łączną i kategorię, którą najczęściej przypisywałeś temu miejscu.
* **Enter** przechodzi do następnego pola; po kwocie łącznej od razu do pierwszej pozycji (waluta, wydarzenie i notatka zwykle zostają domyślne: dojdziesz do nich klawiszem Tab albo myszą). Po zatwierdzeniu kategorii Enter przechodzi do jej kwoty i zaznacza ją całą: Enter ją przyjmuje, albo wpisujesz inną kwotę. Gdy kwota jest mniejsza niż to, co zostało do rozdysponowania, pojawia się następna pozycja z resztą i kolejną w kolejności częstości kategorią tego miejsca (taką, której żadna pozycja jeszcze nie używa); tak do wyzerowania. Wtedy Enter przechodzi do przycisku **Zatwierdź**. Przycisk **+** dodaje pozycję ręcznie, **✕** ją usuwa; **Ctrl+Enter** zatwierdza z dowolnego pola, **Esc** zamyka okno.
* W polach kwoty można wpisać **działanie**, np. `2+2*2` albo `(10,50+4)/2` (dozwolone + − * / i nawiasy). Po Enterze pole pokazuje wynik, a po powrocie do pola znów widać działanie. Zapisuje się tylko wynik. Litera **`r`** w kwocie pozycji oznacza „reszta”: ta pozycja dostaje kwotę łączną minus pozostałe pozycje.
* Po zatwierdzeniu każda pozycja zapisuje się jako osobna transakcja (wspólna data, miejsce, waluta, wydarzenie i notatka); jedno **Cofnij** wycofuje cały paragon.
* **Słownik miejsc** powstaje z tego, co już zapisałeś (także z zaimportowanego Excela): np. po wpisaniu `MPK` i wybraniu Transport › Komunikacja miejska program podpowie tę kategorię następnym razem; gdy do miejsca użyto kilku kategorii, podpowiada najczęstszą (wśród kategorii głównej, której używano najczęściej, wygrywa najczęściej użyta **podkategoria**, a kategoria główna tylko wtedy, gdy miejsca nigdy nie księgowano na jej podkategorii), a w kolejnych pozycjach następne według częstości. Rozpoznaje też miejsce, które zawiera znane miejsce lub zaczyna się tak jak ono (od trzech liter).
* Kategoria **filtruje się podczas pisania**; Enter przyjmuje pierwszą pasującą.
* Kwotę w obcej walucie wpisz z kodem, np. `12,50 EUR` (także po działaniu: `2*6 EUR`) albo wybierz walutę z listy. Kurs średni NBP (tabela A) z ostatniego dnia roboczego przed datą wpisu pobierze się sam i można go zmienić ręcznie. Bez internetu program użyje ostatniego znanego kursu albo poprosi o kurs.
* Po zapisie pojawia się komunikat z przyciskiem **Cofnij** (też Ctrl+Z).
* **Alt+←** i **Alt+→** przesuwają ekran Transakcje, Pulpit, Budżety i Raporty do poprzedniego i następnego tygodnia, miesiąca lub roku.
* W tabeli Transakcje: **Esc** odznacza zaznaczone wpisy (a gdy nic nie jest zaznaczone – czyści filtry), **Del** usuwa zaznaczone, **Ctrl+K** zmienia ich kategorię (to samo jest pod prawym przyciskiem myszy).
* W tabeli Transakcje Home i End przechodzą do pierwszego i ostatniego wpisu. Esc, którego nic innego nie użyło (zaznaczenie, filtry, otwarta lista, pola tekstu), wraca na Pulpit.
* Podwójne kliknięcie w polu z kwotą zaznacza całą kwotę, razem z groszami.
* Klawisze skrótów można zmienić: Ustawienia → Skróty klawiszowe → *Zmień…* (program nie pozwoli przypisać klawisza, który ma już inne polecenie albo który jest zarezerwowany, np. Ctrl+C).
* Pełna lista skrótów: **F1** albo Ustawienia → Skróty klawiszowe.

**Kalkulator**: przycisk z ikoną kalkulatora obok przycisków nowego wydatku i przychodu otwiera go w osobnym oknie, które może zostać otwarte obok programu. Są cyfry, przecinek, `+ − × ÷`, `=`, `C`, ⌫ i zmiana znaku; działania wykonują się po kolei, jak w kalkulatorze biurkowym (12 + 3 × 2 = 30). Działa też z klawiatury (cyfry, `,` lub `.`, `+ - * /`, Enter, Backspace, Del = C, Esc zamyka). Przycisk **Kopiuj wynik** (albo Ctrl+C) wkłada liczbę ze schowka z przecinkiem dziesiętnym, bez spacji, gotową do wklejenia w polu kwoty.

Ekrany: **Pulpit** (miesiąc, kafelki, wykres kategorii z podkategoriami, paski budżetów, ostatnie wpisy, trwające wydarzenie), **Transakcje** (edycja w miejscu, filtry, przeglądanie tygodniowe, miesięczne, roczne, całe (*Wszystko*) albo w dowolnym zakresie, domyślnie bieżący miesiąc; na dole sumy wszystkich pasujących wpisów, a obok po prawej zaznaczonych; ikona Excela w prawym górnym rogu eksportuje to, co widać), **Budżety** (limity miesięczne z historią zmian), **Wydarzenia** (np. wakacje: koszty osobno, koszt na dzień, porównanie z planem), **Wydatki cykliczne** (osobny ekran: każdy wpis ma wymaganą nazwę; po kliknięciu wpisu widać jego szczegóły tylko do odczytu, a przycisk *Zmień* włącza edycję i zamienia się w *Zapisz zmiany*; pole *Potwierdzaj wykonanie* sprawia, że przy każdym terminie program pyta „czy to miało miejsce?”: Tak dodaje wpis, Nie pomija ten termin na zawsze, Później pyta przy następnym uruchomieniu; pytania pokazują się tylko, gdy okno programu jest widoczne), **Raporty** (zakładki: *Przegląd* – u góry wybierasz kolejno **Wszystko, Rok, Miesiąc** (domyślnie zawsze rok; wybór jest zapamiętywany do następnego uruchomienia; „Wszystko” obejmuje cały okres bez porównania, a ikona Excela eksportuje wtedy listę wszystkich transakcji), porównanie z poprzednim okresem (tylko tych zakresów, które istnieją: bieżący okres do dziś i poprzedni do tego samego dnia sprzed miesiąca/roku, a gdy dane zaczynają się później – od tego dnia; przy takim skróceniu pod tabelą jest uwaga, że przegląd jest niepełny), trend z wykresem sum (przychody, wydatki, bilans) obok, przyciski **Wszystko / Przychody / Wydatki** nad wykresem pokazują obie serie albo tylko jedną (bilans znika, gdy brakuje jednej z nich), średnie na dzień, tydzień i miesiąc dla wybranej kategorii; *Porównania* – lata obok siebie miesiąc po miesiącu albo wybrane miesiące obok siebie kategoria po kategorii, osobno dla wydatków i przychodów, wykres słupkowy, liniowy albo *Średnie* (dla każdego roku średnia miesięczna narastająco: styczeń, średnia stycznia i lutego, średnia trzech pierwszych miesięcy…); *Miejsca* – sumy według miejsc w wybranym okresie; po zaznaczeniu miejsca obok, po prawej, pojawia się tabela wszystkich wpisów zliczonych w jego sumie – tylko do przeglądu, z samą datą, kwotą, kategorią i notatką), **Ustawienia** (wygląd, kategorie z wyborem ikony z listy i koloru, import i eksport, diagnostyka, skróty klawiszowe).

Skróty ekranów: Ctrl+1 … Ctrl+8 (Pulpit, Transakcje, Budżety, Wydarzenia, Wydatki cykliczne, Płatności, Raporty, Ustawienia).

### Płatności (rachunki do zapłacenia)

Ekran **Płatności** służy do planowania opłat, np. rachunku z elektrowni z kilkoma terminami, tytułami i kwotami.

* **Odbiorcy**: nazwa i numer rachunku bankowego. Numer jest sprawdzany (26 cyfr i suma kontrolna), program podpowiada, ile cyfr jeszcze brakuje, a po wyjściu z pola pokazuje numer w standardowych grupach (`61 1090 1014 0000 0712 1981 2874`). Można podać kategorię, do której trafią zapłacone rachunki; bez niej program użyje kategorii ze słownika miejsc. Odbiorcę dodajesz w oknie **Zaplanuj płatności** (przycisk *Nowy odbiorca…*) albo na liście **Odbiorcy**.
* **Zaplanuj płatności**: wybierasz odbiorcę, potem wpisujesz wiersze: termin, tytuł płatności i kwota (kwota może być działaniem). **Enter** w kwocie dodaje następny wiersz: program proponuje go jako poprzedni, miesiąc później (z tym samym tytułem i kwotą; dzień 31 wraca po krótkim miesiącu). Zaproponowany wiersz jest wyszarzony i zapisze się dopiero, gdy go zatwierdzisz Enterem w kwocie albo cokolwiek w nim zmienisz; niezaakceptowana propozycja na końcu jest pomijana. Przycisk **+** dodaje taki sam wiersz, ale od razu liczony.
* **Edycja serii**: płatności wpisane razem tworzą serię. Dwukrotne kliknięcie dowolnej z nich otwiera to samo okno z całą serią (odbiorca, daty, tytuły, kwoty), w którym można zmieniać dane, dodawać i usuwać wiersze. Płatność, której dane się zmieniły, wraca do stanu „do zapłaty” (przelew ustawiony w banku już się z nią nie zgadza); niezmieniona zachowuje stan. Płatności wpisane starszą wersją programu edytuje się pojedynczo.
* **Zapłać** przy każdej płatności pokazuje **kod QR** do zeskanowania w aplikacji banku (standard ZBP „2D”: `|PL|rachunek|kwota w groszach|odbiorca|tytuł|||`), numer rachunku i kwotę do skopiowania oraz przyciski **Oznacz jako zapłaconą** i **Oznacz jako zaplanowaną**. Nazwa odbiorcy w kodzie ma najwyżej 20 znaków, tytuł 32 (dłuższe program skraca i o tym informuje); kwota powyżej 9 999,99 zł nie mieści się w kodzie, więc kod jej nie zawiera.
* **Zapłacona**: wydatek trafia do transakcji z **dzisiejszą datą** (miejsce = odbiorca, notatka = tytuł), a płatność znika z listy.
* **Zaplanowana** (przelew ustawiony w banku na później): płatność zostaje na liście, nie przypomina się, a gdy minie jej termin, program sam wpisze ją do transakcji **z datą terminu** i zdejmie z listy.
* **Przypomnienia**: na 3 dni przed terminem (i po nim) program pyta o każdą płatność nieoznaczoną jako zapłacona ani zaplanowana, przy każdym uruchomieniu, aż ją załatwisz. Przyciski: **Przypomnij później** (zapyta znowu przy następnym uruchomieniu), **Zapłać teraz** (kod QR), **Zapłacono**.
* Płatności i odbiorcy synchronizują się między komputerami jak reszta danych. Program w starszej wersji, który nie zna płatności, poprosi o aktualizację.
* Uwaga: strona ZBP z odnośnika (`zbp.pl/.../Standard-kodowania-informacji`) opisuje inny standard; kod QR jest zbudowany wg rekomendacji ZBP „Standard 2D” (przelew krajowy), sprawdzonej na implementacji referencyjnej. Warto raz sprawdzić zeskanowanie w swoim banku.

### Długie operacje

Gdy operacja trwa (np. usunięcie albo zmiana kategorii setek wpisów naraz), na pasku z przyciskami nowego wydatku, przychodu i kalkulatora pojawia się napis z kręcącym się kółkiem, np. „Usuwanie wpisów: 1200…”. Praca idzie w tle, więc okno nie zamiera, a tabele odświeżają się raz, po zakończeniu. Cofnięcie dużej operacji też pokazuje taki napis.

### Wygląd

Ustawienia → **Wygląd**: motyw, zegar obok daty u góry (można wyłączyć), skala interfejsu (mały / średni / duży, działa od razu), symbol koloru kategorii (kółko, kwadrat albo pionowa kreska), pokazywanie znaków „+” i „−” przy kwotach, kolorowanie kwot (czerwone wydatki, zielone przychody; po wyłączeniu kwoty mają zwykły kolor tekstu motywu) oraz karta pokazywana po uruchomieniu programu (domyślnie Pulpit, albo *Ostatnio wybrana*). W palecie kolorów kategorii ptaszkiem są oznaczone kolory, które już ma jakaś kategoria.

### Aktualizacje

Ustawienia → Ogólne → *Automatycznie sprawdzaj aktualizacje*: kilka sekund po starcie program sprawdza najnowsze wydanie (Release) w repozytorium GitHub `NepciorLab/BudzetDomowy`. Gdy jest nowsze, pyta, czy je pobrać; po zgodzie pobiera `BudzetDomowy.exe` (albo archiwum `.zip` z tym plikiem), podmienia uruchomiony plik (stary zostaje chwilowo jako `BudzetDomowy.exe.old`) i uruchamia się ponownie. Przycisk *Sprawdź teraz* robi to samo na żądanie. Wydanie musi mieć załącznik `BudzetDomowy.exe` lub `BudzetDomowy-<wersja>.zip` i znacznik wersji w rodzaju `v0.3.0`.

### Tabele i okno

* Okno programu pamięta położenie i rozmiar (także stan zmaksymalizowany) i startuje z ostatnimi; jeśli monitor zniknął, okno wraca na widoczny ekran.
* Prawy przycisk myszy na nagłówku tabeli pozwala wybrać, które kolumny są widoczne (wybór jest zapamiętywany osobno dla każdej tabeli).
* Ustawienia → Ogólne → *Automatycznie dopasuj szerokość kolumn* dopasowuje kolumny do zawartości.

### Zmiana folderów

Ustawienia → Ogólne → **Folder danych** / **Folder lokalny** → *Zmień…*. Nowy folder danych jest sprawdzany tak jak w kreatorze pierwszego uruchomienia; można skopiować do niego dotychczasowe pliki zapisów (istniejących plików nie nadpisuje). Po zmianie program proponuje restart, a lokalna baza jest odbudowywana z dzienników nowego folderu. Folder lokalny (baza podręczna) musi leżeć poza OneDrive.

Transakcje z wydarzeniem są normalnie wliczane do podsumowań miesięcznych według kategorii; wydarzenie to dodatkowy sposób ich oglądania.

### Import z Excela

Ustawienia → **Import i eksport** → *Importuj z Excela*. Kreator wczytuje pliki `.xlsx` i `.xlsm` (makra nie są uruchamiane), rozpoznaje arkusze i kolumny, pozwala dopasować kategorie (wybór jest zapamiętywany) i pokazuje podgląd z listą problemów. Powtórny import tego samego pliku niczego nie podwaja: wpis o tej samej dacie, kwocie, miejscu i notatce, który już jest w programie, jest pomijany (powtarzające się w pliku zakupy są zachowane).

W podglądzie (ostatni krok) wiersze z błędem, z ostrzeżeniem i **pominięte** (np. wiersz sumy, zerowa kwota, brak kwoty) można **poprawić**: zaznacz taki wiersz i kliknij *Popraw wiersz…* albo kliknij go dwukrotnie. Okno pokazuje, co było w komórce z kwotą, i pozwala wpisać datę, kwotę (też jako działanie), rodzaj, miejsce, notatkę i kategorię; można też wiersz **pominąć**. Tabelę podglądu zawęzisz polami wyboru **Pokaż tylko:** (do zaimportowania, z ostrzeżeniem, duplikaty, z błędami, pominięte, poprawione oraz przychody i wydatki): zaznaczone rodzaje wierszy dodają się do siebie, a rodzaj (przychód/wydatek) zawęża je; bez zaznaczenia widać wszystko. Po wczytaniu pliku od razu zaznaczone są błędy, ostrzeżenia i pominięte (o ile coś takiego jest; w przeciwnym razie tabela pokazuje wszystko), a przycisk *Wyczyść filtry* pokazuje całość. Gdy w oknie poprawki zmienisz wydatek na przychód (albo odwrotnie), lista kategorii zmienia się razem z nim, a wiersz nie dostanie kategorii niepasującej do rodzaju. Poprawiony wiersz jest sprawdzany ponownie (także pod kątem duplikatów) i wchodzi do importu. Gdy przychody miesiąca leżą w innej komórce niż obok napisu „Przychody” (albo nie ma takiego napisu), w pierwszym kroku wpisz **adres komórki** (np. `E3`): program odczyta z niej przychód z każdego wybranego arkusza i od razu pokaże, co w niej znalazł. Adres jest używany tam, gdzie napisu nie znaleziono.

### Eksport

Transakcje do **Excela** (ikona w prawym górnym rogu ekranu Transakcje: wpisy pasujące do filtrów i okresu; w Ustawieniach – wszystkie), raport miesiąca lub roku do Excela.

## Gdzie program trzyma swoje dane

Ustawienia, dzienniki (logi), lokalna baza, kopie zapasowe i identyfikator komputera leżą w folderze **`Data` obok pliku `BudzetDomowy.exe`** (nie w AppData). Stare wersje trzymały je w `%LocalAppData%\BudzetDomowy`: przy pierwszym starcie nowa wersja kopiuje stamtąd dane (stary folder zostaje i można go usunąć ręcznie), więc komputer pozostaje tym samym urządzeniem w folderze danych. Jeśli program leży w miejscu, gdzie nie wolno zapisywać (np. `Program Files`), używa dawnego folderu w AppData.

### Nowsze dane, starsza wersja

Gdy dane zapisała nowsza wersja programu, ta starsza najpierw sprawdza w GitHubie, czy jest aktualizacja (niezależnie od ustawienia automatycznych aktualizacji), i proponuje jej zainstalowanie zamiast pokazywać błąd. Błąd pokazuje się dopiero, gdy aktualizacji nie ma albo się nie powiodła.

## Kopie zapasowe w programie

Ustawienia → **BackUp**: program raz dziennie (kilka sekund po starcie i potem co kilka godzin, jeśli dzisiejszej kopii jeszcze nie ma) robi kopię wszystkich zapisów (dzienników) z folderu danych i trzyma **10 ostatnich**. Kopie to pliki `.zip` w podfolderze `Backups` **folderu danych** (czyli w OneDrive), więc każdy komputer je widzi i może z nich przywracać; kopie zrobione przez 0.4.0 w folderze lokalnym są przy starcie tam przenoszone. Gdy jeden komputer zrobił już dzisiejszą kopię, drugi nie robi kolejnej. Zakładka pokazuje listę kopii (data, rodzaj: codzienna / ręczna / przed przywróceniem, rozmiar), pozwala zrobić kopię od ręki, otworzyć folder kopii i **przywrócić** wybraną. Przed przywróceniem program robi kopię obecnego stanu, więc można do niego wrócić tą samą drogą. Przywrócenie podmienia dzienniki w folderze danych (wycofuje też zmiany z innych komputerów, których dzienniki są nowsze); na drugim komputerze użyj potem Diagnostyka → *Odbuduj bazę z dzienników*.

## Kopia zapasowa ręczna

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
| Błąd, którego nie rozumiesz | Komunikat jest krótki, szczegóły są w pliku dziennika: `Data\logs` obok programu (przycisk w Diagnostyce). Przechowywane jest 14 dni. |

## Znane ograniczenia

* Plik `.exe` nie jest podpisany cyfrowo (ostrzeżenie SmartScreen), nie ma instalatora ani automatycznych aktualizacji.
* Synchronizacja była sprawdzana testami na dwóch folderach i na dwóch kopiach programu na jednym komputerze, nie na prawdziwych dwóch maszynach z OneDrive.
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
powershell -STA -File tools\MakeIcon.ps1           # ikona .ico (16-256 px, biel wokół rysunku przezroczysta) z BudzetDomowy.App\Assets\icon-source.webp
```

Struktura: `BudzetDomowy.Core` (domena, dziennik zdarzeń, synchronizacja, bazy, usługi, import/eksport, bez WPF), `BudzetDomowy.App` (WPF, MVVM, zasoby tekstów w `Resources\Strings.resx`), `BudzetDomowy.Tests` (xUnit, w tym symulacje dwóch urządzeń), `BudzetDomowy.UiTests` (budują ekrany z prawdziwego XAML i rysują je do obrazów w pamięci, bez pokazywania okien). Specyfikacja projektu: `CLAUDE.md`.
