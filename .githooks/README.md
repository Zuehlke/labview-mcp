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

## Activation (per clone)

This is **automatic**: on the first `dotnet build` / `dotnet test`, `Directory.Build.targets`
in the repo root runs `git config core.hooksPath .githooks` once per clone (tracked by a
marker in `.git/`, which is never committed). Since you always build before pushing, the hook
is active by the time it matters.

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
| `run-tests.ps1` | Checks for a `bin/`-locking server, runs `dotnet test`, returns the result |

The `pre-push` stub is forced to LF line endings via `.gitattributes`. Without that, `sh.exe`
on Windows aborts with `bad interpreter: /bin/sh^M` and the hook silently never runs.

## Bypassing (emergencies only)

```powershell
git push --no-verify
```
