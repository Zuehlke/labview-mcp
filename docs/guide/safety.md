# Safety, status and known limits

This page is the small print behind the short status note in the
[README](../../README.md#status-research-grade-and-honest-about-it). Read it before you let an
assistant write to code you care about.

**Contents**

- [Read this before you let a robot touch your VIs](#-read-this-before-you-let-a-robot-touch-your-vis)
- [Status](#status--read-this-before-you-point-it-at-code-you-care-about)
- [Caveats](#caveats)

---

## 🧪 Read this before you let a robot touch your VIs

**Not affiliated with, endorsed by, or supported by NI or Emerson.** Nobody at NI asked for
this, nobody at NI owes you anything for it, and nobody at NI is on the hook when it misbehaves.

**The plumbing is theirs, and it is public.** The server inside LabVIEW is
[ni/grpc-labview](https://github.com/ni/grpc-labview), NI's own open source, MIT licensed gRPC
stack. Nothing was cracked open to get here. It is a *generic* server, which is rather the
point: it serves whatever schema LabVIEW registers into it at runtime, and it ships with gRPC
reflection switched on so that a client can ask what that is. We asked. It answered.

**What it happens to be serving is another matter.** `lvai.LVAI` is not a published NI API.
No `.proto` in the install, no documentation, no version policy, and no promise that any of
these RPCs will still be there next quarter. NI's own repo already warns that generated names
are subject to change and that none of it is covered by NI Technical Support. Believe them.
They are being polite about it.

### Therefore

* **It will break, and probably on a Tuesday.** A LabVIEW update, a tweak to the AI feature, a
  shifted comma in the AIXML dialect, a new .NET runtime, your MCP client developing opinions.
  Any one of those is enough. After every LabVIEW upgrade, run `lvai_dump_schema` and find out
  what moved while you were asleep.
* **When it breaks, it is not a LabVIEW bug.** Please do not open a ticket with NI about a tool
  NI did not write and cannot see. That burns an engineer's afternoon and gets you nowhere.
  Open an issue here instead, where somebody knows what actually happened.
* **Nobody is liable for the outcome.** Not NI, not Emerson, not Zühlke, not whoever last
  touched `main`. Lost work, mangled projects, a generated VI that confidently drives real
  hardware into a wall: all yours. See [`LICENSE`](../../LICENSE), specifically the part in shouty
  capitals about no warranty of any kind.
* **This writes and runs code on your machine.** Work on copies. Commit first. Keep the
  mutating tools behind a confirmation prompt, and do not allow-list the whole server just
  because the prompts are irritating. They are irritating on purpose.

LabVIEW, NI and ni.com are trademarks of National Instruments Corporation, used here only to

---

## Status — read this before you point it at code you care about

**This is not production-tested software.** It is a working research project: everything
documented here was measured on a real LabVIEW installation, and none of it has been through a
production validation cycle, a regression suite on customer code, or use by anyone but its
authors.

Concretely, what that means for you:

- **Tools that write are genuinely destructive.** `lvai_convert_aixml_to_vi` overwrites a `.vi`
  without asking. `pylv_rebuild` overwrites one without LabVIEW ever seeing it. Regenerating a
  VI discards its diagram layout, its decorations and its icon.
- **The pylabview route edits a binary object heap.** The round trip was measured lossless on
  38 of 38 files, and that is a sample, not a guarantee. A malformed edit produces a `.vi` that
  LabVIEW may refuse to load — and, in one measured class of edit, one that **terminated
  `LabVIEW.exe` on load** (see [`docs/connector-pane-repair.md`](../connector-pane-repair.md);
  the capability was removed rather than shipped).
- **It drives NI's private, undocumented `lvai.LVAI` interface**, with no compatibility
  guarantee across LabVIEW versions.

**Work on copies, keep your code in version control, and commit before you let an assistant
loose on it.** Nothing here is covered by any warranty — see [LICENSE](../../LICENSE).


## Caveats

- **Private, undocumented NI interface.** No compatibility guarantee; expect changes between
  LabVIEW versions. Run `lvai_dump_schema` after a LabVIEW upgrade.
- **The port is ephemeral** — chosen at LabVIEW start, not configured. It is discovered by
  looking at `LabVIEW.exe`'s TCP listeners and probing each with a real `lvai.LVAI` call. A
  LabVIEW restart heals on the next tool call.
- **The connection is plaintext HTTP/2 on loopback.** No TLS, no auth — anything on the
  machine that can reach the port can drive LabVIEW.
- **What the mutating RPCs actually do, measured against a live LabVIEW:**
  `ConvertAIXMLToVI` works — it generated real, runnable VIs. `OpenFile` works. But
  **`ApplyAIXMLToVI` is unusable, and the tool now refuses it by default** (pass
  `userAskedForThisByName` only when the user asked for the RPC by name). It failed with
  `Error 42 (generic)` in six distinct
  configurations — delta and full-state XML, a clean VI and a VI containing an Express VI, the
  VI open and closed, and with LabVIEW's own byte-exact canonical export as input. The sixth,
  on LabVIEW 2026 (26.3f0), was constructed to be the best possible case and still failed:
  a three-element self-contained VI whose AIXML round-trips byte-for-byte, an additive change
  (one `FreeLabel`, one fan-out `Indicator`) that `ValidateAIXML` accepts with `errorCode 0`,
  the VI closed, outside any library. `viBytesBefore == viBytesAfter` — nothing was written.
  **Since 2026-09-16 it answers `errorCode 0` with an empty message and still writes nothing** —
  measured twice on one VI, closed and then open in the IDE, the AIXML export identical both
  times. That is worse than the refusal it replaced: `errorCode 0` is what success looks like
  everywhere else here, so the only sound check is to export the VI afterwards and compare.

  **The likely reason, and the one untried route.** This RPC is the one behind LabVIEW's own AI
  code completion, which does work — so it is plausibly not broken but *session-bound*, usable
  only inside the context `MonitorCodeCompletion` establishes rather than as a standalone call.
  That inverts the direction: instead of calling Apply, you wait on the monitor, LabVIEW hands
  you a `request`, and you answer with `suggestions[].changes` — which is AIXML that **LabVIEW
  itself applies**. Editing an existing VI that way is untested here and needs a human to trigger
  the AI feature in the IDE, but it is the designed path and the only one not yet ruled out.

  `RunVIAsTopLevel`,
  `BuildFromBuildSpecification`, `FindPaletteItem` and `DropPaletteItem` are still only
  unit-tested against the fake server — start those on throwaway copies.
- **Not every VI can be regenerated.** `ConvertAIXMLToVI` rejects a `Call` to a project- or
  library-local subVI (`Unsupported SubVI`), and Express VIs fail the same way, so generated
  VIs must be self-contained. A whole DQMH module therefore cannot be generated at all.
- **No RPC creates a file container.** `ConvertAIXMLToVI` writes a `.vi`, but nothing writes a
  `.lvproj`, `.lvlib` or `.lvclass`, and `OpenFile` only opens a path that already exists. Write
  the XML yourself — see [Creating a project](#creating-a-project).
- **An empty AIXML export is not an empty VI.** A 100–200 byte export containing only the
  `<VI …/>` element means the diagram was not readable — and `ConvertVIToAIXML` still returns
  `errorCode 0`. Cross-check with `--diagram`: no `viImage` either confirms it.
- **No RPC returns a VI icon or a connector pane picture, but you can still get one.**
  `describe_vi`'s `infoJson` carries exactly `viName`, `viPath`, `viXml`, `viImage`,
  `controlsIndicators`, `subvisInfo`, `owningProjectPath`, `owningProjectName`, `errorCode`,
  `errorMessage`, `warnings` — `viImage` is the *block diagram*. The route to the other two
  pictures is to **generate a helper VI and run it**: `Open VI Reference` → Invoke Node
  `target="Print.VI To HTML"` → `Close Reference`, built with `ConvertAIXMLToVI` and driven by
  `RunVIAsTopLevel`. LabVIEW then writes `<stem>c.png` — the connector pane with the icon inside
  it. Full recipe, including the four things that each cost a debug cycle, in
  [`.claude/agents/labview-doc-generator.md`](../../.claude/agents/labview-doc-generator.md).
  The ActiveX equivalent (`VirtualInstrument.PrintVIToHTML`,
  [`scripts/Export-VIDoc.ps1`](../../scripts/Export-VIDoc.ps1)) needs the VI Server **ActiveX**
  protocol and did not work on the development station in six configurations — the COM object is
  created but inert (empty `Version`, `NullReferenceException` from `GetVIReference`).
- **`RunVIAsTopLevel` works against a real LabVIEW** — no longer only fake-tested. Two limits:
  it sets control values through a variant, so a **path control cannot be set from a string**
  (`Error 91 … Control Value:Set`; use a string control plus `String To Path` on the diagram),
  and it reads indicators back as strings, so any **non-string indicator returns `Error 91`**
  even though the VI ran correctly. Judge success by the VI's own outputs, not by `errorCode`.
- **To read many VIs, use `ConvertVIToAIXML` with `returnContent: false`, not `describe_vi`.**
  Both return the same AIXML, but `describe_vi` always includes `viImage`, a base64 PNG of the
  block diagram, in the tool result. Writing the XML to disk instead keeps the responses to four
  fields per VI.
- **Two whole categories of file are unreadable.** `describe_vi` rejects a `.ctl` with
  `errorCode 5001 — Unsupported VI type`, so **control typedefs cannot be read at all** — which
  matters because that is where DQMH keeps every event's argument cluster. And a password-protected
  VI returns `errorCode 5002`, which covers the entire Delacor DQMH scripting toolchain. Both are
  hard walls, not timeouts: no argument or retry gets past them.
- **Monitor contention:** `NigelLocalService` may already be attached to those streams.
  Whether a second client also receives events is unverified — a timeout can mean "no user
  activity" *or* "Nigel consumed it". Closing the LabVIEW chat window removes the contention.
- `SearchInfoCache` returned an empty list on a station whose cache is not populated. Empty
  is not necessarily an error.

