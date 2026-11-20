| # | Gap | Layer | Blocks | In any step | Usage | Elements (first seen) | Composer says |
|---|---|---|---:|---:|---:|---|---|
| 1 | Express VI instance: terminals depend on its configuration | composer | 32 | 33 | 23 | Ex_Inst_Simulate Signal.vi, TimeFrequencyDualWindow Source.vi, Ex_Inst_Filter.vi | `Call 'Ex_Inst_Simulate Signal.vi' has no input 'Offset' on its pane (has: Signal, error in (no error), error o` |
| 2 | Property Node: other property or class not harvested | composer | 19 | 25 | 352 | Property Node, Invoke Node | `Invoke Node 1888: no invoke node GenClass.XML_Document.DOMUserDefRef.DUXCreateElement harvested yet` |
| 3 | Graph style or graph cursor property | composer | 18 | 20 | 559 | graph, VI Server Reference, int32 | `a VI Server node is bound to 'Movement Data', whose style 'graph21717' has no part here - it would be composed` |
| 4 | Refnum wires never composed yet (UsrDefined, AutoRef, Occurrence, TCP, DataSocket, Python session) | composer | 16 | 21 | 0 | Empty Collection?, Read Map Max & Min Keys | `net '1310.stream session' carries a Refnum.UsrDefined, which no composed wire has carried yet` |
| 5 | VI Server Reference to a SubPanel / Web Browser control | composer | 12 | 15 | 424 | VI Server Reference, CaseFrame | `VI Server Reference 197: '{LV.SubPanel}' is not composable yet` |
| 6 | Refnum types without a part (DataLog, DataFinder, TDMS, EventReg) | composer | 12 | 12 | 0 | cluster{array{path.appended path}.Image File Paths,ref{GenClass.IMAQAVI.IMAQAVI}.AVI Ref,r | `type 'ref{TDMSFile}' is not composable yet` |
| 7 | Callee unreadable (pylabview timeout) | composer | 11 | 11 | 0 | Caraya.lvlib:Assert.lvclass:Assert Almost Equal_Float.vi | `callee Helper SubVI - TimeCepstrum Data.vi cannot be read: pylabview timed out after 30 s: -x -i C:\Program Fi` |
| 8 | Feedback Node inside a case/sequence (not directly in a loop) | composer | 10 | 12 | 44 | Feedback Node | `Feedback Node 23: only at the top level or directly in a loop` |
| 9 | Digital waveform / digital table type | composer | 10 | 10 | 559 | graph, digitaltable, digitalwaveform | `type 'digitaltable' is not composable yet` |
| 10 | Expression Node | composer | 7 | 7 | 24 | Expression Node | `Expression Node 5059: needs a numeric wire into input` |
| 11 | Python Node | composer | 6 | 6 | 8 | Python Node | `Node 'Python Node' is not a primitive this prototype knows` |
| 12 | Timed Loop | composer | 6 | 6 | 7 | Timed Loop | `structure 'Timed Loop' is not composable yet` |
| 13 | Picture type (control or constant) | composer | 6 | 6 | 0 |  | `no constant encoding for Picture` |
| 14 | Asynchronous call (Start/Wait On Asynchronous Call) | composer | 5 | 8 | 8 | Start Asynchronous Call, Wait On Asynchronous Call | `Node 'Start Asynchronous Call' is not a primitive this prototype knows` |
| 15 | Primitive unknown by name (unresolved ?primResID) | reader | 5 | 8 | 4 | ?primResID 3916, ?primResID 3917, ?primResID 3918 | `Node '?primResID 3916' is not a primitive this prototype knows` |
| 16 | Constructor Node (.NET) | composer | 5 | 5 | 5 | Constructor Node | `Node 'Constructor Node' is not a primitive this prototype knows` |
| 17 | Property/Invoke Node on an ActiveX/.NET class or ActiveX refnum | composer | 4 | 5 | 352 | Property Node, Invoke Node, ref{ActiveX} | `Invoke Node 581: class '{DotNet}' is not composable yet` |
| 18 | In Place Element: Array Split/Replace Subarrays, Array Index/Replace | composer | 4 | 5 | 7 | Array Split / Replace Subarrays, Array Index / Replace Elements | `net '3716.value' ends at index#2 3269 of Array Index / Replace Elements, which is not composable yet` |
| 19 | Fixed-point type | composer | 4 | 4 | 3 | fixed, fixed | `type 'fixed' is not composable yet` |
| 20 | Waveform Unbundle/Bundle Elements, Build Cluster Array | composer | 4 | 4 | 2 | Waveform Unbundle / Bundle Elements, Build Cluster Array | `Node 'Waveform Unbundle / Bundle Elements' is not a primitive this prototype knows` |
| 21 | Register For Events on a non-user-event source | composer | 3 | 7 | 13 | Register For Events | `Register For Events 745: event source 'event source' is neither a user event nor a VI Server reference` |
| 22 | MATLAB script / Call MATLAB Function | composer | 3 | 4 | 2 | Call MATLAB Function, MATLAB script | `Node 'Call MATLAB Function' is not a primitive this prototype knows` |
| 23 | Numeric with physical unit (double{m}) | composer | 3 | 3 | 559 | double{m}, double{m^-3 kg}, map{string.name,double{m^-3 kg}.amount} | `type 'double{m^-3 kg}' is not composable yet` |
| 24 | Waveform/MeasureData constant | composer | 3 | 3 | 0 |  | `no constant encoding for MeasureData` |
| 25 | Property Node bound to a control: the reader writes class {?} | reader | 2 | 3 | 352 | Property Node, Invoke Node | `Property Node 868: class '{?}' is not composable yet` |
| 26 | Control style lost in the export (Tree, Listbox, Tab, Slide, ...): node bound to a plain control | reader | 1 | 2 | 352 | Property Node | `Property Node 1362: bound to 'Tab Control', which is composed as a plain LV.Enum (the export names no style or` |
| 27 | Primitive whose terminal names the table lacks | composer | 1 | 2 | 4 | Insert Into Array | `Node 'Insert Into Array': no template with terminals array, index (col), new element/subarray, output array, d` |
| 28 | Map and Set primitives | composer | 1 | 2 | 1 | Map Get / Replace Value, Build Map, Remove From Set | `Node 'Build Map' is not a primitive this prototype knows` |
| 29 | Library member the .lvlib does not list (licensed toolkit) | composer | 1 | 1 | 1 | Example Actor.lvlib:Example Actor.lvclass:Write Actor Label.vi, NI_DigFilterLicensed.lvlib | `Call 724: Example Actor.lvclass lists no member 'Write Actor Label.vi'` |
| 30 | File I/O: Format Into File / Scan From File | composer | 1 | 1 | 1 | Format Into File, Scan From File | `Node 'Format Into File' is not a primitive this prototype knows` |
| 31 | Property Node linked to a control the export does not carry | reader | 0 | 18 | 352 | Property Node | `Property Node 231: no control 'Movement Data' to link to` |
| 32 | Merge Signals | composer | 0 | 6 | 9 | Merge Signals | `Node 'Merge Signals' is not a primitive this prototype knows` |
| 33 | Composer crash | composer | 0 | 2 | 352 | Property Node, PORT IO Uninstall Driver__ogtk.vi, PORT IO Disable Range__ogtk.vi | `CRASH AttributeError: 'NoneType' object has no attribute 'attrib' @ lvheap.py:481` |
