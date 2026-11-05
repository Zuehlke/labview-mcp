# News-Poster-Vorlage

Erzeugt ein A-Format-Poster (1131 × 1600 px) im Skizzen-Stil des LabVIEW-MCP-Posters von 11/2026,
als PNG (doppelte Auflösung, 2262 × 3200) und als einseitiges PDF.

| Datei | Inhalt |
|---|---|
| `example-2026-11.json` | alle Texte des Posters von 11/2026 – Ausgangspunkt für ein neues Poster |
| `poster.html` | das Aussehen (Layout, Farben, Schriften) |
| `render.py` | baut aus einer JSON-Datei PNG und PDF |
| `sketch.webp` | das Roboter-Bild |

## Einmalig einrichten

Python 3 und Microsoft Edge (oder Chrome) sind nötig, dazu das Paket für den QR-Code:

```
python -m pip install --user segno
```

Für die Schriften (Google Fonts) braucht das Rendern Internetzugang.

## Ein neues Poster

Ein Release-Poster heisst nach seinem Tag, `vX.Y.Z-poster`, und liegt in `announcements\` -
siehe `announcements\README.md`.

1. `example-2026-11.json` in diesem Ordner kopieren, nach dem Tag benannt, z. B.
   `v2.5.0-poster.json`.
2. Texte anpassen (siehe unten).
3. Im Vorlagenordner rendern, mit der Ausgabe eine Ebene höher nach `announcements\`
   (`--out` gilt relativ zum aktuellen Ordner):

```
python render.py v2.5.0-poster.json --out ..
```

Die Ausgabe heisst wie die JSON-Datei: `announcements\v2.5.0-poster.png` und `.pdf`. Ohne
`--out` landet sie neben der JSON-Datei, also hier im Vorlagenordner.

4. Die neue Zeile in die Tabelle in `announcements\README.md` eintragen.

Die JSON-Datei eines Posters bleibt hier neben dem Beispiel liegen, damit sich das Poster später
neu rendern lässt.

## Die Felder

| Feld | Bedeutung |
|---|---|
| `tagline` | Zeile oben rechts, z. B. `Release News · 03/2027` |
| `headline_top`, `headline_marked` | Überschrift: erste Zeile, und zweite Zeile mit gelbem Marker |
| `aside` | handschriftliche Notiz neben der Überschrift; `\n` = Zeilenumbruch |
| `image`, `image_alt` | Bild (relativ zur JSON-Datei oder zum Vorlagenordner) und seine Beschreibung |
| `accent` | Akzentfarbe (Marker, Nummern, Ringe), Standard `#f2b705` |
| `notes` | 1 bis 3 Post-its, nebeneinander |
| `cta`, `cta_note` | „Try it now“ und die Notiz unter dem Link |
| `url`, `url_text` | Link des QR-Codes; `url_text` ist optional (sonst die URL ohne `https://`) |

Jedes Post-it in `notes` hat ein `label` und eine Farbe `color` (die umgeklappte Ecke wird daraus
automatisch dunkler berechnet) und beliebig viele dieser Teile, in dieser Reihenfolge:

| Teil | Darstellung |
|---|---|
| `title` | grosse Zahl oder Schlagwort |
| `chips` | Liste kleiner Pillen (z. B. Versionen) |
| `checks` | Liste mit Häkchen |
| `text` | ein Satz Fliesstext |

Bewährte Post-it-Farben: `#fff3c4` gelb, `#dcefe9` mint, `#e3ecfa` hellblau, `#fbe3d6` pfirsich.

## Worauf achten

- Das Poster hat eine feste Höhe. Bei viel Text in den Post-its wird es eng über der Trennlinie:
  pro Post-it höchstens etwa drei Häkchen oder ein Titel, eine Pillenreihe und ein Satz.
- Mit drei Post-its wird jedes schmaler – dann kurze Titel wählen.
- Ein neues Bild sollte quadratisch sein und einen hellen Hintergrund haben.
- Den QR-Code vor dem Drucken einmal mit dem Handy testen.
