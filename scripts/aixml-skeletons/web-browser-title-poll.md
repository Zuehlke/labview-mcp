# `web-browser-title-poll.xml` — a Web Browser program that POLLS instead of calling once

Two documents, generated in this order:

| document | VI |
|---|---|
| `web-browser-read-page-title.xml` | `Read Page Title.vi` - one `Execute JavaScript` call: the title once the page has loaded, `(no title)` for a loaded page without one, the previous title otherwise, and `Ready?` when the browser answered at all |
| `web-browser-title-poll.xml` | the program: it calls `Read Page Title.vi` by bare name, so that VI must be OPEN when this one is generated (`lvai_open_file` with the project and `viPaths`) |

An event loop writes every new `URL String` into the `Web Browser Control` and ends on `Stop`. A
second loop calls `Read Page Title.vi` every 250 ms on a `WB Ref` stand-in that
`lvai_bind_control_references` turns into a bound reference after the graft. While the browser is
not ready - `Error 53` right after `FP.Open`, or a page still loading - the previous title is kept,
so the next poll retries instead of the program failing.

**AT START-UP the program loads the URL already in `URL String`**, as NI's `Display a URL.vi` does:
a `URL Ref` stand-in (`ref{LV.String}`) reads the value before the loops, and the POLL LOOP writes it
back with `Value (Signaling)` - once, on the first iteration where `Read Page Title.vi` answers
`Ready?` (a `Start Pending` shift register keeps it to one). That fires the `URL String` Value Change
frame, so the start-up load and every later change take ONE path.

## History of this skeleton

- **Round 1-3** (2026-10-02, `docs/keep-supplied-front-panel.md` §6g): the poll shape, measured on
  NI's `Display a URL.vi` and `Navigation History Methods.vi`.
- **Round 4** added the start-up load as an IMMEDIATE `Value (Signaling)` right after `FP.Open`, and
  `(no title)`. It showed `Page One` at start-up in that build.
- **Round 5 found the immediate signal losing its navigation, 2 of 2** - the browser read back
  `about:blank`. Its scaffold gates the signal on the browser's first answer and moves the title read
  into `Read Page Title.vi`, which took the diagram from 1977 px to 1619 px. **That scaffold is this
  skeleton**, with its four navigation frames removed. Measured the same day, this document on NI's
  `Display a URL.vi`: generated 1466 x 609 px, 9 stages, `execState 1`; grafted, bound
  (`sinksReconnected` 2 and 1), and run - see "Reading an empty `Page Title`" for the numbers.

## Why polling, and why this shape

**`Execute JavaScript` cannot be called once.** It needs the control's panel open in the running
instance, and even after `FP.Open` the first call can answer `Error 53` while the browser
initialises (measured twice on cold starts). A load poll is also how a program learns that a
navigation has finished - and, since round 5, when the browser is ready to take the start-up load.

**The obvious shape - an Event Structure Timeout frame - is not authorable**: AIXML has no timeout
terminal on an Event Structure (`docs/cold-build-atm-cld.md` §4). So the poll is a SECOND loop paced
by `Wait on Notification` with a 250 ms timeout. The notifier is what stops it: the `Stop` frame
sends a notification, `timed out?` turns FALSE, and the loop ends without a stop flag being polled.

| element | role |
|---|---|
| `Obtain Notifier` (`bool`) | the stop signal shared by both loops; released after both end |
| `FP.Open` on an unwired `{LV.VI}` reference | opens the program's OWN panel - `This VI` |
| `Wait on Notification`, `timeout in ms (-1)` = 250 | paces the poll and ends it |
| `Read Page Title.vi` | the script, five lines (`&#10;` in the value, `\3A` for the colon): `if(d.readyState!='complete'\|\|d.URL=='about:blank') return 'LV_PENDING'; return d.title\|\|'(no title)';` - a FUNCTION BODY, so it needs `return`. PENDING is a document still loading or the browser's initial `about:blank`, which is `complete` with an empty title. Its own `error out` is its `error in`: script errors while the browser starts are expected |
| `URL Ref` -> `read+Value` before the loops, `write+Value (Signaling)` in a Case gated on `Ready? AND Start Pending` | the start-up load, through a bound reference - a Local Variable would stay bound to the duplicate the graft deletes |
| `No Error` constant into the event loop's error SHIFT REGISTER | the event loop's chain starts clean; each frame passes it through, a navigation frame's method node chains onto it |
| `Merge Errors`, three inputs | event loop, poll loop and the start-up read |

## The calls that build it

| | |
|---|---|
| `lvai_generate_vi` | `web-browser-read-page-title.xml` → `Read Page Title.vi`, beside the scaffold |
| `lvai_open_file` (project + `viPaths` = that VI) | makes the bare-name `Call` resolvable |
| `lvai_generate_vi_with_events` | `web-browser-title-poll.xml` → the scaffold, both event frames registered |
| `lvai_graft_diagram` `allowNewControls` (+ `replaceDiagram` when the panel holds code) | into the supplied panel with the real Web Browser control |
| `lvai_bind_control_references` `{"WB Ref":"Web Browser Control","URL Ref":"URL String"}` | the bound references AIXML cannot write, both in one call |

The labels are the supplied panel's (`URL String`, `Stop`, `Web Browser Control`); adapt them to
yours. Running it opens its panel, and `lvai_run_vi_and_read_values` closes that panel again
afterwards (`panelClosedAfterRun`), so the next regeneration onto the same path is not `Error 1357`.

## Navigation and buttons

The event loop carries `WB Ref` into the Event Structure as tunnel `In2`, unused by the two frames
here, because the navigation methods ride the SAME stand-in - `NavigateBackward`, `NavigateForward`,
`ReloadPage`, `StopLoadingPage` (names from NI's `Navigation History Methods.vi`) - and one
`lvai_bind_control_references` call reconnects every node on it (`sinksReconnected` 2 for two
loops). Each button's terminal sits inside its own Value Change frame, wired to a Case selector
(`True`/`False`) whose TRUE case calls the method on `In2` with the frame's error from `In3`; keep
the delivered buttons LATCHED and drive a separate test-only graft with `switchActionControls`. With
six frames the round-5 build rendered 1619 px wide.

## Reading an empty `Page Title` in a test

The title starts EMPTY and keeps its last good value, so an empty one in a snapshot means no read has
succeeded YET - not that the page has no title. Every `lvai_run_vi_and_read_values` run starts the
browser COLD, because the tool closes the panel the run opened (`panelClosedAfterRun`), and how long
that takes VARIES. Measured 2026-10-02 on this skeleton, start-up load only: after 6 s the title was
there in 3 of 5 runs and empty in 2 (the URL was always in the control), after 12 s in 1 of 1. So
**give a start-up check `runForMs` of at least 12000**, and a navigation 3 s or more (`signalGapMs`)
before reading its title.

**Two transients are possible and unmeasured**: right after a navigation the OLD document may still
answer `complete` for one poll, so the old title shows for 250 ms; and a page whose title is set by
script after load reads `(no title)` until then.

## Testing it

Run the SCAFFOLD only for `execState` and a short start-up run: its stand-ins are unbound, so its
references are null and nothing loads - the values say nothing. The behaviour is tested on the
grafted, bound VI, or on its test-only copy.

## Stop is DELAYED after a history navigation - reproduced, not explained

Round 3 saw one run in eight not end on a signalled `Stop`. **Round 5 reproduced it as a DELAY, not a
hang**: after `Back` or `Forward`, a `Stop` signalled 3-4 s later was not handled and `Page Title`
still showed the page before - while the same sequence with 6 s (agent) or 10 s (re-check) between
the signals updated the title and ended cleanly (`targetEndedBeforeAbort: true`, `error out` 0).
After a plain URL change, 3 s was always enough. Both loops are late together, which fits
`Execute JavaScript` (`Wait for Return Value?` TRUE) blocking during a history navigation - a
hypothesis, not a measurement. For a person at the panel it means `Stop` can take a few seconds after
Back or Forward; for a test, leave 6 s or more after one before signalling the next.
