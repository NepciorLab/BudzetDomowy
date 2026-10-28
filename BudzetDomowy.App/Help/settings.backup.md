# Ustawienia: BackUp

![Zakładka BackUp (przykład).](settings-backup.png)

Raz dziennie (kilka sekund po starcie i potem co kilka godzin, jeśli dzisiejszej kopii jeszcze nie ma) program robi kopię wszystkich zapisów (dzienników) i trzyma **10 ostatnich**. Kopie to pliki `.zip` w podfolderze `Backups` folderu danych, więc każdy komputer je widzi. Gdy jeden komputer zrobił już dzisiejszą kopię, drugi nie robi kolejnej.

- Lista kopii: **data i godzina**, **rodzaj** (codzienna, ręczna, przed przywróceniem), **rozmiar**, **pliki zapisów**.
- **Utwórz kopię teraz** – kopia ręczna.
- **Przywróć wybraną kopię…** – podmienia dzienniki w folderze danych (wycofuje też zmiany z innych komputerów, których dzienniki są nowsze). **Przed przywróceniem program robi kopię obecnego stanu**, więc można do niego wrócić tą samą drogą. Na drugim komputerze użyj potem [Diagnostyka → Odbuduj bazę z dzienników](settings.diagnostics).
- **Otwórz folder** kopii.
