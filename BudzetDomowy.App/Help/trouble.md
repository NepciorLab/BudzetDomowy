# Gdy coś nie działa

Komunikat w programie jest krótki; szczegóły są w pliku dziennika w folderze `Data\logs` obok programu (przycisk **Otwórz folder dzienników** w [Diagnostyce](settings.diagnostics)). Przechowywane jest 14 dni.

- **„Plik … jest dostępny tylko w chmurze”.** Kliknij folder danych prawym przyciskiem → **Zawsze zachowaj na tym urządzeniu** i poczekaj na pobranie plików.
- **„Kopia konfliktowa OneDrive”** (plik `journal_…-NAZWA-KOMPUTERA.jsonl`). Program jej nie wczytuje. Sprawdź zawartość i usuń ją, gdy nie jest potrzebna; w razie wątpliwości zachowaj kopię poza folderem.
- **„Zaktualizuj program”.** W folderze są dane zapisane przez nowszą wersję programu. Do czasu aktualizacji zapisywanie jest wyłączone, żeby nic nie uszkodzić: [Aktualizacje](settings.general).
- **„Folder danych jest niedostępny”.** Sprawdź, czy dysk i OneDrive działają i czy folder nie został przeniesiony.
- **Dane wyglądają na niepełne albo dziwne.** [Diagnostyka](settings.diagnostics) → **Odbuduj bazę z dzienników** (bezpieczne; dziennik się nie zmienia).
- **Pomyliłem się.** Większość operacji można [cofnąć](main.toast), a przywrócić starszy stan pozwala [BackUp](settings.backup).
