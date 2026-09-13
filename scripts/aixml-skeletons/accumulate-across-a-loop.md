# `accumulate-across-a-loop.xml`

The header this skeleton used to carry inline. **An XML comment inside an AIXML document makes
`ConvertAIXMLToVI` write an EMPTY diagram at `errorCode 0`** - measured 2026-09-11 - so every
skeleton documents itself in a sibling `.md`. See `docs/aixml-reference.md`.

SKELETON - accumulate one block per iteration instead of keeping only the last.

Shows three shapes that were each got wrong at least once and re-derived three times:

  * a For Loop given its N by `maxin` (here `maxin="n.value"` from an int32 constant),
    not by indexing a literal array and not by a While Loop with a counter;
  * an accumulator on a shift register, seeded from an EMPTY array constant;
  * the seed / append / keep case pair that makes that accumulator work at all.

WHY THE CASE PAIR IS NOT OPTIONAL. The inner For Loop auto-indexes the accumulator against
the new block. On iteration 0 the accumulator is empty, so it runs min(0, N) = 0 times and
the accumulator stays empty FOREVER. Nothing reports this: it validates, it runs, and the
output is an empty array that looks exactly like a device error. Hence the inner case,
selected on "is the accumulator empty", which seeds instead of appending. The outer case,
selected on "is the new block empty", keeps what has already been accumulated when a late
read fails - without it the same zero-iteration arithmetic wipes the run's data.

WHAT YOU MUST NOT COPY FROM HERE

  * There are no `conIdx` attributes, deliberately. Which index is which slot depends on the
    pane pattern, which is a station setting (`DefaultConPane` in LabVIEW.ini). Call
    `lvai_connector_pane` with no argument and take the numbers from there.
  * `instance="WDT Append Waveforms DBL.vi"` is the polymorphic instance measured on the
    station below. Confirm it with `lvai_vi_terminals` rather than trusting this file: a
    skeleton is a frozen measurement, and this repository has already shipped one that
    stopped being true ("a generated VI always gets pattern 4815").

Uses SYMBOLIC uids - the server numbers them before LabVIEW sees the file, so elements can
be copied out of here into your own AIXML without renumbering anything.

WHAT "VALIDATED" COVERS HERE, exactly:

  * ValidateAIXML returns errorCode 0. **THE "3 166 bytes" THIS LINE USED TO CLAIM AS A REAL VI
    WAS AN EMPTY ONE** - re-measured 2026-09-11 at 3 172 bytes with a block-diagram heap holding
    `diag` and nothing else, because THIS FILE'S OWN HEADER COMMENT made ConvertAIXMLToVI
    discard the whole document at errorCode 0. The header now lives in the .md beside this file
    and the skeleton generates for real.
    Measured 2026-08-14 on LabVIEW 2026, DefaultConPane=4833.
  * The accumulation ARITHMETIC is NOT re-proven by this file. `block` is an array of
    waveforms, and the run tools can only set string inputs, so running this skeleton feeds
    it an empty block and every iteration takes the "keep" branch. The shape is copied from
    the diagram of C:\temp\DaqReadAndTDMS.vi, where it WAS verified behaviourally at
    3 channels x 500 samples through a harness that built synthetic waveforms on the diagram.
    So the arithmetic is inherited evidence, not a measurement of this file.

Worth adding when someone next touches this: a sibling self-test that builds a synthetic block
on the diagram, so the skeleton proves its own central claim instead of inheriting it.

## The note that used to sit inline at the Indicator

> value="" is REQUIRED on an Indicator, not only on a Control. Leaving it off fails
> validation with `missing required attribute 'value'` and a line number, which is clear
> enough once you know that Indicator and Control share the schema.
