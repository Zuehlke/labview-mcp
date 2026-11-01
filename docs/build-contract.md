# The build contract

A multi-VI application is built by several `labview-*` agents in parallel. Until 2026-11-01 each
agent settled the interfaces it needed for itself. The **build contract** settles them once,
before the first agent starts: one JSON file in the build folder (`build-contract.json`) naming
every VI with its path, owner, builder, connector pane and callees.
`lvai_check_build_contract` checks it before the build and compares the built VIs with it after
each wave.

## 1. Why - the ATM DQMH cold build of 2026-10-31

Five agents, about 30 min wall clock, about 1.7 M tokens (`docs/ideen-schneller-und-weniger-tokens.md`).
Three costs came from interfaces nobody had fixed in advance:

- **Sixteen `lvai_vi_terminals` calls** to read names and types another agent had just chosen.
- **A name clash found by a test run.** A loose `SubVIs\Deposit.vi` and the DQMH module's
  `Account Manager.lvlib:Deposit.vi` shared a file name. The Caraya suite's bare `Deposit.vi`
  call linked to the module's request VI and failed with Error 403681 ("module not running").
  The contract check names this before anything is built (`sameFileName`, `ambiguousCall`).
- **The main VI was the critical path (19 min).** Its agent could not start until every callee
  existed, because it learned their panes by reading them. With the panes in the contract, the
  scaffold can be authored while the callees are still being built. Only the generate step waits.

## 2. The format

```json
{
  "application": "ATM",
  "root": ".",
  "project": "ATM Solution.lvproj",
  "panePattern": 4833,
  "types": {
    "Accounts": "array{cluster{string.Account Number,string.First Name,string.Last Name,double.Balance}}"
  },
  "vis": [
    {
      "path": "SubVIs\\Read Accounts File.vi",
      "builder": "account-subvis",
      "purpose": "Reads the accounts file into the Accounts array.",
      "inputs": [
        {"name": "Accounts File", "type": "path", "conIdx": 0, "connection": "required"},
        {"name": "error in", "type": "cluster{bool.status,int32.code,string.source}", "conIdx": 11, "connection": "required"}
      ],
      "outputs": [
        {"name": "Accounts", "type": "@Accounts", "conIdx": 4, "connection": "recommended"},
        {"name": "error out", "type": "cluster{bool.status,int32.code,string.source}", "conIdx": 15, "connection": "recommended"}
      ]
    },
    {"path": "Libraries\\Account Manager\\Deposit.vi", "owner": "Account Manager.lvlib", "kind": "existing", "builder": "dqmh"},
    {"path": "Automatic Teller Machine (ATM).vi", "kind": "supplied", "builder": "main",
     "calls": ["Account Manager.lvlib:Deposit.vi", "SubVIs\\Read Accounts File.vi"]}
  ]
}
```

| key | meaning |
|---|---|
| `root` | the folder `path`s are relative to; default the contract's own folder |
| `panePattern` | the pattern of new VIs; default the station's `DefaultConPane`. A class member (`owner` ending `.lvclass`) is always checked against 4815, because the class tools re-pane it there |
| `types` | named type literals; a terminal writes `"type": "@Accounts"`. The long state cluster of a CLD application is written once instead of in every VI |
| `kind` | `new` (default): a VI this build creates, held to the house rules. `supplied`: a panel the program is grafted into, with no pane rules. `existing`: made by a scripting tool (DQMH, class providers) or already there; terminals are optional. `test`: a unit test VI |
| `owner` | the `.lvlib` or `.lvclass` the VI belongs to. It makes the qualified name `Owner:File.vi`, which a call must use where the bare name is ambiguous |
| `builder` | which agent builds it. Two builders in one folder are warned about (CLAUDE.md, "ONE AGENT, ONE OUTPUT DIRECTORY") |
| `calls` | callees, by qualified name, path or bare name. A name matching no contract VI is taken as an installation VI and listed as `externalCall` |
| terminal | `name`, `type` (the AIXML type literal), `conIdx`, `connection`, optional `description` |

Unknown keys are refused by name at every level, for the same reason as in every `casesJson`: a
key dropped in silence has made a test assert the opposite of what was asked.

## 3. What `mode=plan` checks (no LabVIEW)

For every `new` VI:

| rule | why |
|---|---|
| `noErrorIn`, `noErrorOut`, `errorInLabel` | every VI we create carries `error in` (not NI's `error in (no error)`) and `error out` |
| `errorClusterType` | `cluster{bool.status,int32.code,string.source}` on both |
| `errorInNotRequired` | a VI generated new has `error in` required (the user's rule of 2026-10-31) |
| `outputRequired` | an output that is required, or has no `connection` (which LabVIEW reads as required), breaks every caller that leaves it unwired |
| `patternWidened` (warning) | a VI with no `panePattern` whose conIdx exceed the station pattern but fit 4834 is checked against 4834, because the generator widens it by itself |
| `noConIdx`, `pane` | every terminal on a slot of the pattern; inputs on the left edge, outputs on the right; `error in` and `error out` in the bottom corners (`ConnectorPane.Review`, the same verdict as `lvai_connector_pane`) |

Over the whole contract:

| rule | severity |
|---|---|
| `duplicatePath` | error |
| `sameFileNameTwice` (two loose VIs with one file name: Error 1051) | error |
| `sameFileName` (owned and loose, e.g. the ATM `Deposit.vi`) | warning |
| `ambiguousCall` | error |
| `callsItself`, `callCycle` | error |
| `installationNamesake` (a palette VI of the same name) | warning |
| `sharedOutputDirectory` | warning |
| `duplicateTerminalName`, `noType`, `badConnection` | error |

The answer carries the **waves**. Wave 0 calls nothing in the contract; wave *n* calls only
earlier waves. Everything in one wave can be built in parallel. `callSkeletonsFor` returns a
ready `<Call>` per named VI, written by the same code as `lvai_vi_terminals`, so a caller needs
no export of a callee that does not exist yet.

## 4. What `mode=verify` checks (after a wave)

Each named VI that exists is exported (on the chosen backend, through the export cache) and its
PANE is compared with the contract:
- missing and extra terminals;
- direction, type (whitespace ignored), conIdx and connection.

A missing `connection` in the export counts as `required`, as LabVIEW reads it. A `supplied` VI,
or one with no terminals in the contract, is only checked for existence. With `vis` given, a VI
that is not built yet fails the gate. Without it, an unbuilt VI is listed and belongs to a later
wave.

## 5. The workflow

1. **Orchestrator, before any agent:**
   1. Read the spec and the supplied panel once.
   2. Write `build-contract.json`.
   3. Run `lvai_check_build_contract mode=plan` until `ok`.
2. **Spawn wave 0's builders** with the contract path and their VIs.
3. **At the same time, spawn the main-VI builder** to author its scaffold against the contract.
   It writes AIXML and plans the graft, and does not generate until its callees exist.
4. **After each wave:**
   1. Run `mode=verify` with that wave's VIs.
   2. If a VI differs, fix the VI, or change the contract and tell every builder whose VIs call
      it.
   3. Never build the next wave against a VI that differs.
5. **Test builders** take the contract too: their subjects' panes come from it, and their calls
   use the qualified names it prescribes.

## 6. Measured: the second ATM cold build, 2026-11-01

The same task as on 2026-10-31 (ATM, DQMH), built in `C:\Temp\ATM_DQMH_Cold_3`. It used everything
on the branch:
- the contract;
- the shortened tool descriptions and `CLAUDE.md`;
- compact answers;
- the graft and fixture fixes.

Effort levels and models were unchanged. The figures cover the five builder agents; the
orchestrating session is not counted, in either run.

| | 2026-10-31 | 2026-11-01 |
|---|---|---|
| wall clock, start to delivered | ~30 min | 28 min (incl. 2 LabVIEW crashes, ~7 min lost) |
| contract + spec + panel | - | 2.5 min |
| DQMH module + 4 events | 7.0 min | 3.4 min |
| account + UI subVIs | 7.8 min | 4.6 / 3.5 min (parallel) |
| handlers | 2.5 min | 0.4 min |
| Caraya | 7.5 min, 3 of 4 runs failed | 5.1 min, 41/0 first run |
| main VI (scaffold, graft, drive) | 19.2 min | 24.8 min active, incl. both crashes |
| agent requests | 227 | 169 |
| agent context at first request | 184-218 k | 68-93 k |
| cache-read tokens | 62.6 M | 30.1 M (−52 %) |
| cache-creation + output tokens | 1.64 M | 1.08 M (−34 %) |

**What worked:**
- Every leaf stage was 2-6x faster.
- No `vi_terminals` round trips for contract VIs.
- No name clash: the loose file VIs were named apart from the module's.
- The fixtures were reset by the tool.
- All agents but the main VI agent stayed under 200 k context throughout.

**What did not:** the main VI is still the critical path (97 requests, 101 k output tokens,
context up to 335 k). Its scaffold, composed by the pylabview composer, crashed LabVIEW twice on
load:
- the error was `DAbort 0x87FB5957: I expected a container refnum here!` in `proptyp3.cpp(970)`;
- the agent suspected the composer's event-data refnum for a Value Change frame on a CLUSTER
  control (Left/Right Buttons);
- the files are kept in `C:\Temp\ATM_DQMH_Cold_3\Scratch\crash\`.

The agent then generated the scaffold through NI's converter (grpc) and the build finished.

The agent's suspicion was wrong. The cause, found by bisecting the document afterwards, is a
queue whose element is an ARRAY: the composer typed `queue out`, `error out` and the Enqueue
behind them as arrays. Fixed the same day; `docs/aixml-backends.md`, "A queue of ARRAYS killed
LabVIEW on load".
Without the crashes the main VI would have taken roughly 17 min.

**Found on the way:**
- `lvai_generate_test` leaves the subject's REQUIRED `error in` unwired (the rule of 2026-10-31
  made it required), and a `setup` VI's too, while answering `ok: true`.
- The DQMH tool's `createdFiles` lists files other agents wrote in parallel (it diffs the
  folder).

## 7. Acceptance, 2026-11-01 - the ATM build of 2026-10-31, over raw stdio

A contract of 31 VIs was derived from that build's own exports (pylabview reader), so it is what
the build actually produced:

| part | VIs | kind |
|---|---|---|
| account SubVIs | 8 | `new` |
| UI SubVIs | 4 | `new` |
| controller VIs | 8 | `new` |
| DQMH request VIs | 4 | `existing`, owner `Account Manager.lvlib` |
| supplied main VI | 1 | `supplied` |
| Caraya test VIs | 6 | `test` |

`calls` come from each export's `Call` targets.

| run | result | time |
|---|---|---|
| `plan` | `ok`, 0 errors. The build kept every house rule. The checks named the 4 owned/loose name pairs (`Deposit`, `Get Balance`, `Verify Account`, `Withdraw`) and that `Find Account.vi` and `ATM Step.vi` sit on 4834. Answered 5 waves | 0.5 s |
| `verify`, all | `ok`, 24 of 24 compared VIs match, 7 checked for existence only | 4.4 s |
| negative control: one type changed, one conIdx moved, one terminal invented | `ok: false`, all three named; the untouched VI beside it still `match` | 0.3 s |
| the ATM clash: `Test Deposit.vi` calling bare `Deposit.vi` | `ambiguousCall`, fix: rename `SubVIs\Deposit.vi` | 0.2 s |

**The first run found two defects, one in the tool and one in the acceptance script.** The tool
defect: before the fix, the `plan` run reported 15 pane errors. Those were conIdx 16-19 checked against the station's 4833,
on the two VIs that sit on 4834. Since then, `plan` follows the generator: a VI without its own
`panePattern` whose conIdx fit 4834 is checked against 4834, and the `patternWidened` warning
says so.

The script defect: it wrote the loose `Get Balance.vi` and the module's `Get Balance.vi` into one
export folder and read the wrong file back. That is the same name clash, one level up, and
`verify` answered it with five `differs`, which is what it is for.
