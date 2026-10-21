# Vendored third-party code and how the bundle is put together

## What is committed here

`vendor\pylabview\` — the upstream Python sources, unmodified.

| | |
|---|---|
| Project | [mefistotelis/pylabview](https://github.com/mefistotelis/pylabview) |
| Commit | `69768647c18d2d792a259b69884b2433761c3a4f` |
| Date | 2026-07-30 |
| Licence | MIT — `vendor\LICENSE-pylabview.txt` |
| Size | 20 files, 1.4 MB |
| Local changes | **none** |

Vendored rather than a submodule, deliberately: the version is pinned, a fresh clone works
offline, and there is no submodule step for anyone building this. The price is that upstream fixes
have to be pulled in by hand — 879 commits since 2013 and 38 in 2026, so that is a real cadence.
Re-vendor by copying the `pylabview/` package over `vendor\pylabview\` and updating the commit
above.

**Keep `local changes: none` true.** Every name pylabview lacks is added from the outside instead —
`experiments\pylabview\annotate_names.py` writes them in as XML comments, which needs no upstream
change at all. A fork here would cost the ability to take upstream fixes, which is the whole reason
the annotation route was measured in the first place (FINDINGS §3.9).

## Patches - applied to the bundle, never to `vendor\`

Where upstream has an outright defect, the fix goes in `patches\patches.json` and `provision.ps1`
applies it to the **assembled copy**. `vendor\` stays byte-identical to upstream, so a new release
can still be taken by copying the package over it.

Four fixes, fifteen entries.

`probe-table-guard`: `LVblock.py` calls `getNumRepeats()` on the probe table
without checking it is a `RepeatedBlock`, and a VI whose probe table resolves to a `Cluster`,
`Number` or `NumberPtr` then dies with `AttributeError` and extracts nothing. Measured on a
production codebase at **32 of 900 VIs**, all from this one cause, all of them DQMH module VIs -
which is why NI's own example corpus never hit it. `patches.json` carries the reasoning and the
measurement; the crash is in cosmetic code, so no content was ever beyond reach.

`class-link-tail-*` and `class-link-tcpi-*` (`LVlinkinfo.py`, 2026-10-12, replacing the
`class-link-cache-extra-*` entries of 2026-10-11): from file version 16 LabVIEW writes a tail after
every class link record's API link cache content - the class's ancestors, one byte, and from
version 20 a list of classes with their parents - and LIbd holds `TCPI` records (a type cast's link
to a class) that upstream does not know. Upstream then leaves the WHOLE link block binary. The old
entries read a fixed 8 bytes, which was right only for a class with no parent. With the fix 497 of
NI's 509 class-linked examples (all but three LabVIEW 8.2 files) extract every LI block as XML and
rebuild it byte-identical; it is also what lets the composer write class calls at all
(`docs/aixml-backends.md`, "Calling a class DIRECTLY"). Both version bounds are bracketed by
loading composed files in LabVIEW 2026 with and without each part.

`variant-consolidation-*` (`LVdatafill.py`, `LVclasses.py`, 2026-10-21, issue #96): a variant
inside a TYPE descriptor stores its types inline, and pylabview reads it so - but a variant in that
variant's DEFAULT DATA was always read as if its types were VCTP indices, which upstream's own
comment there calls a TODO. A Network Stream endpoint refnum is exactly that shape (its tag's
`NI_VAR_DATATYPE` attribute holds the stream's data type, and a cluster with a variant element puts
a nested variant in it), so every VI with an endpoint failed to extract - NI's own *Simple Network
Streams* example included - with `TypeError: object of type 'NoneType' has no len()` on
`TDObjectTag.ident`, the third error of a chain. `typedef-datafill-error-name` fixes the second: a
`NameError` on an undefined `client` in the TypeDef data-fill's error handler, which hid the real
exception. Swept over 1 824 VIs (every example plus the stream VIs in `vi.lib`), old against new
runtime: 2 fixed, 0 broken, every other extract byte-identical. `docs/network-streams.md`.

**The failure mode the mechanism is built against is silence.** A Find/Replace against a line of
upstream source stops matching when upstream edits that line - and then the bundle assembles
perfectly with the bug back in. Three things prevent that:

* `provision.ps1` **throws** unless the Find string occurs *exactly once*, and says why in words.
* After the smoke test it re-reads the assembled file and confirms the replacement is really there,
  so "applied" is never taken on trust.
* `PyLabviewPatchTests` checks the same patches against the same vendored tree, so a re-vendor that
  breaks a patch fails the suite rather than a developer's next provisioning run. It also asserts no
  replacement is *already* in `vendor\`, which is what catches someone editing the vendored tree.

Patches are single-line replacements on purpose. An earlier attempt used a backslash line
continuation and produced `unexpected character after line continuation character`, because the
backslash landed in front of a CR: the vendored files are CRLF and the writer preserves them byte
for byte.

Remove an entry once the fix lands upstream - the `upstream` field says where each one stands.

## What is NOT committed

The Python runtime. `provision.ps1` assembles it into `runtime\`, about **32 MB**. Two reasons it
stays out of git: 32 MB of binaries do not belong in version control, and a runtime assembled from
whatever CPython the machine has is more honest than a pinned copy that silently rots.

`runtime\` is in `.gitignore`. **But 659 of its files ARE tracked** (measured 2026-10-21 with
`git ls-files tools/pylabview/runtime`) - added past the ignore rule at some point - so a
re-provisioning shows up as a diff: the patched `app\pylabview\*.py`, `bundle.json`, and deleted
stdlib `__pycache__` files. Commit the first two with the patch that changed them; restore the
`.pyc` files (`git ls-files -d tools/pylabview/runtime | xargs git checkout --`).

## Why a real CPython has to travel with us

`LVblock.py` line 21 is `from PIL import Image` — an unguarded top-level import. Pillow is
therefore a hard requirement for the entire tool, not an icons-only extra. That rules out
IronPython, Python.NET and every other pure-.NET route, because Pillow ships a C extension.

It also rules out "just require Python": the point of the bundle is that the machine running the
server needs nothing installed.

## How the isolation works

A file named `pythonNNN._pth` beside `python.exe` — the same mechanism python.org's embeddable
distribution uses. When CPython finds it, it ignores `PYTHONPATH`, `PYTHONHOME`, the registry and
every `site-packages` directory, and takes its module search path from that file alone. Ours reads:

```
Lib
DLLs
app
```

So the bundle is a folder, not an installation: nothing on `PATH`, no registry keys, no user
site-packages, and no interference from any Python the user may install later. `provision.ps1`
proves this rather than assuming it — its smoke test fails if any `site-packages` path leaks in.

## Licences travelling in the bundle

| Component | Licence | Where |
|---|---|---|
| pylabview | MIT | `app\LICENSE-pylabview.txt` |
| CPython (interpreter + stdlib) | PSF License Agreement | `Lib\LICENSE.txt` from the source install |
| Pillow | MIT-CMU | `Lib\PIL` package metadata |

All three permit redistribution. If this ever ships to a customer, the three licence texts must
travel with it — that is what the table is for.

## Rebuilding it

```powershell
powershell -ExecutionPolicy Bypass -File tools\pylabview\provision.ps1
```

Discovery order for the source runtime: `-Source`, then `py -0p`, then `PATH`. Needs Python 3.8+ —
older XML parsers do not preserve attribute order, and pylabview's byte-exact rebuild depends on
it. If Pillow is not present in the source runtime the script stops and says so; it does not
install anything, because that is a download and the decision belongs to whoever runs it.

On a machine with no Python at all, unpack python.org's embeddable zip and pass `-Source` at it —
then Pillow still has to come from somewhere, so `-PillowFrom` a venv that has it.
