# Ideensammlung: schneller und mit weniger Tokens - ausgehend vom ATM-DQMH-Kaltbau

Stand 2026-11-01. Grundlage ist der ATM-Kaltbau vom 2026-10-31 (`C:\Temp\ATM_DQMH_Cold_2`): fünf
`labview-*`-Agenten, ungefähr 30 min Wandzeit, ungefähr 1,7 M Tokens. Gemessen ist das an den
Transkripten der Session (Skripte im Scratchpad, Zahlen unten). Bereits Untersuchtes steht in
`docs/token-economy.md` und wird hier nicht wiederholt, sondern nur weitergeführt. Das ist eine
**Diskussionsgrundlage**; umgesetzt ist nichts davon.

## 0. Was der ATM-Lauf gekostet hat

| Agent | Requests | Wandzeit | davon in Tools | Kontext beim 1. Request | Kontext am Ende | Output-Tokens |
|---|---|---|---|---|---|---|
| DQMH-Modul | 36 | 9,5 min | 6,3 min | 184 k | 230 k | 13 k |
| Konto-SubVIs | 43 | 7,2 min | 3,1 min | 217 k | 309 k | 31 k |
| UI-SubVIs | 27 | 7,0 min | 4,7 min | 217 k | 275 k | 22 k |
| Caraya-Tests | 35 | 7,4 min | 3,9 min | 209 k | 292 k | 24 k |
| Main VI + Graft | 86 | **19,1 min** | 4,7 min | 218 k | **447 k** | **100 k** |
| **Summe** | 227 | 50 min (parallel: ~30) | 22,7 min | | | 190 k |

Daraus folgen vier Befunde, auf die sich die Ideen unten beziehen:

1. **Schon der erste Request jedes Agenten liegt bei 184–218 k Tokens.** Bezahlt werden also
   ausschliesslich Requests über der 200-k-Schwelle, ab der der Long-Context-Tarif gilt
   (`token-economy.md` §4c). Der Kontext setzt sich etwa so zusammen:

   | Anteil | Grösse | Tokens (ca.) |
   |---|---|---|
   | `CLAUDE.md` | 296 kB | ~75 k |
   | Agent-Definition | 35–92 kB | |
   | Tool-Schemas der Agenten-Roster | **120–270 kB** | gemessen über `tools/list` |
   | Systemprompt von Claude Code | | |

   Die 62,6 M Cache-Read-Tokens der Agenten stammen zu gut zwei Dritteln aus diesem festen Sockel.
2. **Nur 45 % der Agenten-Wandzeit läuft in Tools, 55 % ist Modell-Latenz.** Beim Main VI sind es
   nur 25 % (4,7 von 19,1 min). Die Latenz folgt den Turns und den Output-Tokens, nicht der
   Kontextgrösse (`token-economy.md` §4a).
3. **Das Main VI ist der kritische Pfad.** Es braucht 86 Turns und 100 k Output. Davon entfallen
   nur etwa 20 k auf geschriebenes AIXML (79 kB); **der Rest ist Denken**. Zeit kostete vor allem:
   - die Suche nach Dingen, die vorher niemand festgelegt hatte: Schrift von `ATM Messages`
     (8 Bash-Aufrufe, ungelöst), Schreibweisen für Property Nodes, Spec-Seiten erneut gelesen;
   - **4 von 6 `lvai_graft_diagram`-Aufrufen schlugen fehl**:
     - kein aktives Projekt;
     - der `dIdx`-Fehler im Skript;
     - der Graft hat die `VI Server Reference`-Knoten gelöscht.
4. **Bekannte Fehlermuster wiederholen sich.** Die Teile im Überblick:

   | Teil | Fehlgeschlagen | Ursachen |
   |---|---|---|
   | Caraya-Läufe | 3 von 4 | Namenskonflikt `Deposit.vi`/`Withdraw.vi`, Fixtures nicht zurückgesetzt, Error 1 |
   | `lvai_dqmh_new_module` | 4 von 5 | Resume-Muster `stillRunning`, gewollt |
   | `lvai_generate_vis` | 2 Batches | Timeout, danach einzeln nachgebaut |

   Hinzu kommt, dass LabVIEW einmal abgestürzt ist, während parallel gearbeitet wurde.

## 1. Caches besser einsetzen

**1a. `lvai_vi_terminals` für eigenen Code cachen.** Für den AIXML-Export ist der Ausschluss von
eigenem Code richtig: Der Export hängt auch von den SubVIs ab. Die **Anschlüsse** eines VIs hängen
aber nur an seinem eigenen Connector Pane. Ein Schlüssel aus Pfad, Grösse und mtime genügt.
- Im ATM-Lauf gab es 16 Aufrufe, je 1–3 s und 1–2 kB.
- Diese Idee passt gut mit 2a zusammen; mit 2a wird sie fast überflüssig.

**1b. Einen „Schreibweisen-Spickzettel“ für CLD-Bausteine statt `lvai_aixml_reference`-Lookups pro
Agent.** Die Agenten haben unabhängig voneinander dieselben Dinge gesucht:
- 12 Reference-Aufrufe mit 88 k Zeichen;
- 2D-String-Array dreimal, Datei-Refnum, `Bundle By Name`, `Clear Errors`;
- Property Nodes mit mehreren Feldern;
- User-Event-Payload;
- dazu Greps durch NI-Exporte im Cache.

Vorschlag:
- `scripts/aixml-skeletons/` um verifizierte Bausteine erweitern: Datei lesen/schreiben, Suche in
  einem 2D-Array, Event-Loop mit Tick-User-Event, Control-Gruppe deaktivieren, DQMH-Request
  aufrufen.
- Jeder Baustein ist auf beiden Backends erzeugt und ausgeführt.
- Der Orchestrator nennt den Agenten die passenden Dateien.

Der Effekt ist ein Cache auf Wissensebene, nicht auf Byte-Ebene. Er spart Turns, Output und
Fehlversuche.

**1c. Prompt-Cache über Agenten hinweg.** Jeder Agent zahlt beim Start 184–350 k
Cache-Creation-Tokens (1,25-facher Preis). Agenten **desselben Typs** haben einen gleichen
Präfix (Systemprompt, Tools, `CLAUDE.md`, Definition). Ein zweiter Agent, der gestartet wird,
nachdem der erste seinen Cache geschrieben hat, liest diesen Präfix nur noch.
- Nicht gemessen ist, ob die Reihenfolge der Präfix-Teile das zulässt.
- Billig zu prüfen: zwei Agenten desselben Typs 20 s versetzt starten und
  `cache_creation_input_tokens` vergleichen.

**1d. Spezifikation und Panel einmal aufbereiten, nicht pro Agent.** Das PDF wurde zweimal gelesen,
in der Hauptsession komplett mit 3,5 M Zeichen Tool-Ergebnis. Vorschlag: einmal `pdftotext` plus
ein AIXML-Export des Vorlagen-VIs in den Build-Ordner (`spec.txt`, `panel.xml`), und die Agenten
bekommen die Pfade.

## 2. Workflow bei der Erstellung

**2a. Ein „Bauvertrag“ vor dem ersten Agenten.** Das ist der grösste einzelne Hebel für den
kritischen Pfad. Der Orchestrator schreibt `build-contract.json` in den Build-Ordner:
- jedes VI mit Pfad, Ordner und Projekt;
- Anschlussnamen, Typen, `conIdx` und `required`;
- welche Library-Member gleich heissen wie lose VIs.

Die Agenten implementieren gegen diesen Vertrag. Damit entfallen:
- `vi_terminals`-Rückfragen;
- abweichende Schreibweisen zwischen den Agenten;
- der Namenskonflikt `Deposit.vi`: er wäre beim Schreiben des Vertrags aufgefallen, nicht erst im
  Caraya-Lauf.

Der Vertrag kostet den Orchestrator wenige Minuten. Er stellt Fragen, die heute jeder Agent für
sich klärt, und zwar einmal und vorher.

**2b. Den Scaffold des Main VI früher anfangen.** Der Main-VI-Agent startete erst nach den SubVIs.
Mit dem Vertrag aus 2a kann er:
- den Scaffold sofort entwerfen, also AIXML schreiben, nachdenken und den Graft vorbereiten;
- erst für den Generate-Schritt warten, bis die Callees existieren und geladen sind.

Erwartung: Vom kritischen Pfad fallen mehrere Minuten weg, weil das Denken des Main-VI-Agenten
parallel zu den SubVIs läuft.

**2c. Den Main VI zerlegen.** 86 Turns in einem Kontext, der auf 447 k wächst, sind teuer und
fehleranfällig. Vorschlag:

| Teil | Wer | Inhalt |
|---|---|---|
| Scaffold | ein Agent | Erzeugen, Ausführen und Prüfen ohne Panel |
| Graft und Bind | Orchestrator | festes Rezept, mit einem Tool-Aufruf machbar (siehe 3a) |
| Verifikation | Orchestrator oder ein kleiner Agent | `signalsJson`-Drive |

**2d. Nicht nachforschen, was nicht spezifiziert ist.** Die Font-Suche (8 Aufrufe) und ähnliche
Detailfragen gehören als offene Punkte in den Bericht, nicht in eine Recherche. Das sollte als
Regel in die Agent-Definitionen: höchstens 2 Aufrufe für eine kosmetische Eigenschaft, danach
melden.

**2e. LabVIEW-Zustand gehört dem Orchestrator.** Die Agenten haben 6-mal `lvai_status` oder
`lvai_ensure_labview` aufgerufen (bis 41 s). Der Orchestrator stellt das einmal sicher; die
Agenten verlassen sich auf die Fehlermeldung des Arbeitstools.

**2f. Test-Fixtures automatisch.** Tests, die Dateien schreiben, brauchen pro Fall eine eigene
Kopie und vor jedem Lauf einen Reset. Das sollte der Runner-Generator anlegen
(`fixturesJson`: Quelle → Kopie pro Fall, Reset im Runner selbst) und nicht der Agent von Hand per
Bash.

**2g. Tool-Fixes aus dem Lauf.** Jeder dieser Punkte ist ein bekannter Fehlversuch weniger:
- `lvai_graft_diagram` öffnet das Projekt selbst, wenn `projectPath` angegeben ist, statt
  `noActiveProject` zu melden.
- `lvai_graft_diagram` wandelt `VI Server Reference`-Knoten selbst in Stand-ins um und bindet sie
  danach. Heute werden sie beim Graft still gelöscht.
- Der `dIdx`-Fix ist committet (`386fdf2a`), aber noch ohne Test.
- `lvai_check_aixml` warnt, wenn ein Call mit blossem Namen auf ein geladenes Library-Member
  gleichen Namens auflösen kann.

## 3. Tool-Beschreibungen

**3a. Beschreibungen auf „Was, wann, statt was“ kürzen und die Geschichte in die docs/
verschieben.** Die 117 Tools servieren 451 k Zeichen Schema, davon 213 k Beschreibung, mit 120
datierten Einträgen der Art „measured 2026-09-…“. Ein Agent-Roster kostet 120–270 kB pro Request.
Vorschlag für jede Beschreibung:
- ein Satz, was das Tool tut;
- ein Satz, wann man es nimmt und wann ein anderes Tool;
- die harten Vorbedingungen (Projekt offen oder zu);
- ein Verweis auf die docs/-Stelle.

Parameterbeschreibungen werden genauso behandelt. Schätzung: Die Schemas halbieren sich, das sind
pro Agenten-Request 15–35 k Tokens.

*Umgesetzt am 2026-11-01:*
- Das servierte Schema schrumpft von 454 k auf 258 k Zeichen (−43 %):
  - Tool-Beschreibungen: 215 k → 93 k;
  - Parameter: 152 k → 83 k;
  - datierte Einträge: 146 → 0.
- Pro Request eines Agenten sind das etwa 19 k Tokens weniger beim vi-generator und etwa 31 k beim
  class-generator.
- Der vorherige Wortlaut ist unverändert in `docs/tool-descriptions-archive.md` erhalten.
- Jede Beschreibung beginnt jetzt mit einem Familienpräfix („Generate:“, „Class:“, „DQMH:“ …).* **Das ist gleichzeitig der Findbarkeits-Hebel**: ToolSearch
und das Modell gewichten den ersten Satz, und der verschwindet heute in Messgeschichte.

**3b. Ein Präfix-Vokabular für die Suche.** Etwa „DQMH:“, „Class:“, „Test:“, „Generate:“,
„Inspect:“ am Anfang jeder Beschreibung. Dann findet `ToolSearch` mit einem Begriff die ganze
Familie, und der Agent lädt alles Nötige in **einem** `select:`. Im Gesamtkorpus kosteten 989
ToolSearch-Aufrufe 12,8 h Latenz.

**3c. Kleinere Roster.** Der Klassen-Agent hat 56 Tools (269 kB), der Caraya-Agent 36 (173 kB).
Werkzeuge, die eine Phase nur selten braucht, könnten aus dem Roster genommen und bei Bedarf über
den Orchestrator geholt werden. Abwägung: Ein fehlendes Tool kostete schon einmal einen Nachbau von
Hand (`CLAUDE.md`, „roster gaps“). Deshalb nur dort kürzen, wo eine Phase das Tool nachweislich nie
nutzt; das lässt sich aus den Transkripten zählen.

## 4. XML → JSON

Ehrlich gesagt ist der Rest hier klein. Antworten sind bereits JSON. Die Run-Werte sind seit
#115 nativ, und JSON für pylabview wurde gemessen und verworfen (`token-economy.md` §3). Im ATM-Lauf
war nicht XML das Grosse, sondern **wiederholter Begleittext in JSON**:

| Antwort | Grösse | was sie aufbläht |
|---|---|---|
| `lvai_generate_vis` | 32–43 k Zeichen pro Batch | pro VI vollständige `steps` mit `composerReport`, `note`, `symbolicNote`, `memoryNote`, `aiXmlSentToLabview` |
| `lvai_exec_state` | 5–10 k | `note`, `meaning`, `helperViPath`, `backendSource` pro VI wiederholt |

**4a. Erfolgsantworten kompakt.** Bei `ok: true` kommen pro VI nur Pfad, `ok`, `execState`,
Diagrammgrösse und Warnungen zurück. Statische Hinweise stehen einmal pro Antwort, nicht pro VI.
Die vollen `steps` gibt es nur bei einem Fehler oder mit `verbose: true`. Grob geschätzt sind die
grossen Antworten danach 70–80 % kleiner.

**4b. Ein AIXML-Autorenformat in JSON** (eine kompakte DSL, die der Server zu AIXML expandiert):
- Im ATM-Lauf wurden ~140 kB AIXML geschrieben, also ~35–40 k Output-Tokens oder ~20 % des
  Outputs.
- Eine DSL spart vielleicht die Hälfte davon.
- Sie ist aber eine zweite Sprache, die die Agenten lernen und falsch schreiben können.

Ich halte 1b (verifizierte Bausteine) und mehr Generatoren aus kurzen Spezifikationen für den
besseren Weg. Erst messen, welche Formen wiederkehren.

## 5. Effort-Stufen dynamisch

Der Output (100 k beim Main VI, davon ~80 k Denken) treibt die Latenz. Die Effort-Stufe lässt sich
auf zwei Wegen setzen:
- pro Agent-Definition (Frontmatter);
- pro Aufruf, aber nur, wenn `CLAUDE.md` oder der Benutzer es ausdrücklich verlangt.

Vorschlag: eine Tabelle in `CLAUDE.md` unter „Agents first“.

| Aufgabe | Effort | Begründung |
|---|---|---|
| DQMH-Modul und Events (tool-getrieben) | low/medium | Die Tools machen die Arbeit, der Agent ruft nur auf und prüft |
| Leaf-SubVIs nach Bauvertrag | medium | Der Vertrag nimmt die Designentscheide vorweg |
| Caraya-Tests nach Bauvertrag | medium | Erwartungswerte stehen in Spezifikation und Vertrag |
| Main VI, Graft, Event-Logik | high | Hier entstehen die echten Fehler (z. B. Producer/Consumer, `controlReadBeforeWait`) |
| Diagnose eines unerklärten Fehlers | high/xhigh | Eine falsche Diagnose kostet mehr als das Denken |

Die Qualitätsabsicherung bleibt dieselbe:
- `execState`, Caraya, `signalsJson`-Drive und Diagrammgrösse sind harte, maschinelle Gates;
- ein Agent mit weniger Effort, der ein Gate reisst, muss nachbessern.

Messen sollten wir es als A/B: den ATM-Kaltbau einmal mit dieser Tabelle und einmal ohne, und dann
Zeit, Tokens, Testergebnis und Restpunkte vergleichen. Ein **Modellwechsel** (z. B. Sonnet für die
Leaf-Agenten) wäre der stärkere Hebel. Er gehört aber in denselben A/B und nicht in eine
Vorab-Entscheidung.

## 6. Weitere Beobachtungen

- **`CLAUDE.md` kürzen** (`token-economy.md` §4.1) ist nach wie vor offen und **deine
  Entscheidung**. Mit 3a zusammen würde jeder Agent unter 200 k starten. Damit fiele der
  Long-Context-Aufschlag auf praktisch allen Agenten-Requests weg; geschätzt ist das die
  grösste reine Kostenersparnis dieser Liste.
- **Nebenläufigkeit gegen Stabilität.** LabVIEW ist während paralleler DQMH-Skripte und
  Generierung abgestürzt. Die Ursache ist offen. Wenn sich das wiederholt, lohnt sich eine Regel
  wie „DQMH-Skripting nie parallel zu anderen LabVIEW-Arbeiten“. Ein Absturz kostet mehr als die
  gewonnene Parallelität.
- **Resume-Turns bei `lvai_dqmh_new_module`.** Vier `stillRunning`-Runden sind vier Turns. Wenn
  der Client es erlaubt, wäre ein längeres Warten im Server pro Aufruf (nahe an 60 s) billiger.
- **Batch-Timeout bei `lvai_generate_vis`.** Ein Batch, der in den Timeout läuft, wird danach
  einzeln nachgebaut, und das ist teurer als gleich einzeln zu bauen. Entweder das Resume-Muster
  aus `ResumableCall` auch hier, oder eine Obergrenze der Batchgrösse nach geschätzter Dauer.

## 6a. Stand 2026-11-01 (Branch `feat/build-contract`)

| Punkt | Stand | Gemessen |
|---|---|---|
| 2a Bauvertrag | umgesetzt | `docs/build-contract.md` §7 |
| 3a Tool-Beschreibungen | umgesetzt | Schema 454 k → 258 k Zeichen |
| `CLAUDE.md` gekürzt | umgesetzt | 298 kB → 35 kB, also etwa 75 k → 9 k Tokens pro Request; Wortlaut in `docs/claude-md-archive.md` |
| 4a kompakte Erfolgsantworten | umgesetzt | `lvai_generate_vis`-Antwort: 17 % der ausführlichen (1 174 statt 7 106 Zeichen); volle Antwort mit `verbose: true` |
| 2g Graft | umgesetzt | öffnet das Projekt selbst (`projectPath`); lehnt gebundene `VI Server Reference`s vorab ab (`scaffoldBoundReferences`) |
| 2f Fixtures | umgesetzt | `lvai_run_caraya_tests fixturesJson`: Reset vor dem Lauf; ATM-Suite 28/0 |

Bei der Abnahme fiel auf, dass das `Accounts.txt` des ATM-Builds schon am 2026-10-31 um 11:50 auf
350 statt 550 stand. Ein Reset aus der Liefer-Datei hätte die Withdraw-Tests also rot gemacht.
Eine Fixture-Quelle gehört deshalb in den Testordner, nicht in die Daten der Anwendung.

## 7. Vorschlag für die Reihenfolge

Sortiert nach Ertrag pro Aufwand, unter der Bedingung, dass die Qualität nicht sinkt:

| # | Massnahme | Hebel | Code-Aufwand | Qualitätsrisiko |
|---|---|---|---|---|
| 1 | 2a Bauvertrag + 2b früher Scaffold | Zeit (kritischer Pfad), Turns | keiner (Orchestrator-Regel) | sinkt eher |
| 2 | 3a Tool-Beschreibungen kürzen | Tokens, Findbarkeit | mittel (117 Tools) | gering, wenn Vorbedingungen bleiben |
| 3 | `CLAUDE.md` kürzen | Tokens (Long-Context) | keiner | deine Entscheidung |
| 4 | 4a kompakte Erfolgsantworten | Tokens | klein | keins (`verbose` bleibt) |
| 5 | 2g Graft-Fixes, 2f Fixtures, Namenskonflikt-Warnung | Zeit, Fehlversuche | klein–mittel | sinkt |
| 6 | 1b CLD-Bausteine | Zeit, Output | mittel | sinkt (verifiziert) |
| 7 | 5 Effort-Tabelle, als A/B gemessen | Zeit, Output | keiner | per A/B prüfen |
| 8 | 1a, 1c, 1d | klein | klein | keins |
