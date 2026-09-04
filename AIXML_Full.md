# Master-Referenz: AI-XML-Struktur für LabVIEW-VIs

## 1. Zweck und Grundmodell

Diese Referenz beschreibt die **funktionale Struktur** eines AI-XML-Formats zur Repräsentation von LabVIEW-VIs.

| Konzept | Bedeutung |
|---|---|
| **`VI`** | Wurzelelement des Dokuments |
| **`uid`** | Eindeutige Kennung eines Elements innerhalb eines VI |
| **`uid_parent`** | Hierarchische Einbettung eines Elements |
| **Wire-Name** | Beschreibt Datenfluss |
| **`inputs` / `outputs`** | Verbinden Terminals über Wire-Namen |

### Kernregel

- **Hierarchie** wird über `uid` und `uid_parent` beschrieben.
- **Datenfluss** wird über **Wire-Namen** beschrieben.
- Diese beiden Modelle sind **getrennt**.

---

## 2. Globale Struktur

### Root-Element

```xml
<VI _name="Example.vi" description="Optional description">
  ...
</VI>
```

### Attribute von `VI`

| Attribut | Pflicht | Bedeutung |
|---|---:|---|
| **`_name`** | Ja | Name des VIs |
| **`description`** | Nein | Beschreibung |

### Regeln

| Regel | Bedeutung |
|---|---|
| Genau ein Root-Element | Muss `VI` sein |
| VI enthält Kind-Elemente | Controls, Nodes, Structures usw. |
| Elementreihenfolge | Nicht gleich Ausführungsreihenfolge |

---

## 3. Identität und Hierarchie

### `uid`

| Regel | Bedeutung |
|---|---|
| Eindeutig pro VI | Keine Dubletten |
| Kein Datenflussobjekt | Nicht als Wire verwendbar |

### `uid_parent`

| Wert | Bedeutung |
|---|---|
| `root` | Element liegt auf Top-Level |
| andere `uid` | Element liegt in diesem Parent |

### Beispiel

```xml
<Structure _name="For Loop" uid="100" uid_parent="root">
  <Node _name="Add" uid="101" uid_parent="100"/>
</Structure>
```

---

## 4. Datenfluss und Wire-Regeln

### Prinzip

| Konzept | Bedeutung |
|---|---|
| Output-Terminal | Erzeugt einen Wire |
| Input-Terminal | Verbraucht einen Wire |
| Ein Wire | Genau eine Quelle |
| Ein Wire | Beliebig viele Senken |

### Namenskonvention

```text
<uid>.<terminal_name>
```

Beispiel:

```text
71.x+y
43.value
```

### Regeln

| Regel | Bedeutung |
|---|---|
| Ein Output-Wire eindeutig | Keine zwei Quellen |
| Mehrere Inputs dürfen denselben Wire lesen | Fan-out erlaubt |
| Inputs/Outputs referenzieren Wire-Namen | Nicht `uid` |

---

## 5. Terminal-Listen

### Format

```text
terminal1:wire1,terminal2:wire2,terminal3:
```

### Bedeutung

| Teil | Bedeutung |
|---|---|
| `terminal` | Exakter Terminalname |
| `:` | Trenner |
| leerer Wire | Unverdrahtet |
| Reihenfolge | Semantisch relevant |

### Beispiel

```xml
<Node _name="Add" inputs="x:1.value,y:2.value" outputs="x+y:3.x+y" uid="3" uid_parent="root"/>
```

---

## 6. Elementtypen

## 6.1 `Control`

### Zweck
VI-Eingang.

### Prototype

```xml
<Control _name="input" outputs="value:43.value" type="double" uid="43" uid_parent="root" value="0"/>
```

### Attribute

| Attribut | Pflicht | Bedeutung |
|---|---:|---|
| `_name` | Ja | Anzeigename |
| `description` | Nein | Beschreibung |
| `outputs` | Ja | Output-Terminal `value` |
| `type` | Ja | Datentyp |
| `uid` | Ja | Eindeutige ID |
| `uid_parent` | Ja | Parent |
| `value` | Ja | Default-Wert |
| `style` | Nein | Stil, z. B. `Ring` |
| `items` | Bedingt | Ring-Einträge |
| `values` | Bedingt | Ring-Werte |

### Regeln
- Controls haben nur **Outputs**.

---

## 6.2 `Indicator`

### Zweck
VI-Ausgang.

### Prototype

```xml
<Indicator _name="result" inputs="value:71.x+y" type="double" uid="90" uid_parent="root" value="0"/>
```

### Attribute

| Attribut | Pflicht | Bedeutung |
|---|---:|---|
| `_name` | Ja | Anzeigename |
| `description` | Nein | Beschreibung |
| `inputs` | Ja | Input-Terminal `value` |
| `type` | Ja | Datentyp |
| `uid` | Ja | ID |
| `uid_parent` | Ja | Parent |
| `value` | Ja | Default-Wert |
| `style` | Nein | Stil |

### Regeln
- Indicators haben nur **Inputs**.

---

## 6.3 `Constant`

### Zweck
Benutzerdefinierte Konstante.

### Prototype

```xml
<Constant _name="N" outputs="value:10.value" type="int32" uid="10" uid_parent="root" value="5"/>
```

### Regeln
- Konstante ist eigener Knoten.
- Keine direkten Literale an Node-Terminals.

---

## 6.4 `FixedConst`

### Zweck
Built-in-Konstante.

### Prototype

```xml
<FixedConst _name="PI" outputs="value:98.value" uid="98" uid_parent="root"/>
```

---

## 6.5 `Node`

### Zweck
Built-in-LabVIEW-Funktion.

### Prototype

```xml
<Node _name="Multiply" inputs="x:1.value,y:2.value" outputs="x*y:3.x*y" uid="3" uid_parent="root"/>
```

### Attribute

| Attribut | Pflicht | Bedeutung |
|---|---:|---|
| `_name` | Ja | Funktionsname |
| `inputs` | Nein | Eingangsterminals |
| `outputs` | Nein | Ausgangsterminals |
| `uid` | Ja | ID |
| `uid_parent` | Ja | Parent |

### Mögliche Zusatzattribute

| Attribut | Beispiel |
|---|---|
| `operation` | `add` |
| `inversions` | ` , , ` |
| `includeHigh` | `true` |
| `includeLow` | `true` |
| `fields` | Property Node |
| `type` | `{LV.VI}` |

### Regeln
- Terminalnamen müssen exakt stimmen.
- Terminalreihenfolge ist relevant.

---

## 6.6 `Call`

### Zweck
SubVI-Aufruf.

### Prototype

```xml
<Call inputs="x:1.value" outputs="result:5.result" target="My SubVI.vi" uid="5" uid_parent="root"/>
```

### Attribute

| Attribut | Pflicht | Bedeutung |
|---|---:|---|
| `target` | Ja | Ziel-SubVI |
| `instance` | Nein | Polymorphe Instanz |
| `inputs` | Nein | Eingänge |
| `outputs` | Nein | Ausgänge |
| `uid` | Ja | ID |
| `uid_parent` | Ja | Parent |

---

## 6.7 `FreeLabel`

### Zweck
Kommentartext.

### Prototype

```xml
<FreeLabel comment="This loop averages the input data." uid="200" uid_parent="root"/>
```

---

## 7. Typnotation

### Primitive Typen

| Typ | Bedeutung |
|---|---|
| `bool` | Boolean |
| `int32` | I32 |
| `uint32` | U32 |
| `uint64` | U64 |
| `double` | DBL |
| `string` | String |
| `path` | Path |

### Arrays

| Typ | Bedeutung |
|---|---|
| `array{double}` | 1D-Array DBL |
| `array.2{double}` | 2D-Array DBL |

### Cluster

| Typ | Bedeutung |
|---|---|
| `cluster{double.X,double.Y}` | Cluster |
| `cluster{bool.status,int32.code,string.source}` | Error Cluster |

### Referenzen

| Typ | Bedeutung |
|---|---|
| `ref{LV.VI}` | VI-Referenz |

---

## 8. Ring-Style

### Zusätzliche Attribute

| Attribut | Bedeutung |
|---|---|
| `style="Ring"` | Ring-Darstellung |
| `items` | Sichtbare Einträge |
| `values` | Zugeordnete Integer-Werte |
| `value` | Aktiver Default-Wert |

### Beispiel

```xml
<Control _name="Mode" items="Low,Medium,High" outputs="value:40.value" style="Ring" type="int32" uid="40" uid_parent="root" value="1" values="[0,1,2]"/>
```

### Regel
- `value` muss in `values` enthalten sein.

---

## 9. Graphen

Graphen erscheinen typischerweise als Indicators.

| Graphtyp | Beispielstil |
|---|---|
| Waveform Chart | `graph21717` |
| Waveform Graph | `graph21703` |
| XY Graph | `graph21716` |
| Intensity Chart | `graph21719` |
| Intensity Graph | `graph21720` |
| Digital Waveform Graph | `graph21718` |
| Mixed Signal Graph | `graph21721` |

---

## 10. Strukturen

## 10.1 Allgemein

| Strukturtyp | `_name` |
|---|---|
| For Loop | `For Loop` |
| While Loop | `While Loop` |
| Case Structure | `Case Structure` |

---

## 10.2 `For Loop`

### Prototype

```xml
<Structure _name="For Loop" count="141.value" maxin="666.value" maxout="" uid="141" uid_parent="root">
</Structure>
```

### Attribute

| Attribut | Bedeutung |
|---|---|
| `count` | Iterationszähler innen |
| `maxin` | Optionales `N` von außen |
| `maxout` | `N` innen |
| `label` | Optionaler Kommentar |

---

## 10.3 `While Loop`

### Prototype

```xml
<Structure _name="While Loop" count="310.value" uid="310" uid_parent="root">
  <Condition inputs="value:43.value" uid="341" uid_parent="310" value="stop"/>
</Structure>
```

---

## 10.4 `Condition`

### Zweck
Loop-Bedingung.

### Prototype

```xml
<Condition inputs="value:43.value" uid="341" uid_parent="310" value="stop"/>
```

### Regel

| Form | Bedeutung |
|---|---|
| `value="stop"` | Stop-Bedingung |
| ohne `value` | Continue-Bedingung |

---

## 10.5 `Case Structure`

### Prototype

```xml
<Structure _name="Case Structure" selectin="43.value" uid="193" uid_parent="root">
  <CaseFrame selector="False" uid="203" uid_parent="193"/>
  <CaseFrame selector="True" uid="210" uid_parent="193"/>
</Structure>
```

### Attribut

| Attribut | Bedeutung |
|---|---|
| `selectin` | Case-Selector-Wire |

---

## 10.6 `CaseFrame`

### Attribute

| Attribut | Bedeutung |
|---|---|
| `selector` | Wert oder Bereich |
| `label` | Optionaler Kommentar |
| `selectout` | Frame-lokaler Selector-Wire |

### Beispiele

| Typ | Beispiel |
|---|---|
| Boolean | `False`, `True` |
| Integer | `0`, `4..9`, `Default` |
| Enum/String | `"Option 1"`, `"apple"` |
| Fehlercluster | `No Error`, `Error 1..5`, `Default` |

---

## 11. Strukturgrenzen

Direktes Verdrahten über Strukturgrenzen ist nicht zulässig.

| Übergang | Mechanismus |
|---|---|
| Außen → Innen | `Tunnel In` |
| Innen → Außen | `Tunnel Out` |
| Iteration → nächste Iteration | `ShiftReg` |

---

## 12. `ShiftReg`

### Prototype

```xml
<ShiftReg uid="511" uid_parent="141">
  <Left inputs="value:43.value" outputs="value:281.string" uid="520" uid_parent="511"/>
  <Right inputs="value:281.concatenated string" outputs="value:523.value" uid="523" uid_parent="511"/>
</ShiftReg>
```

### Bedeutung

| Teil | Rolle |
|---|---|
| `Left inputs` | Initialwert außen |
| `Left outputs` | Wert innen |
| `Right inputs` | Neuer Iterationswert innen |
| `Right outputs` | Endwert außen |

### Regel
- Feedback ist nur per `ShiftReg` erlaubt.

---

## 13. `Tunnel`

## 13.1 Tunnel In

### Non-indexing

```xml
<Tunnel _id="In1" inputs="value:outside_wire" outputs="value:inside_wire" uid="325" uid_parent="318"/>
```

### Indexing

```xml
<Tunnel _id="In1" inputs="value:outside_wire" mode="index" outputs="value:inside_wire" uid="325" uid_parent="318"/>
```

---

## 13.2 Tunnel Out

### Last value

```xml
<Tunnel _id="Out1" inputs="value:inside_wire" outputs="value:outside_wire" uid="383" uid_parent="358"/>
```

### Indexing

```xml
<Tunnel _id="Out1" inputs="value:inside_wire" mode="index" outputs="value:outside_wire" uid="383" uid_parent="358"/>
```

### Concatenating

```xml
<Tunnel _id="Out1" inputs="value:inside_wire" mode="concat" outputs="value:outside_wire" uid="343" uid_parent="318"/>
```

### Conditional

```xml
<Tunnel _id="Out1" cond="421..not. x?" inputs="value:365.value" mode="index" outputs="value:383.value" uid="383" uid_parent="358"/>
```

### Modusübersicht

| Modus | Wirkung |
|---|---|
| leer | Letzter Wert |
| `index` | Arrayaufbau |
| `concat` | Konkatenation |

---

## 14. Tunnel-Regeln für Case Structures

### Input-Tunnel

| Ort | Regel |
|---|---|
| Parent-Struktur | `inputs` setzen |
| Jeder Frame | gleiches `_id`, `outputs` setzen |

### Output-Tunnel

| Ort | Regel |
|---|---|
| Jeder Frame | `inputs` setzen |
| Parent-Struktur | `outputs` setzen |

### Wichtige Regel
- Jeder relevante Case muss einen Wert für jeden Output-Tunnel liefern.

---

## 15. Property Node

### Prototype

```xml
<Node _name="Property Node" fields="write+VI Name,read+VI Path" inputs="reference:1732.value,error in (no error):1768.value,VI Name:1834.value" outputs="VI Path:1482.Path,reference out:1482.reference out,error out:1482.error out" type="{LV.VI}" uid="1482" uid_parent="root"/>
```

### Attribute

| Attribut | Bedeutung |
|---|---|
| `fields` | Reihenfolge der Properties |
| `inputs` | Ref, Error, Write-Werte |
| `outputs` | Read-Werte, Ref out, Error out |
| `type` | Referenzklasse |

### Regeln
- `read+...` erzeugt Outputs.
- `write+...` erwartet Inputs.
- Reihenfolge in `fields` bestimmt die Terminalreihenfolge.

---

## 16. Zusätzliche Konsistenzregeln

| Regel | Bedeutung |
|---|---|
| Attribute alphabetisch | Formatkonvention |
| Kein echtes Newline in Attributen | `\0A` verwenden |
| Keine direkten Literale an Funktionsklemmen | Immer `Constant` |
| Keine freien Zyklen | Nur per `ShiftReg` |

---

## 17. Scope-Regeln

| Element | Scope |
|---|---|
| normale Nodes | Parent-Kontext |
| Loop-Innenleben | lokal zur Loop |
| CaseFrame | lokal zum Frame |
| Tunnel In | außen ↔ innen |
| Tunnel Out | innen ↔ außen |
| ShiftReg | außen ↔ innen ↔ außen |

---

## 18. Cheat Sheet

| Thema | Merksatz |
|---|---|
| `uid` | Identifiziert Knoten |
| `uid_parent` | Beschreibt Einbettung |
| Wire | Beschreibt Datenfluss |
| Feedback | Nur per Shift Register |
| Strukturgrenzen | Nur per Tunnel/ShiftReg |
| Case I/O | Über `InN` / `OutN` |

---

## 19. EBNF-/Schema-nahe Grammatik

```ebnf
Document        = VI ;
VI              = "<VI" NameAttr [DescriptionAttr] ">" { VIChild } "</VI>" ;
VIChild         = Control | Indicator | Constant | FixedConst | Node | Call | FreeLabel | Structure ;
Structure       = ForLoop | WhileLoop | CaseStructure ;
LoopChild       = Control | Indicator | Constant | FixedConst | Node | Call | FreeLabel | Tunnel | ShiftReg ;
CaseFrameChild  = Control | Indicator | Constant | FixedConst | Node | Call | FreeLabel | Tunnel | Structure ;
TerminalList    = TerminalEntry , { "," , TerminalEntry } ;
TerminalEntry   = TerminalName , ":" , [ WireName ] ;
```

### Semantische Regeln zur Grammatik

| Regel | Bedeutung |
|---|---|
| `uid` eindeutig | Pflicht |
| `uid_parent` auflösbar | Pflicht |
| Wire pro Quelle eindeutig | Pflicht |
| Terminalnamen exakt | Pflicht |
| Feedback nur per `ShiftReg` | Pflicht |
| Typ und `value` konsistent | Pflicht |

---

## 20. Vollständiges positives Beispiel

```xml
<VI _name="Scale And Offset Array.vi" description="Scales array elements and optionally adds an offset.">
  <FreeLabel comment="Scales each input element by a factor and optionally adds an offset depending on the selected mode." uid="10" uid_parent="root"/>
  <Control _name="Input Array" outputs="value:20.value" type="array{double}" uid="20" uid_parent="root" value="[]"/>
  <Control _name="Scale Factor" outputs="value:21.value" type="double" uid="21" uid_parent="root" value="1"/>
  <Control _name="Offset" outputs="value:22.value" type="double" uid="22" uid_parent="root" value="0"/>
  <Control _name="Mode" items="Scale Only,Scale And Offset" outputs="value:23.value" style="Ring" type="int32" uid="23" uid_parent="root" value="0" values="[0,1]"/>
  <Structure _name="For Loop" count="30.i" label="Process each array element" maxin="" maxout="" uid="30" uid_parent="root">
    <Tunnel _id="In1" inputs="value:20.value" mode="index" outputs="value:31.elem" uid="31" uid_parent="30"/>
    <Tunnel _id="In2" inputs="value:21.value" outputs="value:32.scale" uid="32" uid_parent="30"/>
    <Tunnel _id="In3" inputs="value:22.value" outputs="value:33.offset" uid="33" uid_parent="30"/>
    <Tunnel _id="In4" inputs="value:23.value" outputs="value:34.mode" uid="34" uid_parent="30"/>
    <Node _name="Multiply" inputs="x:31.elem,y:32.scale" outputs="x*y:35.scaled" uid="35" uid_parent="30"/>
    <Structure _name="Case Structure" selectin="34.mode" uid="36" uid_parent="30">
      <CaseFrame label="Scale only" selector="0" selectout="" uid="37" uid_parent="36">
        <Tunnel _id="Out1" inputs="value:35.scaled" uid="38" uid_parent="37"/>
      </CaseFrame>
      <CaseFrame label="Scale and offset" selector="1" selectout="" uid="39" uid_parent="36">
        <Node _name="Add" inputs="x:35.scaled,y:33.offset" outputs="x+y:41.x+y" uid="41" uid_parent="39"/>
        <Tunnel _id="Out1" inputs="value:41.x+y" uid="42" uid_parent="39"/>
      </CaseFrame>
      <Tunnel _id="Out1" outputs="value:43.result_elem" uid="43" uid_parent="36"/>
    </Structure>
    <Constant _name="Zero" outputs="value:47.zero" type="double" uid="47" uid_parent="30" value="0"/>
    <Node _name="Add" inputs="x:48.sum_in,y:43.result_elem" outputs="x+y:44.sum_out" uid="44" uid_parent="30"/>
    <Tunnel _id="Out1" inputs="value:43.result_elem" mode="index" outputs="value:45.output_array" uid="45" uid_parent="30"/>
    <ShiftReg uid="46" uid_parent="30">
      <Left inputs="value:47.zero" outputs="value:48.sum_in" uid="48" uid_parent="46"/>
      <Right inputs="value:44.sum_out" outputs="value:49.final_sum" uid="49" uid_parent="46"/>
    </ShiftReg>
  </Structure>
  <Indicator _name="Output Array" inputs="value:45.output_array" type="array{double}" uid="60" uid_parent="root" value="[]"/>
  <Indicator _name="Sum" inputs="value:49.final_sum" type="double" uid="61" uid_parent="root" value="0"/>
</VI>
```

---

## 21. Negatives Beispiel

```xml
<VI _name="Broken Example.vi">
  <Control _name="x" outputs="value:1.value" type="double" uid="1" uid_parent="root" value="0"/>
  <Control _name="y" outputs="value:1.value" type="double" uid="1" uid_parent="root" value="5"/>
  <Node _name="Add" inputs="a:1.value,b:2.value" outputs="sum:3.sum" uid="3" uid_parent="root"/>
  <Structure _name="For Loop" count="10.i" maxin="4.value" maxout="" uid="10" uid_parent="root">
    <Node _name="Multiply" inputs="x:3.sum,y:5.value" outputs="x*y:11.prod" uid="11" uid_parent="10"/>
    <Tunnel _id="Out1" outputs="value:12.out" uid="12" uid_parent="10"/>
  </Structure>
  <Indicator _name="result" inputs="value:12.out" type="double" uid="20" uid_parent="root" value="0"/>
</VI>
```

### Typische Fehler darin

| Fehler | Warum ungültig |
|---|---|
| doppelte `uid` | Identitätsverletzung |
| gleicher Output-Wire | Zwei Quellen |
| falsche Terminalnamen | Node-Semantik verletzt |
| unbekannte Input-Wires | Fehlende Quelle |
| direkter Zugriff in Loop | Scope-Verletzung |
| Output-Tunnel ohne Input | Ungültiger Tunnel |

---

## 22. JSON-Schema-ähnliches Modell

```json
{
  "VI": {
    "_name": "string",
    "description": "string?",
    "children": "VIChild[]"
  },
  "BaseNode": {
    "uid": "id",
    "uid_parent": "root | id"
  },
  "Control": {
    "_name": "string",
    "outputs": "terminalList",
    "type": "typeString",
    "uid": "id",
    "uid_parent": "parentRef",
    "value": "literal"
  },
  "Indicator": {
    "_name": "string",
    "inputs": "terminalList",
    "type": "typeString",
    "uid": "id",
    "uid_parent": "parentRef",
    "value": "literal"
  }
}
```

### Kernlogik

| Ebene | Regel |
|---|---|
| Identität | `uid` eindeutig |
| Parenting | `uid_parent` gültig |
| Wire | genau eine Quelle |
| Typ | `value` passt zu `type` |
| Struktur | nur gültige Kinder |

---

## 23. Validator-Checkliste

### Prüf-Reihenfolge

| Schritt | Prüfung |
|---|---|
| 1 | XML syntaktisch gültig |
| 2 | genau ein Root |
| 3 | Root ist `VI` |
| 4 | Pflichtattribute vorhanden |
| 5 | nur gültige Elementtypen |
| 6 | `uid` eindeutig |
| 7 | `uid_parent` gültig |
| 8 | Parent-Kind-Kontext gültig |
| 9 | Terminal-Listen gültig |
| 10 | Wire-Quellen eindeutig |
| 11 | Input-Wires auflösbar |
| 12 | Scope-Regeln korrekt |
| 13 | Tunnelregeln korrekt |
| 14 | Shift-Register korrekt |
| 15 | Case-Outputs vollständig |
| 16 | Typen konsistent |
| 17 | keine unzulässigen Zyklen |

### Empfohlene Fehlerausgabe

| Feld | Inhalt |
|---|---|
| Severity | Error / Warning |
| Code | z. B. `UID_DUPLICATE` |
| Element | `uid`, Typ, Name |
| Message | Kurzbeschreibung |
| Hint | Korrekturhinweis |

---

## 24. Mapping AI-XML ↔ LabVIEW

### Grundmapping

| AI-XML | LabVIEW-Konzept |
|---|---|
| `VI` | VI |
| `Control` | Frontpanel-Control |
| `Indicator` | Frontpanel-Indicator |
| `Constant` | Diagram Constant |
| `FixedConst` | Built-in Constant |
| `Node` | Primitive/Funktion |
| `Call` | SubVI |
| `Structure` | Loop/Case |
| `CaseFrame` | Einzelner Case |
| `Tunnel` | Tunnel |
| `ShiftReg` | Shift Register |
| `Condition` | Conditional Terminal |
| `FreeLabel` | Freies Label |
| `Property Node` | Property Node |

### Identität und Hierarchie

| AI-XML | LabVIEW-Bedeutung |
|---|---|
| `uid` | interne Knotenidentität |
| `uid_parent` | Container-Kontext |
| `root` | Top-Level-Blockdiagramm |

### Datenfluss

| AI-XML | LabVIEW-Bedeutung |
|---|---|
| `outputs="x:wire"` | Ausgang erzeugt Draht |
| `inputs="x:wire"` | Eingang konsumiert Draht |
| gleicher Wire in mehreren Inputs | verzweigter Draht |

### Strukturmapping

| AI-XML | LabVIEW |
|---|---|
| `count` | `i` |
| `maxin` | `N` |
| `selectin` | Case Selector |
| `Tunnel In` | Eingangstunnel |
| `Tunnel Out` | Ausgangstunnel |
| `mode="index"` | Auto-Indexing |
| `mode="concat"` | Concatenating Tunnel |

---

## 25. Was nicht direkt beschrieben wird

| Nicht direkt enthalten | LabVIEW-Aspekt |
|---|---|
| Koordinaten | Diagramm-Layout |
| Wire-Farbe | nur indirekt über Typ |
| Frontpanel-Geometrie | Position, Größe, Fonts |
| Compiled Code | nicht enthalten |
| Visuelle Connector-Pane-Ansicht | nicht enthalten |

---

## 26. Praktische Kurzreferenz

| Frage | Wo schauen? |
|---|---|
| Was ist das Element? | Elementname |
| Wie heißt es? | `_name` |
| Wo liegt es? | `uid_parent` |
| Was kommt rein? | `inputs` |
| Was geht raus? | `outputs` |
| Ist es in einer Struktur? | Parent prüfen |
| Geht Datenfluss über Grenze? | Tunnel / ShiftReg prüfen |

---

## 27. Export-Hinweis für PDF 📄

Du kannst dieses Markdown leicht als PDF speichern:

| Tool | Weg |
|---|---|
| **Word** | Einfügen → Speichern unter → PDF |
| **Typora** | File → Export → PDF |
| **VS Code** | Markdown öffnen → Markdown-PDF |
| **Browser** | In HTML umwandeln → Drucken → Als PDF speichern |

Wenn du willst, kann ich dir im nächsten Schritt noch eine **kompakte PDF-freundliche Version ohne Wiederholungen** erzeugen.