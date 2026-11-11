# VI execution settings (VI Properties > Execution)

`lvai_set_vi_execution` sets every setting of the Execution page on an existing VI. Inline and
reentrant execution can make a big difference to performance (the user's request of 2026-11-11).
AIXML carries none of these settings. VI Server can write only seven of them. So the tool patches
the file:

1. close the active project;
2. `pylv_extract`;
3. patch the LVSR fields;
4. strip compiled code;
5. rebuild;
6. ask LabVIEW. Its execState and Error List gate the call, and so does what it reads back over
   COM.

The experiments are in `experiments/exec-settings-2026-11-11/`.

## The table, measured 2026-11-11

| Dialog setting | LVSR field(s) | file values | LabVIEW reads it via |
|---|---|---|---|
| Allow debugging | `Instrument.DebugCapable` | 0/1 | COM `AllowDebugging` |
| Reentrancy: non-reentrant | `Execution.IsReentrant`, `PooledReentrancy` | 0, 0 | COM `IsReentrant` |
| Reentrancy: shared clones | same | 1, 1 | COM `ReentrancyType` 0 |
| Reentrancy: preallocated clones | same, plus `Execution.SaveParallel` 1 | 1, 0 | COM `ReentrancyType` 1 |
| Inline subVI into calling VIs | `Unknown.InlineStg` (2 on, 1 off), plus `Execution2.ShouldInline` | 2/1, 1/0 | the Error List (below) |
| Priority | `Execution.Priority` 0..5, plus `IsSubroutine` for subroutine | background 0 .. subroutine 5 | COM `ExecPriority` = file + 1 |
| Preferred execution system | `Execution.PrefExecSyst` | same as caller -1, user interface 0 .. other 2 5 | COM `PreferredExecSystem`: 7 = same as caller, else file + 1 |
| Enable automatic error handling | `Execution2.DefaultErrorHandling` | 1 = on | — (VI Server write `true` set 1) |
| Run when opened | `Execution.RunOnOpen` | 0/1 | COM `RunOnOpen` |
| Suspend when called | `Execution.HasSetBP` | 0/1 | COM `SuspendOnCall` |
| Clear indicators when called | `Execution2.ClearIndMask` | 0/1 | — (kept by LabVIEW's own save) |
| Auto handle menus at launch | `Flags0C.AutoHndlMenus` | 0/1 | — (kept by LabVIEW's own save) |

How each row was settled:

- **Inline.** `InlineStg=2` set ALONE on a non-reentrant VI with debugging on made LabVIEW break it
  with two Error List lines:
  - "This VI is configured with inlining and debugging enabled."
  - "This VI is configured with inlining enabled and reentrant execution disabled."

  `ShouldInline=1` alone left the same VI executable. With `ShouldInline=1`, `InlineStg=2`,
  preallocated reentrancy and debugging off, the VI is eIdle. Of 600 vi.lib VIs scanned, the 77
  with inlining on all carry `ShouldInline=1 InlineStg=2 SaveParallel=1 DebugCapable=0`.
- **Suspend when called.** `HasSetBP=1` alone read `SuspendOnCall=True` over COM; `InStBit13=1`
  alone read False. A VI Server write of the property had set both fields.
- **Priority.** A plain VI reads file 1 / COM 2 (normal). NI's 92 subroutine VIs in vi.lib read
  file 5 with `IsSubroutine=1` / COM 6. A VI Server write of 3 landed as file 2.
- **Execution system.** COM writes of 1, 3, 6 and 7, saved by LabVIEW, read back as file 0, 2, 5
  and -1. A COM write of 0 is refused ("An input parameter is invalid").
- **Clear indicators, auto handle menus, automatic error handling.** These were set in the file.
  LabVIEW then loaded the VI (eIdle) and saved a copy, and every field came back unchanged. The
  field names are pylabview's.

## What the tool does about it

- `inline: true` implies `allowDebugging: false` and `reentrancy: "preallocated"` when they are not
  given. An explicit contradiction is refused before anything is written.
- `priority: "subroutine"` sets `IsSubroutine` too.
- A VI LabVIEW still holds keeps serving its old settings after the file is rewritten. The tool
  closes the active project first (`closeProject`). If the COM read-back disagrees with the
  request, the call fails and says so.
- With no setting given, the tool only reads: `fileAfter` lists all ten as the file holds them.

A NEW VI's reentrancy can also come from the AIXML trailer `<?lvmcp exec ...?>`
(`docs/aixml-reference.md` §9). The other settings need this tool after generation.
