# `web-browser-title-poll.xml` — a Web Browser program that POLLS instead of calling once

An event loop writes every new `URL String` into the `Web Browser Control` and ends on `Stop`. A
second loop asks the browser for `document.title` every 250 ms through `Execute JavaScript`, on a
`WB Ref` stand-in that `lvai_bind_control_references` turns into a bound reference after the graft.
While the browser is not ready - `Error 53` right after `FP.Open`, or a page still loading - the
previous title is kept, so the next poll retries instead of the program failing. AT START-UP the
program loads the URL already in `URL String`, as NI's `Display a URL.vi` does: a `URL Ref` stand-in
(`ref{LV.String}`) reads the value and writes it back with `Value (Signaling)`, which fires the
`URL String` Value Change frame - so the start-up load and every later change take ONE path.

Built by `labview-vi-generator` on 2026-10-02 as the acceptance test of the Web Browser route
(`docs/keep-supplied-front-panel.md` §6g): grafted into a copy of NI's `Display a URL.vi` with
`replaceDiagram`, bound, `execState 1`, and driven with `signalsJson` it showed `Acceptance Page`,
`Second Page` and the live NI page title. **Replaced by the fourth acceptance build's scaffold the
same day**, which added the start-up load and the untitled-page handling: grafted, bound with
`{"WB Ref":"Web Browser Control","URL Ref":"URL String"}`, and it showed `Page One` at start-up,
`What Is LabVIEW? - NI` for NI's default URL, `Page Two` after a change, `(no title)` for a page
without one, and ended on a signalled `Stop` with a clean `error out` (`targetEndedBeforeAbort`).
This document itself generates at 1813 x 629 px, 9 stages, `execState 1`.

## Why polling, and why this shape

**`Execute JavaScript` cannot be called once.** It needs the control's panel open in the running
instance, and even after `FP.Open` the first call can answer `Error 53` while the browser
initialises (measured twice on cold starts). A load poll is also how a program learns that a
navigation has finished.

**The obvious shape - an Event Structure Timeout frame - is not authorable**: AIXML has no timeout
terminal on an Event Structure (`docs/cold-build-atm-cld.md` §4). So the poll is a SECOND loop paced
by `Wait on Notification` with a 250 ms timeout. The notifier is what stops it: the `Stop` frame
sends a notification, `timed out?` turns FALSE, and the loop ends without a stop flag being polled.

| element | role |
|---|---|
| `Obtain Notifier` (`bool`) | the stop signal shared by both loops; released after both end |
| `FP.Open` on an unwired `{LV.VI}` reference | opens the program's OWN panel - `This VI` |
| `Wait on Notification`, `timeout in ms (-1)` = 250 | paces the poll and ends it |
| script, four lines (`&#10;` in the value, `\3A` for the colon): `if(d.readyState!='complete'\|\|d.URL=='about:blank') return 'LV_PENDING'; return d.title\|\|'(no title)';` | a FUNCTION BODY, so it needs `return`. PENDING is a document still loading or the browser's initial `about:blank` - which is `complete` with an empty title, the likely reading of the third build's empty `Page Title`. A LOADED page without a `<title>` answers `(no title)` instead of keeping the previous title. Line breaks keep the constant narrow: the text sets the diagram's width, and one long line pushed it to 2024 px |
| `URL Ref` -> `{LV.String}` `read+Value` -> `write+Value (Signaling)` | the start-up load, through a bound reference - a Local Variable would stay bound to the duplicate the graft deletes. Its error goes into the 3-input `Merge Errors`, not into the poll loop's shift register: chained there it made the chain 11 stages, over the budget of 10 |
| `Select` on `status OR (result = LV_PENDING)` | keeps the previous title while the browser is not ready |
| `Merge Errors` | one `error out` for both loops; the script's own errors are expected and left out |

## The calls that build it

| | |
|---|---|
| `lvai_generate_vi_with_events` | this document → the scaffold, both event frames registered |
| `lvai_graft_diagram` `allowNewControls` (+ `replaceDiagram` when the panel holds code) | into the supplied panel with the real Web Browser control |
| `lvai_bind_control_references` `{"WB Ref":"Web Browser Control","URL Ref":"URL String"}` | the bound references AIXML cannot write, both in one call |

The labels are the supplied panel's (`URL String`, `Stop`, `Web Browser Control`); adapt them to
yours. Running it opens its panel, and `lvai_run_vi_and_read_values` closes that panel again
afterwards (`panelClosedAfterRun`), so the next regeneration onto the same path is not `Error 1357`.

## Navigation and buttons

The same `WB Ref` stand-in feeds the navigation methods as well - `NavigateBackward`,
`NavigateForward`, `ReloadPage`, `StopLoadingPage` (names from NI's `Navigation History Methods.vi`)
- and one `lvai_bind_control_references` call reconnects every node on it, measured on a browser
build where it fed both loops. Each button's terminal sits inside its own Value Change frame, wired
to a Case selector whose TRUE case acts; keep the delivered buttons LATCHED (one click, one action)
and drive a separate test-only graft with `switchActionControls`.

## Reading an empty `Page Title` in a test

The title starts EMPTY and keeps its last good value, so an empty one in a snapshot means no read has
succeeded YET - not that the page has no title. Every `lvai_run_vi_and_read_values` run starts the
browser COLD, because the tool closes the panel the run opened (`panelClosedAfterRun`); the first
scripts then answer `Error 53` for a moment. Give a navigation a few seconds (`signalGapMs`) before
reading its title. Measured 2026-10-02: one load with a 3 s gap read empty, the same load followed by
a Stop with a 2.5 s gap read the title.

**Two transients are possible and unmeasured**: right after a navigation the OLD document may still
answer `complete` for one poll, so the old title shows for 250 ms; and a page whose title is set by
script after load reads `(no title)` until then.

## Testing it

Run the SCAFFOLD only for `execState` and a short start-up run: its stand-ins are unbound, so its
references are null and nothing loads - the values say nothing. The behaviour is tested on the
grafted, bound VI, or on its test-only copy.

## Open: a poll that may not see Stop

In one of eight runs of the third acceptance build the program did NOT end on a signalled `Stop`,
and an identical later run ended cleanly. The fourth build ended on `Stop` in both runs that
signalled it. Unmeasured hypothesis: `Execute JavaScript` with
`Wait for Return Value?` TRUE blocks for a while during a navigation, and the poll loop only checks
the stop notification between calls. Not reproduced, so nothing here is changed for it - if you
see it, measure first.
