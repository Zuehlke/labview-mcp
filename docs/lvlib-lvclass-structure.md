# The `.lvlib` and `.lvclass` file formats

Libraries and classes have no published schema either, so the rules below were derived by census
over **318 files — 129 `.lvlib` and 189 `.lvclass`, 5 283 `Item` elements** — drawn from a
production codebase, the examples shipped with LabVIEW 2026, and the DQMH module templates. All
318 parsed as well-formed XML with no failures. Names in the examples below are neutral
placeholders; the corpus is not part of this repository.

Counts are given throughout because they say how safe a rule is: `85/85` is a rule, `n=1` is an
anecdote.

This document exists because two things a documentation tool needs are **only** in these files and
nowhere in the gRPC interface: **which members are public**, and **which class derives from which**.
`lvai_describe_project` reports `vis`, `libraries` and `classes` but has no field for either.

For the project file that references these, see [`lvproj-structure.md`](lvproj-structure.md).
Unlike `aixml-reference.md` and `dqmh-patterns.md` this document is **not** embedded in the
assembly and has no knowledge tool.

## 1. Grammar

Same three-element grammar as `.lvproj` — only the root differs:

| File | Root element | Count |
|---|---|---|
| `.lvlib` | `<Library LVVersion="…">` | 129 |
| `.lvclass` | `<LVClass LVVersion="…">` | 189 |

`Item` carries `Name`, `Type` and an optional `URL`; `Property` carries `Name`, `Type` and its
value as element text. `URL` resolves the same way as in a project: **relative to the library
file's path**, so `../Foo.vi` is the sibling of the `.lvlib`. `/<vilib>/…` and `/<resource>/…`
name LabVIEW's own directories.

### Item types

| `Type` | Count | Meaning |
|---|---|---|
| `VI` | 4 163 | a member file — **`.vi`, `.vim` AND `.ctl` all use this type**, split them by extension |
| `Folder` | 518 | a virtual folder; the scope carrier (§2) |
| `Class Private Data` | 185 | the class's private data control (`.ctl`), one per class |
| `Document` | 129 | a non-VI file owned by the library — `.dll`, `.png`, … |
| `Parent` | 90 | one ancestor class, plain text (§3) |
| `Parent Libraries` | 85 | the container holding the `Parent` entries |
| `LVClass` | 61 | a class nested inside a library |
| `Friends List` | 16 | container for `Friended Library` entries — the *community* mechanism |

## 2. Access scope

Scope is written as an integer property. Two different property names carry it:

| Property | Where | Count |
|---|---|---|
| `NI.LibItem.Scope` (`Int`) | folders, and some items | 507 |
| `NI.ClassItem.MethodScope` (`UInt`) | class member VIs | 1 339 |

### The enum

| Value | Meaning | Evidence |
|---|---|---|
| `1` | **Public** | folders literally named `Public API` / `Public`; 1 142 class members |
| `2` | **Private** | folders named `Private`; the DQMH `Main.vi`; 366 + 38 occurrences |
| `3` | **Protected** (class) / **Community** (library) | 82 members sit in folders named `protected` and carry `MethodScope 3`; in a `.lvlib` the one folder found with `3` is named `Community` |
| `4` | not public, exact flavour **unverified** | 13 + 2 occurrences, mostly one pre-2015 library |
| *absent* | inherit from the containing folder; at root level → **Public** | — |

The correlation between the two property names is exact: every class member in a folder scoped `2`
carries `MethodScope 2`, every member in a folder scoped `3` carries `MethodScope 3`. LabVIEW
writes the *effective* scope onto each class member.

### The two propagation rules

- **`.lvlib`: the scope usually sits on the folder and the children inherit it.** In the DQMH
  module template every VI under the `Public API` folder (`Scope = 1`) carries no scope property of
  its own. A reader must propagate downward. But this is not absolute: **60 non-folder items in the
  corpus carry their own `NI.LibItem.Scope`**, and an item's own value wins over the folder's.
- **`.lvclass`: every member VI carries its own effective scope** in `NI.ClassItem.MethodScope`.
  No propagation needed — read it per item.

### Never read the folder name

A folder's name is documentation, not data. The corpus contains folders named `private` carrying
`NI.LibItem.Scope = 1` (12 members, all effectively **public**) and folders named `public` with no
scope property at all. Trust the property; the name is what someone typed years ago.

### Dynamic dispatch

Class members also carry `NI.ClassItem.IsStaticMethod` — `false` means the VI is **dynamic
dispatch**, i.e. overridable. Measured: 604 dynamic, 410 static. The same items carry
`NI.ClassItem.ConnectorPane` as an opaque `Bin` blob; it is not the connector pane in any readable
form (use `lvai_describe_vi` for that, see `aixml-reference.md`).

## 3. Inheritance

**LabVIEW writes the parent class in one of two mutually exclusive ways, and which one depends
purely on the version that last saved the file.** Measured over 189 classes:

| Representation | Classes | LVVersion |
|---|---|---|
| `Parent Libraries` / `Parent` items, plain text | 85 | **26xxxxxx only** (LabVIEW 2026) |
| `NI.LVClass.ParentClassLinkInfo`, encoded blob | 33 | every version **≤ 20xxxxxx** (incl. one 8.2) |
| neither → derives from `LabVIEW Object` | 71 | any |

Not one file carried both. A reader must therefore check both, in that order.

### The plain-text form (LabVIEW 2026)

```xml
<Item Name="Parent Libraries" Type="Parent Libraries">
    <Item Name="Abstraction.lvlib:Abstraction.lvclass" Type="Parent"
          URL="../../../Abstraction/Abstraction/Abstraction.lvclass"/>
    <Item Name="Actor Framework.lvlib:Actor.lvclass" Type="Parent"
          URL="/&lt;vilib&gt;/ActorFramework/Actor/Actor.lvclass"/>
</Item>
```

**The entries are the whole ancestor chain, nearest first** — the first `Parent` is the immediate
parent, the rest are its ancestors. 80 of 85 classes list one entry, 5 list two. `Name` is the
qualified name (`<owning library>:<class>`), `URL` resolves like any other.

### The encoded form (older files)

`NI.LVClass.ParentClassLinkInfo` and `NI.LVClass.Geneology` are LabVIEW **flattened strings**: a
plain 6-bit encoding, one character per 6 bits, offset `0x21`. Decoding them is 10 lines and yields
length-prefixed, readable names:

```python
def lv_decode(t):
    out, bits, n = bytearray(), 0, 0
    for ch in t:
        if ch in "\r\n\t ":
            continue
        bits = (bits << 6) | ((ord(ch) - 0x21) & 0x3F)
        n += 6
        if n >= 8:
            n -= 8
            out.append((bits >> n) & 0xFF)
    return bytes(out)
```

For a class `Message Queue.lvclass` owned by `BaseLib.lvlib` the first bytes come out as
`…\x0dBaseLib.lvlib\x15Message Queue.lvclassPTH0` — a `PTH0` path record with one
length-prefixed component per path element (`0x0d` = 13 = `len("BaseLib.lvlib")`). **Pull the names out with a regex over the decoded
bytes rather than parsing the binary record layout**; the layout was not worth reverse-engineering
and a regex over `[\w \-.()]{2,}\.lv(class|lib)` recovers every name.

- `ParentClassLinkInfo` → the **immediate parent**; take the last `.lvclass` name, the `.lvlib`
  before it is its owning library.
- `Geneology` (`Xml` type, value inside `<String><Val>`) → the **whole ancestry**, present in 178
  of 189 classes. Useful as a cross-check, and as the only source when a parent is outside the
  documented scope.

## 4. Library-level properties

The ones worth reading, with their frequency over all 318 files:

| Property | Count | Meaning |
|---|---|---|
| `NI.Lib.Icon` (`Bin`) | 318 | the library icon, a flattened + compressed LabVIEW image cluster. Not decoded here |
| `NI.Lib.Version` (`Str`) | 318 | `1.0.0.0`-style version |
| `NI.LV.All.SourceOnly` (`Bool`) | 309 | source-only library |
| `NI.Lib.SourceVersion` (`Int`) | 304 | LabVIEW's internal source version |
| `NI.Lib.HelpPath` (`Str`) | 113 | help file |
| `NI.Lib.ContainingLib` / `ContainingLibPath` | 66 | this library/class is nested inside another one |
| `NI.Lib.Description` (`Str`) | 12 | the library description — **usually absent**, so a generated document has to derive its summary |
| `NI.Lib.Locked` (`Bool`) | 14 | the library is locked |
| `NI.Lib.FriendGUID*` | 11+ | the friend/community mechanism, paired with the `Friends List` item |

Class-only properties: `NI.LVClass.FlattenedPrivateDataCTL` (185, the private data cluster as a
flattened blob), `NI.LVClass.Geneology` (178), `NI.LVClass.LowestCompatibleVersion` (147),
`NI.LVClass.ClassNameVisibleInProbe` (189).

## 5. Verified vs. assumed

**Verified by census or by a direct read:** the grammar and item types; the scope enum values 1–3
and both propagation rules; the folder-name warning; `IsStaticMethod`; both inheritance
representations, their version split and their mutual exclusivity; the 6-bit decoder; the property
frequencies.

**Assumed / open:** scope value `4` — non-public, but which of Community/Protected it means was not
established (13 occurrences, nearly all in one pre-2015 library). The `NI.Lib.Icon` and
`FlattenedPrivateDataCTL` blobs decode with the same 6-bit scheme but their inner structure
(compressed pixmap, flattened cluster) was not decoded. `Type="Document"` items were sampled, not
censused.
