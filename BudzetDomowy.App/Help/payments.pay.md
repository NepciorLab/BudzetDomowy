# Zapłać

**Zapłać** przy płatności pokazuje okno z kodem QR.

![Okno płatności z kodem QR (przykład; kod jest ilustracją).](pay.png)

- **Kod QR** zeskanujesz w aplikacji banku (standard ZBP „2D”: rachunek, kwota, odbiorca i tytuł). Nazwa odbiorcy w kodzie ma najwyżej 20 znaków, tytuł 32 (dłuższe program skraca i o tym informuje). Kwota powyżej 9 999,99 zł nie mieści się w kodzie.
- **Kopiuj** przy numerze rachunku i **Kopiuj kwotę** – gdy wolisz wpisać przelew ręcznie.
- **Oznacz jako zapłaconą** – wydatek trafia do transakcji z **dzisiejszą datą** (miejsce = odbiorca, notatka = tytuł), a płatność znika z listy.
- **Oznacz jako zaplanowaną** – przelew jest ustawiony w banku na później. Płatność zostaje na liście, przypomnienia ustają, a gdy minie jej termin, program sam wpisze ją do transakcji **z datą terminu** i zdejmie z listy. **Wróć do „do zapłaty”** cofa to oznaczenie.

> Raz sprawdź zeskanowanie kodu w swoim banku, bo kod zbudowano wg rekomendacji ZBP, a nie wszystkie aplikacje czytają go tak samo.
