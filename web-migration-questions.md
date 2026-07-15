# Web Migration — Questions & Decisions Log

Questions accumulated during the full migration pass (step-flow tools + static
windows). Logged instead of asked, per instruction; review after the pass.
Decisions I made unilaterally are marked **DECIDED** with rationale — flag any
you want changed.

## Decisions made during the pass

- **DECIDED — Feature flag instead of 30 Developer buttons.** Every production
  command now opens the web version when the "Web UI" flag is ON (Developer
  panel toggle button; persisted machine-wide), and the WPF version when OFF
  (default). This keeps R25 (parallel until verified) without flooding the
  ribbon. The three existing parallel Web dev buttons (Push Coords, Delete
  Filters, Web Pilot) remain until cleanup.

## Open questions

*(appended as encountered)*
