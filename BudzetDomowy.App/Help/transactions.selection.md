# Zaznaczanie i zmiany zbiorcze

![Zaznaczone wpisy: pod tabelą widać też ich sumy (przykład).](transactions-selection.png)

Zaznacz wpisy myszą (`Ctrl` – pojedynczo, `Shift` – zakres; `Ctrl + A` – wszystkie). Z zaznaczonymi możesz zrobić trzy rzeczy; są pod **prawym przyciskiem myszy**:

- **Zmień kategorię** ({key:Tx.ChangeCategory}) – wybierasz jedną kategorię dla wszystkich zaznaczonych.
- **Zmień kwotę…** – wszystkie zaznaczone wpisy dostają wpisaną kwotę (liczba albo działanie). Wpis w obcej walucie dostaje ją w swojej walucie.
- **Usuń** ({key:Tx.Delete}).

Każda z tych operacji pokazuje komunikat z przyciskiem [Cofnij](main.toast). Duże zaznaczenia zmieniają się w tle, z napisem na pasku u góry ([Długie operacje](main.busy)).

`Esc` odznacza zaznaczone wpisy, a gdy nic nie jest zaznaczone, czyści filtry.
