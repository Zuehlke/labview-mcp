# Graph property sweep - 2026-11-20

Which nested Property Node fields of the graph classes compose, load and work. Started from a
user report: a small local model could not rename plots with `Active Plot` and `Plot Name`.

| script | what it does |
|---|---|
| `sweep.py OUTDIR` | one VI per graph class and sub-object (`Plot`, `Cursor`, `X/Y/Z Scale`, `Label`, `Caption`), ONE Property Node reading every field of it, composed with the pylabview composer |
| `runtime.py OUTDIR` | one VI per graph class that WRITES a set of nested fields and reads them back |
| `add_inherited_graph_rows.py` | one-off: the XY, digital and mixed-signal graphs' catalogue rows, copied from their parent class |

Load and run with `../aixml-without-grpc/lv-com.ps1` (LabVIEW running); export with
`lvai_convert_vis_to_aixml backend=grpc` and compare the `fields` attribute.

## Results

- 23 VIs, 952 fields (waveform graph, chart, XY graph, intensity graph): all composed, all loaded
  executable, and NI's exporter read every field back unchanged.
- At run time, written and read back on all four: `Plot.Plot Name`, `Plot.Line Width`,
  `Plot.Plot Color`, `Plot.Visible`, `Plot.Point Style`, `X/Y/Z Scale.Range:*`,
  `X/Y/Z Scale.Name Label.Text`.
- LabVIEW's own run-time refusals: `Active Plot` past the last plot 1077; `Label.Text` 1073
  (edit mode only); `Caption.Text` 1320 on an object without a caption.
- Before the fix the XY graph composed only `Plot` and `Cursor` fields: its catalogue rows were
  missing and `Label` could not be borrowed. Both fixed (inherited rows; ancestor lookup in
  `Composer._property_row`).

The rules that came out of it are in `docs/aixml-reference.md` §8 ("Sub-object properties") and
`docs/vi-server-reference.md`; the skeleton is `scripts/aixml-skeletons/graph-plot-names.xml`.
