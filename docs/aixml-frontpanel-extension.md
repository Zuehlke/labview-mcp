# Extending AIXML with front-panel data - what NI's gRPC route tolerates

Measured 2026-10-14 on LabVIEW 2026 Q3 (32-bit, `26.3.1f1`), against NI's gRPC service and
against our own pylabview composer, to answer one question before any design work: **can an
AIXML document carry a section NI's format does not know - front-panel positions, sizes,
colours - without breaking the gRPC route?**

Short answer: **not inside `<VI>`, in any spelling. After `</VI>`, yes.** NI's validator is a
real XSD validator with a closed content model, and the converter enforces the same schema.

## 1. The probe

One small VI (two `double` controls into `Add`, `error in` passed to `error out`) and ten
variants that differ from it only in where the unknown data sits. Each was validated and
converted over gRPC; every converted VI was exported back and compared with the baseline's
export, because a `errorCode 0` alone has been measured hiding an empty VI before.
Each variant got its own `_name` (`FpProbe_<variant>.vi`), so no refusal could burn another
variant's name.

## 2. Results over gRPC

| variant | `ValidateAIXML` | `ConvertAIXMLToVI` | export vs. baseline |
|---|---|---|---|
| A baseline | 0 | 0, 5 853 bytes, `execState 1` | - |
| B `<FrontPanel>` as last child of `<VI>` | **-2628**: `no declaration found for element 'FrontPanel'`, `element 'FrontPanel' is not allowed for content model '(Call\|Constant\|Control\|FixedConst\|FreeLabel\|Indicator\|Node\|Structure)'` | **-2628**, nothing written | - |
| C `<fp:FrontPanel>` in a foreign namespace (`xmlns:fp="urn:labviewmcp:frontpanel"` on `<VI>`) | **-2628**, same two messages naming `fp:FrontPanel` | not run (same refusal on pylabview, below) | - |
| D `fp:left` / `fp:top` attributes on a `<Control>` | **-2628**: `attribute '{urn:labviewmcp:frontpanel}left' is not declared for element 'Control'` | - | - |
| D2 the namespace DECLARATION alone, nothing using it | 0 | 0, 5 854 bytes, `execState 1` | identical |
| H plain unknown attribute `fpLayout` on `<VI>` | **-2628**: `attribute 'fpLayout' is not declared for element 'VI'` | - | - |
| E processing instruction `<?lvmcp-fp …?>` as last child of `<VI>` | **Error 42, Generic error** - no line, no column | **Error 42**, nothing written | - |
| F processing instruction AFTER `</VI>` | 0 | 0, 5 853 bytes, `execState 1` | **identical** |
| F2 two PIs after `</VI>`, one multi-line carrying XML-shaped text | 0 | 0, 5 854 bytes, `execState 1` | identical |
| G processing instruction BEFORE `<VI>` | 0 | **0, 3 165 bytes, `execState 1`, `ok: true`** | **`<VI _name="FpProbe_G.vi" description=""/>` - EMPTY** |
| G2 XML comment AFTER `</VI>` | 0 | 0, 5 854 bytes, `execState 1` | identical |

What this settles:

- **The schema is closed, and a foreign namespace does not open it.** The messages are
  Xerces-C's; the `<VI>` content model has no `xs:any` and no `xs:anyAttribute`, so an
  extension namespace is refused exactly like an undeclared plain name. No placement inside
  `<VI>` survives - not an element, not an attribute, not a namespaced attribute.
- **A refusal costs the whole document**, as every other `-2628` in this repository does.
  So an extension that slipped through to NI by accident would not degrade gracefully - the
  VI would not be generated at all. That is the useful half: the failure is LOUD.
- **A processing instruction INSIDE `<VI>` is `Error 42` with no location** - the same
  signature a comment between `<VI>`'s children gives (`aixml-reference.md` section 11).
- **Anything after `</VI>` is ignored**: one or several PIs, multi-line, or a comment. The
  VI is byte-for-byte the same size and its export identical to the baseline's.
- **A processing instruction BEFORE `<VI>` empties the VI SILENTLY** - validate `0`, convert
  `0`, `executable: true`, a 3 165-byte VI whose export carries no element and not even the
  description. The same signature as a leading XML comment (measured 2026-09-11), so the
  rule is wider than comments: **NI's generator reads nothing before `<VI>` but the `<?xml?>`
  declaration.** `lvai_check_aixml` answers `ok: true` for such a document today.
- **The export never carries the extension back.** F's VI exports exactly like A's: AIXML
  read from a VI has no place for it, so the front-panel data must be kept on our side, not
  round-tripped through LabVIEW.
- Incidentally, the content model names two child elements of `<VI>` this repository has
  never used: `Call` and `FixedConst`.

## 3. Results on the pylabview composer

| variant | result |
|---|---|
| A baseline | composed, 5 645 bytes, `execState 1` |
| B `<FrontPanel>` | refused: `element <FrontPanel> is not composable yet` (`composerRefused`), nothing written |
| C `<fp:FrontPanel>` | refused: `element <{urn:labviewmcp:frontpanel}FrontPanel> is not composable yet` |
| F2 PIs after `</VI>` | composed, `execState 1` |
| G PI before `<VI>` | composed, **full VI**, `execState 1` - our parser does not share NI's defect |

The composer refuses an unknown element loudly and by name, and ignores PIs wherever they
are. It is therefore the natural place to IMPLEMENT a front-panel section later: it already
writes the panel heap objects, so positions and sizes are values it sets, not a new route.

## 4. What this means for the design

Three shapes keep the gRPC route untouched, in order of preference:

1. **Strip before the RPC.** Put the extension in our own namespace INSIDE the document
   where it belongs (`<lvmcp:FrontPanel>` as a child of `<VI>`), and have OUR server remove
   every element and attribute in that namespace before it hands the document to
   `ValidateAIXML` / `ConvertAIXMLToVI`. NI's service never sees it, so its tolerance is not
   being relied on at all - this server is the only client that sends AIXML to it. The
   composer reads the same section natively. Measured here: if the strip is ever skipped,
   NI refuses the document LOUDLY (`-2628` naming the element), so a gap in the strip cannot
   produce a silently wrong VI. **The one thing to strip with it is the namespace
   declaration - harmless (D2), but there is no reason to send it.**
2. **A trailing section after `</VI>`** - a processing instruction (F, F2). Works unmodified
   against NI today, needs no strip. Weaker: a PI is unstructured text to an XML parser, so
   the section has to be parsed twice, and it cannot contain `?>`. And it is one move away
   from the silent failure - the same PI placed BEFORE `<VI>` empties the VI.
3. **A sidecar file** (`X.fp.xml` beside `X.xml`). No interaction with NI at all, but two
   files to keep in step and to name in every tool.

A trailing XML COMMENT (G2) works too and is listed only to be rejected: comments are
exactly where this format has bitten twice already, and an editor or formatter that moves
one to the top of the file produces an empty VI with no error anywhere.

The design that builds on this - option 1, with the strip as the single route to NI - is
written up in `docs/aixml-frontpanel-proposal.md` (2026-10-24, not implemented).

## 5. Not measured

- Whether `ApplyAIXMLToVI` behaves differently - it is not called (CLAUDE.md).
- Large trailing sections (only a few hundred bytes were tried).
- Whether NI's EXPORT ever emits anything after `</VI>` (none of the five exports did).
