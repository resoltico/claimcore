# Resource lifetime design challenge

This separate pass challenges ownership transfer, interruption races and claimed evidence.

- Register each acquired child before another fallible step. Transfer the completed aggregate only
  after constructing it; a disposal fault must not prevent attempting the remaining children.
  Keep the scope internal to Hosting and synchronous construction, without locks or speculative
  options. A real witness store's closed capability is a stronger control than only fake counters.
- WitnessProtocol already disposes its store and custody in a finally chain. KeyRing already wipes
  partial construction. Preserve those protections instead of replacing them or treating those
  paths as findings. Runtime's readiness read must precede audit-worker creation; do not allocate
  a worker and then rely on failed adoption to discover it.
- A deferred cleanup task must complete normally with bounded failure knowledge. Do not retain
  provider exceptions in an unobserved promise. A last lease must still release without throwing
  over its definite operation result; a subsequent disposer can observe cleanup failure.
- Load issuer trust only after validating all OIDC values. Protect the returned trust root across
  later configuration steps, own both certificates before metadata verification, and dispose the
  application before runtime/certificate owners. Certificate validation belongs with acquisition,
  including disposal if native property inspection throws. Keep platform key-storage policy.
- A background stdin reader abandoned after WaitAsync cancellation is rejected. Console input can
  remain blocked despite cancellation, so use process termination for process-owned blocking IO.
  Protect interruption and BeforeRemoteDispatch with the same gate: if interruption decides 130,
  later mutation dispatch must be impossible. Possible mutation dispatch or incomplete mutation
  output requires 4. No stderr write in the signal callback may delay termination. Preserve completed
  session-frame knowledge and do not kill the operating-system-owned browser.
- A timeout reset per byte does not bound a whole frame. Use cancellable NetworkStream reads under
  one linked frame deadline and await their completion before disposing token/stream owners. Initial
  BEGIN and later OBSERVE remain 30-second waits; held capture reads use the remaining lease budget
  and existing overall capture token. Keep exact bytes, EOF/refusal behavior and the 16 KiB limit.
- CLI HTTP responses and request deadlines already have scoped disposal; native framing callers
  await dispatch before closing their cached session. Do not invent concurrent-dispose support for
  borrowed client objects without a supported caller. Browser response deadlines deliberately bound
  knowledge while observing the still-running promise; do not infer failed commit or casually change
  the established dispatched-mutation contract.

Accepted with these constraints. Use real process/signal and socket controls alongside ownership
failure tests, complete suite inventories and published-host verification. Keep source inspection
separate from executed evidence and disclose any unsupported platform condition.
