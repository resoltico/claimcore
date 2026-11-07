For the focused development loop, final qualification scope and optional performance probes, see
[Verification](docs/contributing.md#verification) in the contributing guide.

Pre-1.0: no cross-version durable compatibility, migrations or performance budgets. Every durable
format change requires a fresh installation. Investigate performance only for an observed problem,
with a throwaway measurement. Test expectations are hand-authored; do not add generated snapshot
fixtures.
