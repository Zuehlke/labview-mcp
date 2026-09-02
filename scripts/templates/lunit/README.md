# LUnit test-method AIXML templates

Three skeletons for the test methods `lvai_lunit_add_test_method` finishes. They are the files that
were actually generated, run and verified — six methods over a four-field class, `tests="6"
failures="0"` with a negative control — with the names and values replaced by `{{PLACEHOLDER}}`.

**Why they ship.** Measured over six builds of the same shape: authoring these six files, and before
that *finding something to copy them from*, is the largest remaining cost of the whole route —
98 s of wall clock against 5 s inside tools in one run, and still 53 s after the brief named the
sections to read. Four of those turns went on hunting for a previous run's files in a `c:\temp`
folder. `docs/labview-lunit-testing.md` §12 has the numbers. The last run's authoring cost was
effectively zero *because* an identical set happened to be lying around; these files make that the
normal case instead of an accident.

**There is now a tool that fills these in for you.** `lvai_lunit_scaffold_class_tests` takes the
subject class, the test case class, an output directory and one value per field, and writes all six
files — deriving the field names, types and socket names from the class's own accessors. Reach for
these templates directly when you want a shape the tool does not emit, when you are debugging what it
produced, or when LabVIEW is not available. **The tool's output is compared against these files by
`LUnitScaffoldTests`, so they remain the specification either way.**

Read `docs/labview-lunit-testing.md` §§3–6 for why the shape is what it is. This file is only how to
fill it in.

## The route these fit into

1. `lvai_create_class` — the test case class, parent `…\vi.lib\Astemes\LUnit\Test Case.lvclass`,
   **no `projectPath`, no `fields`**. Then write its `.lvproj` entry yourself, **while the project is
   closed** — the close saves, so an edit made while LabVIEW holds the file is destroyed. The line,
   literally, beside the subject class's own entry:

   ```xml
   <Item Name="Apfel Test.lvclass" Type="LVClass" URL="../Tests/Apfel Test.lvclass"/>
   ```

   `URL` is relative to the `.lvproj` file, so `../Tests/…` when the tests live in a subfolder.
2. **Restart LabVIEW.** `lvai_create_class` locks the class it just made and `AddItemFromMemory`
   then answers `Error 1562`. Only the *test case* class needs this — never the subject class.
3. `lvai_placeholder_subvi` once per accessor, **all of them in one message**. Each answer gives you
   a `placeholder` name — that is a `{{STUB_…}}` value. Expect `reused: true` on a rebuild.
4. Fill in these templates, one file per method, written with the file tool — **not a shell
   heredoc**, which has failed at this twice with `unexpected EOF` against a line that was fine.
   Put them anywhere you will keep them; beside the test methods (`…\Tests\`) is the convention,
   because they are the only way to rebuild a method later. **Issue all six writes in one message.**
5. `lvai_lunit_add_test_method` with `classPath`, `projectPath` and a `methodsJson` array — one call
   for every method.
6. `lvai_swap_subvis` per method: point each stub at the real accessor **and** turn the seed constant
   into a class constant. **One call per method, but issue them ALL IN ONE MESSAGE** — LabVIEW
   serialises the work anyway, so the six turns collapse into one. Measured: five turns saved, and
   batching is the only lever left on this route.
7. `lvai_run_lunit_tests`, then break one thing on purpose and run again.

## Placeholders

| | |
|---|---|
| `{{TESTCLASS}}` | the test case class name, e.g. `Apfel Test` — **`{{TESTCLASS}} In` / `Out`, capital I and O** |
| `{{CLASS}}` | the subject class name, e.g. `Apfel` — **`{{CLASS}} in` / `out`, lower case**, because that is what the accessor's own pane calls them |
| `{{FIELD}}`, `{{FIELDn}}` | the field name exactly as the accessor spells it, spaces included, e.g. `Gewicht g` |
| `{{TYPE}}`, `{{TYPEn}}` | `string`, `double`, `int32`, `bool` — the accessor's **real** type, never `variant` |
| `{{VALUE}}`, `{{VALUEn}}` | the value to write. Distinct per field, never a type default |
| `{{DEFAULTn}}` | the type's default: `` (empty) for string, `0` for numerics, `false` for bool |
| `{{DESCRIPTION}}`, `{{DESCn}}` | what the assertion means — it lands in the report on failure |
| `{{VI_DESCRIPTION}}` | the VI's own description. **English**, even when the class and fields are not |
| `{{STUB_WRITE}}`, `{{STUB_READn}}` … | the `placeholder` name from `lvai_placeholder_subvi`, e.g. `LVMCP Stub 57df820953.vi` |

The two capitalisation rules above are not cosmetic. `{{TESTCLASS}} In` must match what
`lvai_lunit_add_test_method` derives from the `.lvclass` file name, or the retype finds nothing and
the terminal keeps its `path` stand-in. `{{CLASS}} in` must match the accessor's pane, or the `Call`
has a terminal that does not exist.

## The files

| file | shape |
|---|---|
| `round-trip.xml` | one field: write, read back, assert equal. **Fully parameterised** — copy once per field |
| `defaults.xml` | all four fields read off a *fresh* class constant, four assertions |
| `independence.xml` | write all four on one object, then read all four back, four assertions |

`defaults.xml` and `independence.xml` are written for **four** fields, because that is the shape they
were measured on. For a different count, add or remove one block in each of these places and nowhere
else:

- the `Expected …` / `Description …` constants (uid 111–118, and 119/150–152 in `independence.xml`);
- the read (and write) `Call` elements, uid 120–123 and 124–127;
- the assertion `Call` chain, uid 130–133.

## Four wiring rules that are easy to get wrong

**The error chain is what forces execution order, and it is not the class wire.** Every `Call`'s
`error in (no error)` takes the previous one's `error out`. In `defaults.xml` the *class* input of
all four reads is the same seed (`110.value`) — they are deliberately independent of each other — so
without the error chain LabVIEW would be free to reorder them and the assertions would read whatever
happened to run first.

**In `defaults.xml` every read takes the seed; in `independence.xml` every read takes `123.{{CLASS}} out`.**
That is the difference between the two tests. Defaults must see an untouched object; independence
must see the one all four writes ran on. Getting this backwards produces a test that passes and
proves nothing.

**The seed constant is a `path` and becomes a class constant on the swap.** Name it
`{{CLASS}} seed` and pass it in `lvai_swap_subvis`'s `constantsJson`:

```json
[{"label": "Apfel seed", "class": "C:\\temp\\Apfel\\Apfel.lvclass"}]
```

A dynamic dispatch input is a **required** terminal, so without this the finished VI is
`Error 1003, not executable` — after everything else reported success. Nodes are swapped first and
constants last; `lvai_swap_subvis` enforces that.

**A stub is named by a hash of its SIGNATURE, so two fields of the same type share one name.** Fine
for a class whose field types are all distinct — the four in these templates are. For a class with
two `double` fields the two Read signatures are identical, one hash covers both, and
`lvai_swap_subvis` refuses the duplicate because matching is by VI name. Such a class needs sockets
named per field, hand-authored as `docs/labview-lunit-testing.md` §6 describes.

## The swap

One entry per stub on the diagram, mapping it to the real accessor:

```json
[{"socket": "LVMCP Stub 57df820953.vi", "target": "C:\\temp\\Apfel\\Write Gewicht g.vi"},
 {"socket": "LVMCP Stub 265773b388.vi", "target": "C:\\temp\\Apfel\\Read Gewicht g.vi"}]
```

**After the method is a class member the socket name changes spelling.** A later swap — injecting a
negative control, say — takes the class-qualified name `Apfel.lvclass:Write Gewicht g.vi`, not the
stub name. Both spellings are correct, minutes apart, on the same VI.

**And the RESTORING swap names the accessor you injected, not the original.** This is the one
counter-intuitive step: the socket is whatever the diagram currently calls, so after injecting
`Write Erntejahr.vi` you undo it with

```json
[{"socket": "Apfel.lvclass:Write Erntejahr.vi", "target": "C:\\temp\\Apfel\\Write Gewicht g.vi"}]
```

Naming the original as the socket finds nothing on the diagram, and `lvai_swap_subvis` refuses a name
it cannot see rather than silently swapping element 0.

## Verifying, and the one thing that proves nothing

`lvai_lunit_add_test_method` verifies each method from LabVIEW's own export: it must report
`_name="<TestClass>.lvclass:<Method>.vi"`, `classTypedTerminals: 2` and
`pathStandInsLeftOnPane: 0`.

Then **an all-green run proves nothing on its own.** The cheapest negative control needs no
regeneration: one `lvai_swap_subvis` repointing a write accessor at a differently-typed field — e.g.
`Write Gewicht g.vi` → `Write Erntejahr.vi`, putting a `double` constant on an `int32` input, an
ordinary coercion that leaves the assertion's wire unchanged. Exactly one case must fail, with
`Actual: 0.000000` proving the field was never written. One swap back restores it.

Pick a single-field round trip for that, never `independence.xml`: its diagram already calls all
eight accessors, so the restoring swap would meet the duplicate-name refusal.
