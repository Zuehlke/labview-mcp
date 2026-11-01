# Cold build: producer/consumer with a user event and a CSV event log (2026-11-01)

Request: "erstelle ein neues Producer consumer projekt beispiel mit einem User event und dem
logging der Events in eine CSV Datei." Built in `C:\Temp\PC_UserEvent_CSV`. One orchestrating
session, one `labview-vi-generator` agent for both VIs, with a two-VI build contract.

## Result

- `SubVIs\Append Event To CSV.vi`: Open/Create/Replace File ("open or create"), Get File Size,
  Set File Position (end), header `Timestamp,Source,Value` only into an empty file. No
  spreadsheet writer, so no "Replace existing?" dialog can come up.
- `Producer Consumer Event Log.vi`: the producer's event structure turns Send, Level and Stop
  into one user event of type `{Source, Value}`; the consumer's event structure appends each one
  to the CSV and ends on `Stop`.
- Both eIdle, contract `verify` ok on both waves, no crash, no dialog.
- Driven with `signalsJson` (on a copy whose latched buttons were switch action, because a
  latched boolean cannot be signalled, Error 1193): `Events Logged = 3`, and the CSV had the
  header plus three lines, Stop included.

## Measured

| | |
|---|---|
| wall clock, start to delivered | 8 min 13 s (contract 30 s, agent 7 min 38 s) |
| agent requests | 60 |
| agent tool calls | 67 (time inside tools about 1 min; the rest is model time) |
| agent context, first / max | 93 k / 183 k |
| agent cache-read tokens | 8.33 M |
| agent cache-creation + output tokens | 183 k + 41 k |
| orchestrator (to delivery) | 13 requests, 1.51 M cache-read, 86 k creation, 9 k output |
| VI run, three events | 1.4 s from the first to the last CSV line (700 ms signal gap) |

Figures from the session transcripts (`tally_run.py`-style count over the `.jsonl`; tool_use
blocks appear twice there, so the per-tool counts were halved and checked against the
completion notice's 67).

## What cost turns

- **The composer does not know `Set File Position` (primResID 8073).** The subVI went to the
  grpc backend for this one generate. A first grpc validate failed on the guessed terminal name
  `time stamp` of Format Date/Time String; the input was left unwired (defaults to now).
- **A payload field named like an event-data field.** The payload's `Source` clashes with the
  event's own `Source`, and the composer picks Event Data Node fields BY NAME. The agent wired
  labelled placeholders and ran `lvai_set_event_data_fields` with field indexes (4, 5); the
  data node's heap uid (20992) is not its AIXML uid, and the first try with the AIXML uid failed.
  A regeneration of the main VI from its AIXML needs that step again. Naming payload fields so
  they cannot clash (`Event Source`) avoids it.

## Fixed the same day

- `Set File Position` is in the composer's tables (`primitive-names-extra.tsv`,
  `terminal-names-learned.tsv`). The parm indexes come from NI's converter's build of a probe read
  back by our reader: 7 `refnum`, 6 `offset (in bytes) (0)`, 5 `from (0:start)`, 4 `error in`,
  3 `refnum out`, out `error out`. `offset (in bytes)` and `offset` are refused by ValidateAIXML;
  `offset (in bytes) (0)` is the only spelling it takes.
- A payload field named like an event field now resolves to the PAYLOAD (`docs/aixml-backends.md`).
- Proof by reproduction: both VIs regenerated on pylabview alone, the main VI reading
  `fields="Source,Value"` directly with no placeholder and no repoint step. Executable, contract
  `verify` ok, and driven: `Events Logged = 3`, CSV `Send,Regen ok` / `Level,7.25` / `Stop,`.
  The reader reads the node back as `fields="Source,Value"`, so the round trip holds.
- The delivered `Producer Consumer Event Log.vi` was regenerated that way; the placeholder version
  is kept beside it as `*.placeholder.*.bak`. Its icon is now the derived `PRDCR|CNSMR|EVENT`.
- Tests: `PylabviewSetFilePositionAndPayloadClashTests`.
