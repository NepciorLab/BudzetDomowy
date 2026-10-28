# Nowy wydatek

Okno odpowiada **jednemu paragonowi**. U góry są dane całego paragonu (data, miejsce, kwota łączna, waluta, wydarzenie, notatka), niżej **pozycje**: każda ma swoją kategorię i kwotę.

![Okno nowego wydatku (przykład z wymyślonymi danymi).](entry-expense.png)

1. [Data](entry.date) – domyślnie dziś.
2. [Miejsce](entry.place) – sklep, firma; program podpowiada kategorię.
3. [Kwota łączna](entry.total) – cały paragon; może być działaniem.
4. [Waluta](entry.currency) – domyślnie PLN.
5. [Wydarzenie](entry.event) – opcjonalnie, np. wakacje.
6. [Notatka](entry.note) – dowolna uwaga.
7. [Pozycje](entry.lines) – kategoria i kwota każdej części paragonu.
8. Przycisk **+** dodaje pozycję ręcznie.
9. [Zatwierdź](entry.confirm).

## Jak to zwykle wygląda

1. Wpisz miejsce (kilka liter wystarczy) i `Enter`.
2. Wpisz kwotę łączną i `Enter`: kursor trafia od razu do pierwszej pozycji, która dostała całą kwotę i kategorię najczęściej używaną dla tego miejsca.
3. Jeśli zakup był z jednej kategorii, naciśnij `Enter` jeszcze dwa razy i zatwierdź.
4. Jeśli z kilku, zmień kwotę pierwszej pozycji na mniejszą: pojawi się następna pozycja z resztą i kolejną w kolejności częstości kategorią tego miejsca. Tak aż do wyzerowania.

Po zatwierdzeniu każda pozycja zapisuje się jako osobna transakcja (wspólna data, miejsce, waluta, wydarzenie i notatka), a **jedno Cofnij wycofuje cały paragon**. Zaraz potem otwiera się puste okno kolejnego wydatku; `Esc` kończy.
