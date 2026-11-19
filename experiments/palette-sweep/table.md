| # | Gap | Layer | Blocks | In any step | Usage | Elements (first seen) | Composer says |
|---|---|---|---:|---:|---:|---|---|
| 1 | Class control/indicator on the panel (ref{UDClassInst}) | composer | 77 | 78 | 52 | ref{UDClassInst}, array{ref{UDClassInst}.3D Plot Datatype.lvclass}, array{ref{UDClassInst} | `type 'ref{UDClassInst}' is not composable yet` |
| 2 | Express VI instance: terminals depend on its configuration | composer | 31 | 33 | 23 | Ex_Inst_Simulate Signal.vi, TimeFrequencyDualWindow Source.vi, Ex_Inst_Write LabVIEW Measu | `Call 'DFD Filter Analysis Source.vi' has no output 'magnitude' on its pane (has: error in (no error), error ou` |
| 3 | Control style lost in the export (Tree, Listbox, Tab, Slide, ...): node bound to a plain control | reader | 24 | 36 | 352 | Property Node, Invoke Node | `Property Node 224: bound to 'Event Data', which is composed as a plain LV.Digital (the export names no style o` |
| 4 | Refnum wires never composed yet (UsrDefined, AutoRef, Occurrence, TCP, DataSocket, Python session) | composer | 23 | 32 | 0 | Empty Collection?, Read Map Max & Min Keys | `net '196.adaptive filter out' carries a Refnum.UsrDefined, which no composed wire has carried yet` |
| 5 | XNode call | composer | 22 | 26 | 10 | Database Variant To Data.xnode, DFD FXP Simulation.xnode, AFT Simulate Fixed-Point FIR LMS | `Call 391: AFT Simulate Fixed-Point FIR LMS.xnode is an XNode - not composable yet` |
| 6 | Property Node: DAQmx property not in the harvested table | composer | 22 | 25 | 352 | Property Node | `Property Node 4238: no property node on GenClassTag.DAQmx Real-Time.NIDAQ harvested yet` |
| 7 | Property Node: other property or class not harvested | composer | 18 | 34 | 352 | Property Node, Invoke Node | `Property Node 5507: no property node on LV.DigitalGraph harvested yet` |
| 8 | Property Node bound to a control: the reader writes class {?} | reader | 17 | 24 | 352 | Property Node, Invoke Node | `Property Node 93: class '{?}' is not composable yet` |
| 9 | Graph style or graph cursor property | composer | 15 | 17 | 559 | VI Server Reference, graph | `a VI Server node is bound to 'Signal', whose style 'graph21717' has no part here - it would be composed as a p` |
| 10 | Refnum types without a part (DataLog, DataFinder, TDMS, EventReg) | composer | 12 | 12 | 0 | cluster{array{path.appended path}.Image File Paths,ref{GenClass.IMAQAVI.IMAQAVI}.AVI Ref,r | `type 'ref{TDMSFile}' is not composable yet` |
| 11 | Property/Invoke Node on a VISA class | composer | 11 | 11 | 352 | Property Node | `Property Node 1345: class '{Visa.USB Raw}' is not composable yet` |
| 12 | Digital waveform / digital table type | composer | 10 | 10 | 559 | graph, digitaltable, digitalwaveform | `type 'digitalwaveform' is not composable yet` |
| 13 | Property Node on NI System Configuration (nisysapi) | composer | 9 | 10 | 352 | Property Node | `Property Node 499: class '{GenClass.nisysapiFilter.nisysapi}' is not composable yet` |
| 14 | Property Node linked to a control the export does not carry | reader | 7 | 28 | 352 | Property Node, Invoke Node | `Property Node 1091: no control 'Signal' to link to` |
| 15 | Stacked Sequence in NI's export form (one frame) | composer | 7 | 7 | 17 | Stacked Sequence Structure | `Stacked Sequence 89: no frames - NI's export writes them as one; only vi2aixml's frame-by-frame form is compos` |
| 16 | Timed Loop | composer | 7 | 7 | 7 | Timed Loop | `structure 'Timed Loop' is not composable yet` |
| 17 | Picture type (control or constant) | composer | 7 | 7 | 2 | Empty Picture | `no constant encoding for Picture` |
| 18 | Feedback Node inside a case/sequence (not directly in a loop) | composer | 6 | 12 | 44 | Feedback Node | `Feedback Node 2851: only at the top level or directly in a loop` |
| 19 | Expression Node | composer | 6 | 7 | 24 | Expression Node | `Expression Node 5059: needs a numeric wire into input` |
| 20 | Python Node | composer | 6 | 6 | 8 | Python Node | `Node 'Python Node' is not a primitive this prototype knows` |
| 21 | Asynchronous call (Start/Wait On Asynchronous Call) | composer | 5 | 8 | 8 | Start Asynchronous Call, Wait On Asynchronous Call | `Node 'Start Asynchronous Call' is not a primitive this prototype knows` |
| 22 | Constructor Node (.NET) | composer | 5 | 5 | 5 | Constructor Node | `Node 'Constructor Node' is not a primitive this prototype knows` |
| 23 | VI Server Reference to a SubPanel / Web Browser control | composer | 4 | 15 | 424 | VI Server Reference, CaseFrame | `VI Server Reference 714: '{LV.SubPanel}' is not composable yet` |
| 24 | Property/Invoke Node on an ActiveX/.NET class or ActiveX refnum | composer | 4 | 5 | 352 | Property Node, Invoke Node, ref{ActiveX} | `Invoke Node 581: class '{DotNet}' is not composable yet` |
| 25 | In Place Element: Array Split/Replace Subarrays, Array Index/Replace | composer | 4 | 5 | 7 | Array Split / Replace Subarrays, Array Index / Replace Elements | `Node 'Array Split / Replace Subarrays' is not a primitive this prototype knows` |
| 26 | Waveform Unbundle/Bundle Elements, Build Cluster Array | composer | 4 | 4 | 2 | Waveform Unbundle / Bundle Elements, Build Cluster Array | `Node 'Build Cluster Array' is not a primitive this prototype knows` |
| 27 | Register For Events on a non-user-event source | composer | 3 | 7 | 13 | Register For Events | `Register For Events 714: event source 'event source' is neither a user event nor a VI Server reference` |
| 28 | Primitive unknown by name (unresolved ?primResID) | reader | 3 | 6 | 4 | ?primResID 3916, ?primResID 3917, ?primResID 3918 | `Node '?primResID 3916' is not a primitive this prototype knows` |
| 29 | Numeric with physical unit (double{m}) | composer | 3 | 3 | 559 | double{m}, double{m^-3 kg}, map{string.name,double{m^-3 kg}.amount} | `type 'double{s}' is not composable yet` |
| 30 | Waveform/MeasureData constant | composer | 3 | 3 | 0 |  | `no constant encoding for MeasureData` |
| 31 | Fixed-point type | composer | 2 | 3 | 3 | fixed, fixed | `type 'fixed' is not composable yet` |
| 32 | Other primitives without a part (Sinc, Inverse Tangent (2 Input)) | composer | 2 | 2 | 2 | Sinc, Inverse Tangent (2 Input) | `Node 'Sinc' (primResID 1227) has no harvested part` |
| 33 | Class constant inside an array or cluster | composer | 1 | 6 | 0 | ref{UDClassInst}, array{ref{UDClassInst}.Nested Actor's Enqueuer}, cluster{ref{UDClassInst | `type 'ref{UDClassInst}' is not composable yet` |
| 34 | Event structure: an event the control type does not have | composer | 1 | 4 | 424 | CaseFrame | `Event Structure 1184: 'Double Click' is an event of a listbox only - 'Example List' is a stdNum, and LabVIEW b` |
| 35 | MATLAB script / Call MATLAB Function | composer | 1 | 4 | 2 | Call MATLAB Function, MATLAB script | `Node 'Call MATLAB Function' is not a primitive this prototype knows` |
| 36 | Map and Set primitives | composer | 1 | 2 | 1 | Map Get / Replace Value, Build Map, Remove From Set | `Node 'Build Map' is not a primitive this prototype knows` |
| 37 | Conditional Disable Structure with a target condition | composer | 1 | 1 | 24 | Conditional Disable Structure | `Conditional Disable 805: condition 'VISION_TARGET_TYPE==ISC, Default' is not composable yet` |
| 38 | File I/O: Format Into File / Scan From File | composer | 1 | 1 | 1 | Format Into File, Scan From File | `Node 'Format Into File' is not a primitive this prototype knows` |
| 39 | Call Library Function Node from NI's export | composer | 1 | 1 | 1 | Call Library Function Node | `Call Library Function Node 1587: its library, function and parameters are not in this document - NI's AIXML ha` |
| 40 | Channel wire (Tag, Stream, ...) | composer | 1 | 1 | 1 | High Speed Stream.lvlib:Write.vi, High Speed Stream.lvlib:Read.vi, Tag.lvlib:Write.vi | `Call 730: High Speed Stream.lvlib is a CHANNEL template (C:\Program Files (x86)\National Instruments\LabVIEW 2` |
| 41 | Merge Signals | composer | 0 | 6 | 9 | Merge Signals | `Node 'Merge Signals' is not a primitive this prototype knows` |
| 42 | Composer crash | composer | 0 | 2 | 352 | Property Node, PORT IO Uninstall Driver__ogtk.vi, PORT IO Disable Range__ogtk.vi | `CRASH AttributeError: 'NoneType' object has no attribute 'attrib' @ lvheap.py:481` |
| 43 | Primitive whose terminal names the table lacks | composer | 0 | 2 | 4 | Generate User-Defined Trace Event, Get Notifier Status, Insert Into Array | `Node 'Generate User-Defined Trace Event' has no terminal 'trace string' (known: ?parm1, ?parm2)` |
| 44 | Call By Reference without a typed refnum | composer | 0 | 0 | 6 | Call By Reference | `Call By Reference 4200: no connector pane (type= or a strictly typed VI refnum into reference)` |
| 45 | Library member the .lvlib does not list (licensed toolkit) | composer | 0 | 0 | 1 | NI_DigFilterLicensed.lvlib:DFD_Disable First Time Running Warning.vi, NI_AdvSigProcTSA.lvl | `Call 4200: NI_DigFilterLicensed.lvlib lists no member 'DFD_Disable First Time Running Warning.vi'` |
| 46 | Network primitives without a part (UDP, Bluetooth, IrDA, DataSocket, Shared Variable) | composer | 0 | 0 | 0 | Bluetooth Create Listener, Bluetooth Discover, Bluetooth Open Connection | `Node 'Bluetooth Create Listener' (primResID 9151) has no harvested part` |
| 47 | Dynamic-dispatch method called by bare name (VI Tester, ...) | composer | 0 | 0 | 0 | failIf.vi, SetTests.vi, addTest.vi | `Call 4200: 'failIf.vi' is a dynamic-dispatch method this composer cannot link (not found as a member of a .lvc` |
| 48 | Callee unreadable (pylabview timeout) | composer | 0 | 0 | 0 | Caraya.lvlib:Assert.lvclass:Assert Almost Equal_Float.vi | `callee Assert Almost Equal_Float.vi cannot be read: pylabview timed out after 30 s: -x -i C:\Program Files (x8` |
