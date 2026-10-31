# A new VI has one required input

The user's rule of 2026-10-31. A VI generated COMPLETELY NEW makes `error in`
`connection="required"`; a new VI without `error in` makes its most important input required
(the author chooses which). A VI that already exists - extended or revised - keeps every flag
exactly as it was, or code that already calls it stops running. Outputs stay `recommended`.

The reason is the user's: a VI nobody has to wire can be dropped on a diagram and forgotten. A
required input forces it into the data flow, or a constant in front of it where that is the
logical implementation.

## Where it is enforced

| | new VI | existing VI |
|---|---|---|
| `lvai_check_aixml` | `newVi: true` (default): `errorInNotRequired` (warning, repaired by `fix`), `noRequiredInput` (error, never repaired) | `newVi: false`: nothing about `error in`, `fix` touches no flag |
| `lvai_generate_vi` | target file does not exist AND the diagram is measured (a deliverable) | target file exists: no flag touched, output repairs included |
| `scripts/aixml_lint.py` | `--new-vi`: `conn-error-in-not-required`, `no-required-input` | without the flag |
| helpers, carriers, class methods | not ruled - the author's flags stand (`PanePolicy.Authored`) | |

The decision is `BulkTools.PanePolicyFor`: the file's existence, then whether it is a
deliverable.

## Measured

**Probe, `C:\temp\RequiredInputProbe`, four arms over stdio:**

- A new VI whose `error in` was `recommended` came out `required` (repaired).
- Regenerating the same document onto that now existing path left the flags as they were.
- A new VI with no `error in` and no required input was refused with `noRequiredInput`, and no
  file was written.
- `newVi: false` on that document reported nothing.

**Project, `C:\temp\RequiredRuleTest`, through the agents (pylabview backend):**

- `labview-vi-generator` built three new VIs: `Clamp Reading.vi`, `Average Readings.vi` and the
  caller `Process Reading.vi`.
  - All three read back `error in` required.
  - Every other input and every output reads `recommended`.
  - The agent authored `required` itself, following its definition, so the repair path did not
    fire here. The probe above is the measurement of the repair.
- The caller calls an EXISTING VI, `Existing\Scale Reading.vi`, whose `error in` is
  `recommended`. That VI was not touched.
- `labview-vi-editor` then extended `Scale Reading.vi` with a new `Offset` input (default 0),
  regenerated onto its own path.
  - Every existing flag stayed `recommended`, `error in` included.
  - The new `Offset` is `recommended`, so callers that leave it unwired keep running.
- The agent's two checks on the edited AIXML:
  - With `newVi: false`: clean.
  - With `newVi: true`, run for comparison: `errorInNotRequired`, which is why an editor must
    pass `false`.
- The caller was not regenerated. It still gives `Clamped 8` for Raw 4, Factor 2, limits 0..10.
- All four VIs are `execState 1`, with no linker errors.
