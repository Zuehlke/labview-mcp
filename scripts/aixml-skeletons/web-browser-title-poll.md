# `web-browser-title-poll.xml` — a Web Browser program that POLLS instead of calling once

An event loop writes every new `URL String` into the `Web Browser Control` and ends on `Stop`. A
second loop asks the browser for `document.title` every 250 ms through `Execute JavaScript`, on a
`WB Ref` stand-in that `lvai_bind_control_references` turns into a bound reference after the graft.
While the browser is not ready - `Error 53` right after `FP.Open`, or a page still loading - the
previous title is kept, so the next poll retries instead of the program failing.

Built by `labview-vi-generator` on 2026-10-02 as the acceptance test of the Web Browser route
(`docs/keep-supplied-front-panel.md` §6g): grafted into a copy of NI's `Display a URL.vi` with
`replaceDiagram`, bound, `execState 1`, and driven with `signalsJson` it showed `Acceptance Page`,
`Second Page` and the live NI page title.

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
| script `return document.readyState === 'complete' ? document.title : 'LV_PENDING';` | a FUNCTION BODY, so it needs `return` |
| `Select` on `status OR (result = LV_PENDING)` | keeps the previous title while the browser is not ready |
| `Merge Errors` | one `error out` for both loops; the script's own errors are expected and left out |

## The calls that build it

| | |
|---|---|
| `lvai_generate_vi_with_events` | this document → the scaffold, both event frames registered |
| `lvai_graft_diagram` `allowNewControls` (+ `replaceDiagram` when the panel holds code) | into the supplied panel with the real Web Browser control |
| `lvai_bind_control_references` `{"WB Ref":"Web Browser Control"}` | the bound reference AIXML cannot write |

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
