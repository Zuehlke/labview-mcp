"""Phase 3 repairs: export the NI-built helper VIs with NI's own exporter, as LEARNING PAIRS.

helper_coverage.py leaves "P3 ni <helper>.vi" in %TEMP%\\LabVIEWMCP\\phase3 - our AIXML built by
NI's converter. Their NI exports beside their heaps are what the learners need: learn_names_full.py
reads terminal names off them (`anything` on Flatten To XML, ...), alias_props.py property and
method aliases (`Save VI Icon to File` for the heap's `VI Icon.Save to File`). The pairs go in the
export cache's own layout - <x>.xml beside <x>.json naming ViPath - so both learners take them with
--cache. Needs LabVIEW with its AI service up; this is the one place NI's service is used, once
per LabVIEW version.

    python helper_pairs.py                       # -> %TEMP%\\LabVIEWMCP\\phase3-pairs
    python learn_names_full.py --cache <pairs> --write
    python alias_props.py --cache <pairs> --parts <parts copy> --write
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import helper_parity as hp  # noqa: E402

PAIRS = os.path.join(os.environ["TEMP"], "LabVIEWMCP", "phase3-pairs")


def main():
    os.makedirs(PAIRS, exist_ok=True)
    server = hp.Server()
    done = failed = 0
    for f in sorted(os.listdir(hp.BUILT)):
        if not (f.startswith("P3 ni ") and f.endswith(".vi")):
            continue
        vi = os.path.join(hp.BUILT, f)
        base = os.path.join(PAIRS, f[len("P3 ni "):-3])
        body = server.tool("lvai_convert_vi_to_aixml", {"viPath": vi, "aiXmlFilePath": base + ".xml",
                                                        "returnContent": False, "backend": "grpc"})
        if body.get("errorCode") == 0 and os.path.exists(base + ".xml"):
            json.dump({"ViPath": vi}, open(base + ".json", "w", encoding="utf-8"))
            done += 1
        else:
            failed += 1
            print("export failed:", f, body.get("errorMessage") or body.get("error"))
    print(f"{done} pairs in {PAIRS}, {failed} exports failed")
    server.p.terminate()


if __name__ == "__main__":
    main()
