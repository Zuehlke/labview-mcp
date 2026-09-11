# Git hooks

Versioned Git hooks for this repo.

## What it does

`pre-push` runs the test suite before every push and **rejects the push unless all tests
pass**. The suite needs no LabVIEW, no hardware and no network, and finishes in about ten
seconds, so the gate is cheap.

Before testing it **stops any running MCP server**. It has to: there is a single configuration
and a single artifact (README section 3), and `dotnet test` rebuilds the main project as a
dependency — into the very file the running server holds open. Without the stop the build fails
with `MSB3027`, but only when the sources actually changed, which makes it an intermittent
mystery rather than an error.

Stopping is safe — no state lives in the process — but the Claude client does **not** restart a
killed MCP server inside a session, so the `lvai_*` tools stay gone until the client is
restarted. Prefer running this script over a bare `dotnet test` so the stop is deterministic.

> Ported from the TestStandMCP hook. That one stops processes because its integration tests
> need exclusive access to the TestStand COM engine; here the reason is the file lock on the
> single build artifact. Same mechanism, different cause.

## Which working tree it tests

**It asks git, not itself.** The gate resolves the tree under test with
`git rev-parse --show-toplevel`, and prints what it resolved:

```
  tree under test : C:\Users\...\labview-mcp\.claude\worktrees\my-branch
  test project    : C:\Users\...\my-branch\tests\LabVIEWMCP.Tests\LabVIEWMCP.Tests.csproj
  resolved by     : git rev-parse --show-toplevel
  worktree        : linked (branch claude/my-branch)
```

Read those lines. They are the whole point of the paragraph below.

### The worktree trap (measured 2026-09-11)

The script used to derive the tree from its **own location**, `Split-Path -Parent $PSScriptRoot`.
That is wrong in a linked worktree, and wrong in the worst possible direction.

`core.hooksPath` is git **config**, and config is shared between a repository and every linked
worktree — so each worktree inherits the main checkout's hooks directory. (In this repository the
stored value is also *absolute*: `git config --show-origin --get core.hooksPath` reports
`C:\...\labview-mcp\.githooks` from `.git/worktrees/<name>/config.worktree`, written by the
worktree tooling. A relative `.githooks` would have resolved per-worktree and hidden the bug
behind luck.) So pushing from `.claude/worktrees/<name>` ran the **main checkout's** script, and
the gate tested whatever was checked out at `main`.

Measured while pushing a branch from a worktree: the hook printed `Failed: 0, Passed: 1625` while
the same script run by hand inside that worktree printed `1667`. The 42 missing tests were exactly
the two files the pushed branch added. **A green gate for an unrelated tree is worse than no
gate** — and the only tell was a test count nobody compares.

What is authoritative instead: git runs a hook with the **pushed worktree** as the process working
directory. Measured with a probe hook in a real linked worktree — cwd,
`git rev-parse --show-toplevel` and `--git-dir` all named the worktree, and `GIT_DIR` was exported
as `<main>/.git/worktrees/<name>`, which does not misdirect `--show-toplevel`.

There is deliberately **no silent fallback** to the script's location while git is answering: a
resolved tree with no test project is an error the gate stops on, because falling back to another
tree is precisely the behaviour this replaced. The script-location route survives only for "git is
not on `PATH` at all", and the output says so when it is used.

Check the aim without running anything:

```powershell
powershell -ExecutionPolicy Bypass -File .githooks/run-tests.ps1 -ResolveOnly
```

`PrePushGateTests` builds a real `git worktree add` fixture with the absolute `core.hooksPath` and
drives the real script through it, because a fixture that merely *looked* like a worktree passed
against the broken script too.

### The same trap, twice more

Two other places asked "which tree am I in?" and got it wrong the same way. Both are fixed, and
both are worth recognising, because the signal is always a **green** result about someone else's
code:

| Where | What it did | Why |
|---|---|---|
| `AixmlCheckTests.RepoRoot` | linted **main's** `scripts\` instead of the branch's | walked for a *directory* called `.git`; a worktree's `.git` is a **file**, so the walk went past it to the main checkout above |
| `Directory.Build.targets` | re-ran its one-time hook setup on **every** worktree build, warning `MSB3371` three times | MSBuild's `Exists()` is true for a file, so `Exists('.git')` fired in a worktree where the marker can never be written |

`tests/LabVIEWMCP.Tests/Support/RepoTree.cs` is now the single resolver for the test side: it
matches on `CLAUDE.md` beside `scripts\` — repository content, which is the same in a worktree, a
clone and a CI checkout — rather than on `.git`, whose *kind* of filesystem object depends on how
the tree was created.

## The .NET SDK preflight

The gate checks for a usable SDK before building, and uses an SDK-bearing `dotnet` even when it is
not the first one on `PATH`.

**Measured 2026-09-11 on this station.** `C:\Program Files\dotnet` holds a runtime only —
`dotnet.exe`, `host`, `shared`, and **no `sdk` directory** — while the SDK (8.0.424) is installed
per-user at `C:\Users\<user>\.dotnet`. Program Files comes **first** on `PATH`, so a bare
`dotnet build` / `dotnet test` dies with:

```
  * You intended to execute a .NET SDK command:
      No .NET SDKs were found.
```

That aborted a push once, with a message about downloading .NET for what is purely **PATH order**.
So the gate looks for a `dotnet` with an `sdk\` directory beside it — checking `DOTNET_ROOT`,
`%USERPROFILE%\.dotnet`, and every `dotnet` on `PATH` — prepends that one for its own process, and
says which it used:

```
  -> dotnet on PATH has no SDK; using C:\Users\<user>\.dotnet\dotnet.exe
     (PATH had C:\Program Files\dotnet\dotnet.exe first - runtime only)
```

If no SDK is discoverable anywhere it prints the diagnosis and the two commands that confirm it
(`Get-Command dotnet -All`, `dotnet --list-sdks`) instead of relaying the raw dump. To fix it for a
whole shell rather than one hook run:

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
```

## Activation (per clone)

This is **automatic**: on the first `dotnet build` / `dotnet test`, `Directory.Build.targets`
in the repo root runs `git config core.hooksPath .githooks` once per clone (tracked by a
marker in `.git/`, which is never committed). Since you always build before pushing, the hook
is active by the time it matters.

That target deliberately does nothing in a **linked worktree**: `core.hooksPath` is shared
config, so the main checkout's build has already set it for every worktree that will ever
exist. A clone that is only ever built inside a worktree therefore needs the manual line
below - though the worktree tooling in this repository sets `core.hooksPath` itself, so that
gap is not reachable in practice.

Set it manually only if you want the hook active *before* the first build, or if `git` was not
on `PATH` during that build:

```powershell
git config core.hooksPath .githooks
```

Verify:

```powershell
git config core.hooksPath   # -> .githooks
```

## Files

| File | Role |
|---|---|
| `pre-push` | POSIX-sh entry point Git invokes; delegates to PowerShell |
| `run-tests.ps1` | Resolves the tree under test **from git**, preflights the .NET SDK, stops a `bin/`-locking server, runs `dotnet test`, returns the result. `-ResolveOnly` prints the aim and exits without building. |

The `pre-push` stub is forced to LF line endings via `.gitattributes`. Without that, `sh.exe`
on Windows aborts with `bad interpreter: /bin/sh^M` and the hook silently never runs.

## Bypassing (emergencies only)

```powershell
git push --no-verify
```
