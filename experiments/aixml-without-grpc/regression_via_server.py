"""The composer's own test suite (tests/*.xml + tests/expect.json), run through the BUILT server:
compose with lvai_convert_aixml_to_vi (backend pylabview), run every case over ActiveX with
lvai_run_vi_and_read_values, compare each expected output. No LabVIEW restart, unlike selftest.py -
so it does not watch LabVIEW's log; it answers "does every test still compose and compute what it
did", which is what a change to the composer or its tables has to keep true.

Uses the parts the environment names (LABVIEWMCP_AIXML_PARTS), else the committed ones.
Skipped: tests with callees (`callees` in expect.json), which need the callee composed beside them
under a stamped name, and t91, which counts its own VI name - selftest.py does both.

    python regression_via_server.py [substring ...]
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import helper_parity as hp  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.environ["TEMP"], "LabVIEWMCP", "regression")


def as_inputs(text):
    out = {}
    for kv in (text or "").split(";"):
        if "=" in kv:
            k, v = kv.split("=", 1)
            if v.startswith("{") and v.endswith("}"):
                v = "[" + v[1:-1] + "]"          # selftest's cluster spelling -> the AIXML literal
            out[k] = v
    return out


# Reads its own VI NAME and counts it; selftest.py composes under a stamped name, so the expected
# length belongs to that name, not to this runner's file name.
NAME_DEPENDENT = {"t91_property_nodes"}


def same(expected, literal):
    if literal is None:
        return False
    # selftest.py's formatter wrote a 2D array flat and an empty one as '' - the runner writes the
    # real shape, [[1,2],[3,4]] and []. Same values, so compare without the brackets as well.
    flat = lambda x: re.sub(r"[\[\]\s]", "", str(x)).lower()
    if isinstance(expected, str) and flat(expected) == flat(literal):
        return True
    if isinstance(expected, (int, float)) and not isinstance(expected, bool):
        try:
            return abs(float(literal) - float(expected)) <= 1e-9 * max(1.0, abs(float(expected)))
        except ValueError:
            return False
    return str(expected).lower() == str(literal).lower()


def main(filters):
    expect = json.load(open(os.path.join(HERE, "tests", "expect.json"), encoding="utf-8"))
    os.makedirs(OUT, exist_ok=True)
    server = hp.Server()
    passed = failed = refused = skipped = 0
    for test, spec in sorted(expect.items()):
        if filters and not any(f in test for f in filters):
            continue
        if spec.get("callees") or not spec.get("cases") or test in NAME_DEPENDENT:
            skipped += 1
            continue
        vi = os.path.join(OUT, test + ".vi")
        body = server.tool("lvai_convert_aixml_to_vi", {"aiXmlFilePath": os.path.join(HERE, "tests", test + ".xml"),
                                                        "viPath": vi, "backend": "pylabview"})
        if body.get("errorCode") != 0 or body.get("executable") is False:
            refused += 1
            print(f"{test}: NOT BUILT {body.get('errorKind')} executable={body.get('executable')} {body.get('errorMessage')}")
            continue
        for case in spec["cases"]:
            run = server.tool("lvai_run_vi_and_read_values", {"viPath": vi, "inputsJson": json.dumps(as_inputs(case.get("inputs"))),
                                                              "backend": "pylabview", "timeoutSeconds": 30})
            values = run.get("values") or {}
            bad = {k: (v, (values.get(k) or {}).get("literal")) for k, v in case["outputs"].items()
                   if not same(v, (values.get(k) or {}).get("literal"))}
            if run.get("ok") and not bad:
                passed += 1
            else:
                failed += 1
                print(f"{test} {case.get('inputs')!r}: FAIL {run.get('errorKind') or ''} {bad}")
    print(f"{passed} cases passed, {failed} failed, {refused} tests not built, {skipped} skipped (callees or no cases)")
    server.p.terminate()


if __name__ == "__main__":
    main(sys.argv[1:])
