# Example Office — core rules (always on)

Source: Revit modelling manual v1.0. This model was started from `EX_Office_2023_template.rte`; everything prefixed `EX_` is office standard and wins over generic Revit habits. Ask `standards <topic>` (phasing, coordinates, families, views, sheets, parameters, naming, export, checklist) for the full sheet.

**File and phasing**
- One .rvt per project part, named `<discipline number>_<part>.rvt`. Existing, demolished, temporary and new work live in ONE file through phasing.
- Phases: `1. Existing`, `2. New construction`, `3. Final`. New work: created `2. New construction`, demolished None. Temporary works: created and demolished in `2. New construction`. Never leave a model element without a phase.
- A view's phase filter and phase start with the same digit (`2.x` with `2. New construction`).

**Modelling**
- Model in 3D what must produce quantities; everything else is 2D annotation.
- Families: what the template has, then the office library, only then a new project family. Office prefixes: `EX_`, `NLRS_`, `GM_`.
- Every new object gets `EX_ObjectCode` and `EX_ObjectDescription`.

**Coordinates and underlays**
- The survey point is the national grid origin: never move it. Shared coordinates come from the base map; the project base point sits on a rounded value and is pinned.
- Link CAD, never import; off-axis correction off, orient to view off, then pin. Never rotate the model or an underlay to get a straight view — rotate the scope box or crop region.

**Views and sheets**
- Work views start from an `EX_W_*` view template; plot views keep their `EX_P_*` template.
- Sheets use the `EX_TITLEBLOCK`; stamp fields are `EX_META_*` parameters.
