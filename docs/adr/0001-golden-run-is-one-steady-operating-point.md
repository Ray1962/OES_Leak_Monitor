# A Golden Run describes one steady operating point

A `GoldenRun` holds one mean and one σ per ratio, and the engine holds one active run at a time.
That is enough for a recipe that settles at a single operating point and stays there, and it is
not enough for a multi-step PECVD or PVD recipe, where each step has its own gas flows and power
and therefore its own leak-free levels. We are keeping the single-operating-point model for now
and answering only the two cases that do not need more — a recipe that ramps before it settles,
and one whose run is shorter than a capture window — with an offline builder that picks the steady
segment afterwards and pools several runs (`BaselineBuilder`).

## Why not model the steps now

The shape of a multi-step baseline depends on a question nobody has answered yet: **what tells the
app which step it is in?** Today nothing does. The SECS interface reports upward only and carries
no inbound step or recipe field; there is no recipe concept anywhere in the app; the only signals
available are the spectrum itself and time since the recorder opened. Each candidate implies a
different structure — a host-supplied step id, a set of detected boundaries with confidence, or an
operator-maintained table — and `GoldenRun` is persisted in `settings.json`, so a field guessed
wrong is one every existing settings file then has to be migrated away from.

## Consequences

- A multi-step recipe can be monitored one step at a time: capture a Golden Run per step and
  switch the active baseline when the step changes. Nothing automates that switch.
- Building one baseline out of two operating points is *worse* than having none: the pooled σ
  spans the gap between them, so thresholds derived from it are wide enough to miss a real leak
  while looking configured. This is why `BaselineBuilder` allows only one window per recording —
  two windows out of one run are either the same operating point with a gap in the middle, or the
  error just described.
- When the step signal question is answered, this decision should be revisited rather than worked
  around. The natural extension is a Golden Run holding several named segments, selected by
  whatever that signal turns out to be.

## Addendum, 2026-10-23: the step signal exists now — for scoping, not for baselines

The question above has a partial answer. The main tool's own process events reach the app:
the fab's EAP forwards the tool's CEIDs (PM3 ProcessStart / ProcessEnd) as S2F41
`TOOL_EVENT`, and a CEID table turns them into **process windows** naming a process class
(issues #25–#35). So the signal is host-supplied, it names a class, and it marks the
boundaries exactly.

What it is used for is narrower than this ADR anticipated. A window decides **which ratios
judge** — a ratio scoped to class A judges inside an A window and stands down elsewhere — and
nothing about the Golden Run's shape changed: it still holds one mean and σ per ratio. What
lets a chamber that interleaves processes have a baseline per process is the per-ratio
`ProcessClass` scoping that already existed (one ratio entry per process, each learning only
from its own class's frames), not a segmented Golden Run. Live capture inherits the window
through the same class check, so a class-A ratio learns only from A-window frames.

This decision therefore still stands. Revisit it — and design the "Golden Run holding several
named segments" this ADR names as the natural extension — when one of these becomes true:

- the same ratio must be judged in two processes against two baselines (today that is two
  ratio entries with the same lines and different classes, which is workable but duplicates
  configuration);
- a single tool window spans more than one operating point (a multi-step recipe the tool
  reports as one ProcessStart / ProcessEnd), so the window alone no longer identifies the
  operating point;
- the Baseline Builder starts using the window class each recording now carries in its
  `.acq.json`, at which point an offline build per class needs a home for its result.
