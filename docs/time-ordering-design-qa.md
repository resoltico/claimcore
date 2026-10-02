# Time design challenge

This separate pass challenges clock ownership and expiration rather than equating every timestamp.

- UTC cannot replace elapsed time for process budgets. A fake clock must move wall and monotonic
  time independently; a rollback must not refresh an expired session/token. Forward UTC changes
  may conservatively refuse a credential. Sample ticket clocks inside the gate, after contention.
- An issuer's expires_in describes remaining lifetime around issuance, not after body delivery.
  Start the conservative budget immediately before HTTPS SendAsync, after interactive authorization.
  Refuse delivery beyond the safety budget; do not implicitly retry an already dispatched mutation.
- A captured instant before an authority lock is not current admission time. Remove that argument
  from internal ports rather than retain an ignored clock input or make callers choose a new source.
  Read clock_timestamp after the locks, never PostgreSQL transaction-start now(). Compare full UTC
  instants and preserve existing microsecond canonical precision.
- Fresh expiry checks must not invalidate exact replay of already witnessed authority. Put them in
  fresh-operation paths after identity/replay lookup. Expiration denies a new decision; it does not
  rewrite acceptance, abandon settlement or automatically clear a pending authority phase.
- Case-list continuation validation and issuance use the same primary observation for one page.
  Recovery/history cursors are positions, not grants or independent freshness evidence. Do not claim
  their encoding prevents a privileged caller from choosing another query position.
- Database wall time can itself move. This design removes differences between application hosts,
  not the deployment requirement for trustworthy synchronized UTC. Signed health rejects future
  checkedAt and ValidUntil equality; ordered witness evidence and revisions remain independent of
  clock order. Preserve historical readback after expiry without granting new mutation authority.
- Hosting calendar vectors must exercise one captured instant, leap dates, UTC/local midnight and
  a daylight-saving transition using the stored zone. Do not infer identical rules across different
  tzdata releases; the existing operational limitation remains explicit.
- Full-audit cadence already uses Stopwatch and a sticky overdue/failure gate. Keep its completion
  spacing, bounded audit and shutdown ownership. Test the exact overdue boundary without adding a
  second scheduler or replacing correct platform timers.

Accepted with these constraints. Revisit temporal placement if proof verification or implementation
reveals another pre-lock observation. Verify actual database boundaries, complete inventories and the
final published-client/CI state before the explicitly requested merge.
