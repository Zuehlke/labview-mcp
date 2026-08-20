# Routing between AIXML and pylabview — how the switch can be automatic

The question this answers: how do the MCP tools decide, without being told, when to use pylabview
instead of AIXML?

The short version: **the decision is measurable, not a guess** — but only for two of the three
cases, and the third must stay explicit. Getting that split wrong is how a router silently ships a
broken VI.

## 1. The trigger rate is already measured, and it is not small

`LabVIEWMCP --corpus` exports every VI in a tree and hands each export straight back to
`ValidateAIXML`. Over LabVIEW 2026 (from `docs/aixml-reference.md` §9):

| | |
|---|---|
| VIs attempted | 1687 |
| exported | **1679 — 99.5 %** |
| exported *and* regenerated | **627 — 37 %** |
| exported and then failed to validate | **1052** |

So reading a VI out works almost always; reading it back in fails on **63 % of NI's own code**. For
*editing existing code*, AIXML is the minority path — which is exactly why routing is worth
automating rather than leaving to whoever remembers.

And the dominant failure is the one pylabview demonstrably fixes:

| Cause | Count | pylabview? |
|---|---|---|
| `Error 53` — a `Call` to a project- or library-local subVI | **737** | **yes** — §3.13 bound a subVI from an ordinary directory and ran it |
| `Error 1`, no detail | 146 | unknown |
| `Event Data Node` / `no events defined` | 54 | **yes** — §3.11, §3.12 |
| other validation errors | 51 | case by case |
| `Static VI Reference … SubVI is missing` | 31 | likely |
| `Property Node` / `Invoke Node` not rebound | 23 | likely |

70 % of all regeneration failures are the subVI boundary, and that is a capability AIXML does not
have at all rather than a bug to fix.

## 2. The predicate — two checks, because one is not sound

**Check A — validate the untouched export.** Export the VI, hand the export straight back to
`ValidateAIXML` without changing a byte. `errorCode 1` means AIXML cannot rebuild this VI, so an
edit must not go that way. This is cheap and it is what the corpus sweep measures.

**Check B — scan the export for silently-unsupported families.** Check A is *not enough*, and §9
says why in one table:

| | unresolvable `Call` | unsupported node family |
|---|---|---|
| `ValidateAIXML` | `errorCode 1`, names the subVI | **`errorCode 0`** |
| `ConvertAIXMLToVI` | refuses, nothing written | **`errorCode 0`, VI written** |
| result | nothing happened | **container built, configuration silently discarded** |

**`Event Structure` is NOT the silent one, and this section said it was.** The paragraph here
used to claim §3.11 measured validation returning `errorCode 0` on an event structure, generation
returning `errorCode 0`, and the result coming back with every `CaseFrame` gone and one frame
labelled `[0] Timeout`. That contradicted FINDINGS.md §3.11, which says the opposite in the same
words it was written in — NI's own export of `User Event Generation.vi` "does not even validate",
15 errors. §3.11 was right; the sentence above it was a Timed Loop finding that had drifted onto the
wrong construct while the `Timed Loop` paragraph below was being corrected.

Re-measured 2026-08-22 on `State Machine Fundamentals.vi`, whose `Wait for Event` state holds a
three-frame Event Structure. `ValidateAIXML` on the untouched export returns `errorCode 1`:

```
Event Data Node: Cluster is invalid or empty
Event Structure: One or more event cases have no events defined.
```

So Check A *does* catch an event structure, by name, and `pylv_route` reports it through
`validateErrorCode` rather than through the blacklist. Note what the export itself gets right: all
three `CaseFrame`s are present with their specifiers (`selector=" &quot;Run State 1&quot;\3A Value
Change "`). The export is faithful **for this construct**; it is the **generator** that cannot read
it back. Check B's `Event Structure` entry is therefore belt and braces like `Timed Loop`'s — keep
it, but do not describe either as quiet.

**Read "faithful" narrowly: it is an `Event Structure` result and `Timed Loop` breaks it.** This
sentence carried no qualifier until 2026-08-22, and read generally it is wrong — a `Timed Loop`
export drops the configuration node on both sides, so the timing is absent from the AIXML entirely
rather than merely unreadable on the way back. Two VIs 3 703 bytes apart exported byte-identically
apart from the name. `FINDINGS.md` §3.16 has the measurement; the practical consequence is that for
a Timed Loop there is no "compare the export" check to run, because the export never had the data.

**The table above still describes a real risk**, just not one these two constructs demonstrate.
Treat it as the shape of the danger Check B exists for, and `docs/aixml-node-gaps.tsv` as where the
genuinely quiet families are to be found.

**`Timed Loop` is NOT silent, and this section said it was.** Measured on NI's own
`Timed Loop Abort.vi`: handing its untouched export back returns `errorCode 1` with
`Unsupported node type: Timed Loop`, by name. So Check A catches it and Check B's `Timed Loop` entry
is redundant - kept as belt and braces, but the two constructs fail in opposite ways and pairing
them was wrong. Read the table above as the shape of the risk, not as a claim that every unsupported
family is quiet.

So Check B is a structural scan of the export for the families NI publishes as unsupported —
`Event Structure`, `Timed Loop` — plus the node kinds in `docs/aixml-node-gaps.tsv` (310 rows).

**Check C — round-trip and diff, when it matters.** Generate to a scratch path, re-export, diff
against the original export. This catches silent degradation by construction rather than by
blacklist. It costs one generate plus one export, so it is the gate for a real edit, not for a
routing hint.

## 3. What may be automatic and what may not

| Operation | Route | Automatic? |
|---|---|---|
| **read** a VI (diagram, dependencies, description, icon) | either | **yes** — prefer AIXML when the service is up because it is 37× smaller; fall back to pylabview when `lvai_status` finds no service. Today those tools simply fail. |
| **read** with no LabVIEW at all (CI, a checkout, no licence) | pylabview | **yes** — the only route |
| **create** a new VI | AIXML | **yes, and only** — pylabview cannot author from nothing (§3.5 item 1) |
| **edit** an existing VI | depends on Checks A+B | **routing yes, execution no** — see below |
| **icon**, layout, decorations | pylabview or `lvai_set_vi_icon` | yes |
| **place a comment** at a chosen spot on the diagram | pylabview | **yes, and only** - AIXML has no coordinate at all, so placement is not something it declines but something the format cannot express (`docs/pylabview-comments.md`) |

**Why an edit must not auto-execute through pylabview.** An AIXML edit is expressed at the level of
the request — "add error handling", "call this VI instead". A pylabview edit is a surgical change to
an object heap: in §3.12 it was six specific text edits, and I only knew which six because AIXML
generated me a reference VI to diff against. That cannot be synthesised from a high-level ask.

So the router's honest output for an edit is **a decision plus a reason**, not a silent switch:

```json
{ "route": "pylabview",
  "routeReason": "AIXML cannot rebuild this VI: ValidateAIXML on the untouched export returned
                  errorCode 1, Unsupported SubVI: MyLib.lvlib:Helper.vi",
  "aixmlRoundTrip": false,
  "silentlyUnsupported": ["Event Structure"] }
```

This follows the idiom the server already uses — `lvai_convert_vi_to_aixml` reports `fromCache` and
`cacheNote` rather than hiding which path it took — and it satisfies `CLAUDE.md`'s standing rule to
say which route was taken and why the official one was not enough.

## 4. Tool surface

A separate namespace, so nothing about the existing contract changes:

| Tool | Does | Needs LabVIEW |
|---|---|---|
| `pylv_status` | is the bundle provisioned, which Python, which pylabview commit | no |
| `pylv_route` | Checks A+B for one VI; answers `route` + `routeReason` + the evidence | yes (for Check A) |
| `pylv_extract` | VI → XML bundle, names annotated from the two TSVs | no |
| `pylv_rebuild` | XML bundle → VI, with the §5 gates below | no |
| `pylv_read_metadata` | dependencies, description, connector pane, icon — no LabVIEW | no |
| `pylv_bind_subvi` | retarget a subVI call to any path (§3.13) | no |

`pylv_route` is the automatic part. The existing `lvai_*` tools stay exactly as they are; what
changes is that an agent asked to edit a VI calls `pylv_route` first and is *told* which way to go,
with the measurement attached.

**Built as of 2026-08-20:** `pylv_status`, `pylv_extract`, `pylv_rebuild`, `pylv_route` - in
`Tools\PyLabviewTools.cs` over `Infra\PyLabview.cs`, 14 tests, build and suite green at 848 tests.
`pylv_read_metadata` and `pylv_bind_subvi` are still designs. FINDINGS section 8 has the detail.

## 5. Process control — the gates

Each gate exists because something in this document went wrong without it.

| # | Gate | Why |
|---|---|---|
| 0 | **Pre-flight**: `roundtrip.py` must come back content-identical on the target VI | pylabview round-tripped 38/38 of NI's files, but that is NI's corpus, not a customer's. Refuse to edit a file that cannot survive an untouched round trip. |
| 1 | **Backup** the original `.vi` before writing | a rebuild overwrites in place |
| 2 | **Release the path with `lvai_close_vi`** before rebuilding, and again before verifying | not a write problem — a *stale read* problem. Measured below. |
| 3 | **Post-check `ExecState == 1`** through VI Server | `0` is `eBad`; this is what caught the broken intermediate in §3.13 |
| 4 | **Post-check by re-export**: AIXML-export the result and confirm the intended change is present | the only way to see silent degradation. §3.12 confirmed the second user event this way. |
| 5 | **Record provenance**: which tool touched the VI, which pylabview commit, which tables | the annotation tables are version-specific; a VI edited under a stale table needs to be findable |

Gates 0, 3 and 4 are the ones that are cheap and catch real defects.

### Gate 2 in detail — this section had it wrong

It used to read "**write to a new path**, never over a path LabVIEW has loaded", citing Error 1357
and Error 1051. That is the right advice for the *AIXML* route and the wrong model for pylabview,
because pylabview does not go through LabVIEW to write. Measured on `WinkelDemo.vi` while it was
open in the IDE with its project active:

| step | result |
|---|---|
| run it | `ergebnis` = **90.01745** |
| overwrite the `.vi` on disk with a different build | **succeeded** — LabVIEW does not lock the file |
| run again, **without** closing | **90.01745** — the *old* value, from memory, while the disk held something else |
| `lvai_close_vi` | `closed: true` |
| run again | LabVIEW loaded the **new** bytes and reported their real state |

So the hazard is not a refused write, it is a **silent stale read**: a verification step that runs
without releasing the path first confirms the VI you replaced, not the one you built. That is a
false pass, which is worse than an error.

And the fix already ships: `lvai_close_vi`. Its two documented preconditions are exactly why
`CLAUDE.md` says to generate into a project — a project must be *active* in the IDE and the VI must
be a *member* of it. That is what makes the escape hatch available at all, and it explains the two
failures that prompted the original wording: `SelTest.vi` (Error 1051) and `Caller3.vi` (would not
open) were loose VIs belonging to no project, so there was nothing to close them through.

**Writing to a new path stays the fallback**, not the rule — for a VI that is not a project member,
where `lvai_close_vi` cannot reach it.

## 6. What is unmeasured, and would be measured before building

* **How often Check A + Check B disagree with Check C.** The blacklist could be incomplete; only a
  corpus run comparing the three tells us. `--corpus` already exists to be extended.
* **The `Error 1, no detail` bucket — 146 VIs.** Nobody knows whether pylabview helps there.
* **pylabview on a real customer project**, not NI's examples. §5 already flags this; it is the
  single biggest open risk and it gates everything above.
* **Whether the `LIvi`/`LIfp`/`LIbd` raw fallback on class-based code hides the metadata
  `pylv_read_metadata` would want to read.** If it does, the highest-value item is worth less than
  it looks.
