# Import z Excela

Kreator wczytuje historię z plików `.xlsx` i `.xlsm` (makra nie są uruchamiane) w czterech krokach. **Powtórny import tego samego pliku niczego nie podwaja**: wpis o tej samej dacie, kwocie, miejscu i notatce, który już jest w programie, jest pomijany.

![Kreator importu, krok 2: kolumny (przykład).](import-2-columns.png)

1. **Plik i arkusze** – wskaż plik (**Przeglądaj…**) i zaznacz arkusze z wpisami. Opcje: *znak kwoty ma znaczenie* (minus to wydatek, plus przychód – wyciąg z banku) oraz *dodaj przychód z komórki*: gdy przychody miesiąca leżą w innej komórce niż obok napisu „Przychody” (albo nie ma takiego napisu), wpisz **adres komórki** (np. `E3`).
2. **Kolumny** – program rozpoznaje, która kolumna to data, kwota, kategoria, miejsce, notatka i (opcjonalnie) rodzaj; popraw, jeśli się pomylił. Jeśli data stoi tylko w pierwszym wierszu dnia, pozostałe wiersze tego dnia dostają tę samą.
3. **Kategorie** – dla każdej kategorii z pliku wybierz kategorię programu, utwórz nową albo zostaw wpisy bez kategorii. Wybór jest zapamiętywany.
4. **Podgląd** – lista wierszy ze statusem. Pola **Pokaż tylko** (do zaimportowania, z ostrzeżeniem, duplikaty, z błędami, pominięte, poprawione, przychody, wydatki) zawężają ją; **Wyczyść filtry** pokazuje całość. Wiersze z błędem możesz [poprawić](import.row). **Importuj** zapisuje wynik.
