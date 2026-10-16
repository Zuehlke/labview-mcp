"""One Python process that runs the server's pylabview scripts one request after another.

Every compose used to be a fresh `python.exe`: interpreter start and imports, then a SECOND
python for pylabview's rebuild, a THIRD per callee whose pane the composer reads, and two more
for the connector-pane check after it - about 2 s of a pylabview generation, measured on the
LayoutCheck build (2026-10-16), against 0.16 s of composing. Here the interpreter, pylabview and
the composer are imported once, pylabview runs in-process, and a callee read once is kept until
its file changes.

Protocol, one JSON object per line on stdin and stdout:
  request  {"id": n, "script": <path>, "argv": [...], "cwd": <dir or null>}
  answer   {"id": n, "exit": <int>, "stdout": <text>, "stderr": <text>, "ms": <int>,
            "recycle": <bool>}
A script runs as `__main__` with its own fresh globals, exactly as `python <script> <argv>` would,
except `aixml2vi.py`, which is imported once and called through `main(argv)` so its callee cache
lives across requests. `recycle` true means this process must not be given another request: an
in-process pylabview call outran its deadline and its thread cannot be stopped, so the caller
ends the process and starts a fresh one. `stale` true means the request was NOT run: a Python
file of the composer changed since this process imported it (a checkout's live copy is read, so
an edit takes effect without a rebuild), and the caller sends it to a fresh process.
"""
import contextlib
import io
import json
import os
import sys
import threading
import time
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
_CODE = {}          # script path -> (mtime, code object)
_poisoned = False   # a hung in-process pylabview thread is still running


def _signature():
    """The composer's own Python files and their times - what this process imported."""
    sig = []
    for f in sorted(os.listdir(HERE)):
        if f.endswith(".py"):
            try:
                sig.append((f, os.stat(os.path.join(HERE, f)).st_mtime_ns))
            except OSError:
                pass
    return sig


def _code(script):
    st = os.stat(script).st_mtime_ns
    got = _CODE.get(script)
    if got is None or got[0] != st:
        with open(script, "rb") as f:
            got = (st, compile(f.read(), script, "exec"))
        _CODE[script] = got
    return got[1]


def _exit_code(ex):
    """What `python` turns a SystemExit into: an int is the code, None is 0, anything else -
    `sys.exit("message")`, which the scripts use for a refusal - is PRINTED to stderr and 1."""
    if ex.code is None:
        return 0
    if isinstance(ex.code, int):
        return ex.code
    print(ex.code, file=sys.stderr)
    return 1


def _exec_main(script, argv):
    """`python <script> <argv>`, in this process. Returns the exit code."""
    old = sys.argv
    sys.argv = [script] + list(argv)
    try:
        exec(_code(script), {"__name__": "__main__", "__file__": script, "__builtins__": __builtins__})
        return 0
    except SystemExit as ex:
        return _exit_code(ex)
    finally:
        sys.argv = old


def _inproc_pylabview(args, timeout=120):
    """lvheap.run_pylabview without a process: readRSRC.py run as __main__ on a thread, its
    output captured. A thread that outlives `timeout` cannot be stopped - the process is then
    poisoned and the caller replaces it after this request."""
    global _poisoned
    import lvheap
    script = lvheap._pylabview_argv()[-1]
    box = {}
    out, err = io.StringIO(), io.StringIO()

    def body():
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                box["code"] = _exec_main(script, args)
        except BaseException:   # noqa: BLE001 - reported like a crashed subprocess
            err.write(traceback.format_exc())
            box["code"] = 1

    t = threading.Thread(target=body, daemon=True)
    t.start()
    t.join(timeout)
    if t.is_alive():
        _poisoned = True
        raise RuntimeError("pylabview timed out after %d s: %s" % (timeout, " ".join(args)))
    return box["code"], out.getvalue() + err.getvalue()


def _run(script, argv):
    name = os.path.basename(script).lower()
    if name == "aixml2vi.py" and os.path.dirname(script).lower() == HERE.lower():
        import aixml2vi
        return aixml2vi.main(list(argv))
    return _exec_main(script, argv)


def serve():
    global _poisoned
    # the answers go to a PRIVATE copy of stdout; descriptor 1 itself is pointed at stderr, so
    # nothing a script or a C extension writes past sys.stdout can land in the protocol
    proto = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n")
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    requests = sys.stdin
    sys.path.insert(0, HERE)
    import lvheap
    lvheap.run_pylabview = _inproc_pylabview
    started = _signature()
    for line in requests:
        line = line.strip()
        if not line:
            continue
        req = json.loads(line)
        if req.get("warm"):
            # import what a compose and a pylabview read need, before anyone waits on it
            start = time.perf_counter()
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                import aixml2vi  # noqa: F401
                _code(lvheap._pylabview_argv()[-1])
                sys.path.insert(0, os.path.dirname(os.path.dirname(lvheap._pylabview_argv()[-1])))
                import pylabview.LVrsrcontainer  # noqa: F401
            proto.write(json.dumps({"id": req.get("id"), "exit": 0, "stdout": "", "stderr": "",
                                    "ms": int((time.perf_counter() - start) * 1000),
                                    "recycle": False}) + "\n")
            proto.flush()
            continue
        if _signature() != started:
            proto.write(json.dumps({"id": req.get("id"), "stale": True}) + "\n")
            proto.flush()
            return
        script = os.path.abspath(req["script"])
        out, err = io.StringIO(), io.StringIO()
        here = os.getcwd()
        start = time.perf_counter()
        sys.path.insert(0, os.path.dirname(script))
        try:
            if req.get("cwd"):
                os.chdir(req["cwd"])
            # a script reading stdin sees it closed, as the subprocess route closes it; the real
            # one carries the requests
            sys.stdin = io.StringIO("")
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                try:
                    code = _run(script, req.get("argv") or [])
                except SystemExit as ex:
                    code = _exit_code(ex)
                except BaseException:   # noqa: BLE001 - a script's crash is its answer
                    traceback.print_exc()
                    code = 1
        finally:
            sys.stdin = requests
            os.chdir(here)
            sys.path.remove(os.path.dirname(script))
        answer = {"id": req.get("id"), "exit": int(code or 0), "stdout": out.getvalue(),
                  "stderr": err.getvalue(), "ms": int((time.perf_counter() - start) * 1000),
                  "recycle": _poisoned}
        proto.write(json.dumps(answer) + "\n")
        proto.flush()
        if _poisoned:
            return


if __name__ == "__main__":
    serve()
