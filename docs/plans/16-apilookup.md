# Plan 16 — `apilookup`: Revit API signatures from the running Revit

> Goal: `exec` code written against the API that actually exists in the user's Revit year,
> instead of a remembered one. pyRevit 7.0 (2026-10-06) ships the same idea as
> `lookup_revit_api`; VibeModel's version is a read-only command plus a hint on every failed
> `exec` compile.

> **STATUS (built 2026-10-19, branch claude-bot/t-352).** Headless tests on the framework's own
> types and a fixture (`ApiLookupTests`); live in Revit 2023 — see "Verification" below.

## Decisions

- **Reflection, not a shipped index.** `ApiLookup` reflects RevitAPI, RevitAPIUI and VibeModel's
  own assembly as loaded in this Revit (`GetExportedTypes`), so every year answers for itself —
  members that exist, `[Obsolete]` messages that say what replaces them. No file to keep current.
- **Type matching**: exact full or simple name (case-insensitive) → a unique substring of the
  simple name → else not found with the 5 closest names (prefix, then edit distance ≤ 2, then
  substring). Several substring hits are listed, never guessed. Two types with one simple name
  are tie-broken by a namespace order (`Autodesk.Revit.DB` first).
- **What a type shows**: header (kind, sealed/abstract/static), the `using` line — and, because
  exec's wrapper only imports System(.Collections.Generic/.Linq/.Text) and Autodesk.Revit.DB/.UI,
  "exec does not import it: write <full name>" for anything else (a method body cannot add a
  `using`); base type; **how to get one**; declared properties, methods grouped by overload,
  events, enum values; `[Obsolete: message]` markers. `--all` adds inherited members (object's
  own are left out), `--filter` narrows by member name, 60 members max with a `--filter` hint.
- **How to get one**: constructors; for an Element subclass the collector line
  (`new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()`). The Revit API
  reference (ElementClassFilter remarks) lists classes OfClass refuses — Room, Space, Area and
  their tags and tag types, CombinableElement, Mullion, Panel, AnnotationSymbol(Type), the
  reinforcement and truss types, and every subclass of Material, CurveElement, ConnectorElement
  and HostedSweep; for those the line uses the nearest base class Revit accepts and narrows in code
  (`OfClass(typeof(CurveElement)).OfType<ModelLine>()`, `OfClass(typeof(SpatialElement)).OfType<Room>()`),
  and when none qualifies (CombinableElement, Element itself) there is no line. Then every public method/property in the scanned
  assemblies that returns the type or a subtype: static ones on the type first (`Wall.Create`),
  then static elsewhere, then instance members elsewhere (`doc.ActiveView`, receivers named
  `doc` / `uiDoc` / `uiApp` as in exec). An instance member of the type's own hierarchy is left
  out (StringBuilder.Append needs a StringBuilder already). A member returning the base type is
  no way to get a subtype (doc.GetElement → Element is not listed under Wall). 12 lines max. The
  index is built once per Revit session on first use.
- **exec compile errors** CS1061/CS0117 ("'X' does not contain a definition for 'Y'") and CS0246
  ("type or namespace name 'Z' could not be found") append `Hint: apilookup X.Y` plus the first
  20 lines of that lookup (≤ 2 lookups) — the closest member names or the real overloads.
- "Not found" is an answer, not an error (`ok:true`, `data.found:false`, candidates).
- Pure (`Services/Claude/ApiLookup.cs`), command `Services/Claude/Commands/ApiLookupCommand.cs`.

## Verification (Revit 2023, 2026-10-19 — same session as plan 15)

- `apilookup Wall` 0.28 s the first time (the index), every later lookup 0.01–0.03 s. It shows the
  collector line and the five `Wall.Create` overloads under "How to get one", then properties
  (`Width : double { get; }` …) and methods.
- `apilookup Wal` → five candidates (Wall, FaceWall, WallKind, WallSide, WallType), never a guess;
  `Waal` → closest Wall, Visual.Wave; `Wall.Flipp` → Wall.Flipped, Wall.Flip.
- `StructuralType` → enum values and "exec does not import it: write
  Autodesk.Revit.DB.Structure.StructuralType"; `TransactionHelper` → VibeModel's own helpers.
- `Document.Export` → the GBXML overload marked `[Obsolete: This method is deprecated in Revit 2023 …
  use 'Document.Export(String, String, GBXMLExportOptions)' instead.]`.
- **Found live**: the compiler on this PC answers in Dutch ("Autodesk.Revit.DB.Wall bevat geen
  definitie voor Widht …", no quotes), so the first, English-text matcher never fired. Rewritten to
  the error number + the identifier at the error position; tested headless against the real CodeDom
  compiler. Live afterwards: `w.Widht` → `Hint: apilookup Wall.Widht … Closest: Wall.Width`;
  `typeof(Waal)` → `Hint: apilookup Waal … Closest: Autodesk.Revit.DB.Wall`.
- **Changed in the code review, not re-run in Revit**: the collector line for classes OfClass
  refuses (unit-tested with Revit-named classes built at test time); a variable named `type` no
  longer taken for the missing member; constructors no longer offered as "closest member".
