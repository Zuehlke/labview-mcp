# A pylabview edit runs the OLD VI: stale compiled code

**Symptom.** A VI generated from AIXML is edited through pylabview — most often by
`lvai_generate_vi`'s `panePattern`, which is a `pylv_apply` `conpane` step — and afterwards:

- on **32-bit** LabVIEW (measured here) it runs and returns a **wrong result with no error**:
  `error out` clean, `lvai_exec_state` answering `1` (eIdle), every tool answer `ok`;
- on **64-bit** LabVIEW (reported by a user, not reproducible here — there is no 64-bit LabVIEW on
  this station) opening or running it **crashed LabVIEW**, repeatably.

The only thing that names the cause is LabVIEW's own log, `%TEMP%\LabVIEW_<bits>_<ver>_…_cur.txt`:

```
DWarn 0x65A8655C: [VI "Repro Caller FSB.vi" (0x2c8aeef0)] was trying to execute when it had not
been compiled correctly
```

**Cause.** A VI converted while its callees are loaded is saved WITH compiled code — `VICD` blocks
plus `GCDI`, `NUID`, `SUID`, `BNID`, and `SourceOnly="0"`. pylabview cannot parse those blocks and
copies them through unchanged, which is exactly what makes its round trip lossless. After an edit
they still describe the VI as it was before, and LabVIEW runs them instead of recompiling.

**Fix, since 2026-09-29.** `pylv_apply` runs `scripts/pylv-strip-compiled.py` after the operations
and before the rebuild, on every edit — so `lvai_generate_vi`'s `panePattern`, `lvai_generate_vis`,
`lvai_add_class_method` and `lvai_lunit_add_test_method` all get it, since every one of them reaches
pylabview through `pylv_apply`. The VI is then source-only and LabVIEW recompiles it on load. After
the rebuild the saved FILE is checked for the four bytes `VICD`, and a leftover fails the call as
`failedAtStep: stripCompiled`. The event tools (`lvai_generate_vi_with_events`,
`lvai_set_event_data_fields`, `lvai_wire_dynamic_events`) have run the same strip since 2026-09-10
for the same reason (`Error 47, Unknown heap`); `pylv_apply` had been the one route without it.

**The side effect.** A VI that went through `pylv_apply` is SOURCE-ONLY, that is "Separate compiled
code from source file" in its properties, the same state NI's own instantiated `.vit` templates are
in. That is not a defect, but it is a visible difference from a VI that was never edited.

## The measurement

Reproduced from a user's crash report whose own suspect was a **Flat Sequence** around a Call to a
loaded project VI. Minimal repro under `C:\temp\FlatSeqRepro` (not committed): a callee
(`y = 2x`, `positive = x > 0`) listed in a project and OPEN, and a caller with an outer error Case
using `selectout`, the Call, an `Increment`, a nested boolean Case and several tunnels — once
inside a single-frame Flat Sequence, once without. Input `x = 3`, correct output `8`. Each faulty
file was run under a FRESH name, byte-identical copy, so that no same-named VI still in memory
could answer instead.

| arm | Flat Sequence | `panePattern` | result | `VICD` in file |
|---|---|---|---|---|
| A | yes | — | **8** | yes |
| B | yes | 4834 | **0** — wrong, no error, `execState 1` | yes |
| C | no | — | **8** | — |
| D | no | 4834 | **0** — wrong, no error | yes |
| E = D, compiled code stripped | no | 4834 | **8** | no |

So the **Flat Sequence is not the variable** — A and C agree, B and D agree — and the fault follows
the pylabview rebuild. E is the control that settles it: the same file as D with only the compiled
code removed. Arm A was also opened in the IDE and rendered; nothing happened.

Arm A carries `VICD` and runs correctly: compiled code is not harmful in itself, only compiled code
that no longer matches the diagram.

**Acceptance of the fix** is a unit test over that very file: arm A's VI is committed as
`tests/LabVIEWMCP.Tests/Fixtures/compiled-code/Compiled Caller.vi`, and
`PyApplyCompiledCodeTests` runs a `conpane` edit through `pylv_apply` with the real pylabview bundle
and asserts no `VICD` remains. **With the strip removed the test fails** — the negative control was
run, and the file check itself answered `failedAtStep: stripCompiled`, so both layers catch it
independently. Not yet re-run against a live LabVIEW through the rebuilt server: the client has to
be restarted first.

## Observations along the way, not investigated

- **`ValidateAIXML` accepted the project-local Call after the first convert.** Arm A's validation
  answered `Error 53, Unsupported SubVI: Repro Callee.vi` as `CLAUDE.md` describes; arms B, C and
  D — same callee, same open project, run afterwards — validated with `errorCode 0`. `CLAUDE.md`
  says the validator "refuses the same document in every arm". What changed between A and B is
  that a caller linking the callee was now in memory; not tested further.
- **Not every converted VI carries compiled code.** The callee, converted with NO project open and
  nothing loaded, has no `VICD`; every caller converted with the callee loaded has it.
  `pylv-strip-compiled.py`'s header says `ConvertAIXMLToVI` writes compiled code itself, from a
  2026-09-10 measurement; both may be true under different conditions. The strip is harmless on a
  file with none (it answers `already 1` / `dropped: (none)`), so the fix does not depend on which.
