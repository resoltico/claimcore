# Synthetic CLI-v3 walkthrough

All names and facts in this directory are fictional. Complete [Getting
started](../docs/getting-started.md) and its database migration before submitting the requests.

The files form one deterministic CLI-v3 `command.execute` sequence for `DEMO-0001`. Each invocation
has a fixed operation ID and expected revision, so use a dedicated synthetic database without that
reference. The host's business date must be on or after 2026-08-20.

## Expected sequence

| Order | Request | Accepted revision and state |
|---|---|---|
| 01 | [Open](01-open.json) | 1; `OPENED`, undecided. |
| 02 | [Decide](02-decide.json) | 2; `OPENED`, unpaid decision. |
| 03 | [Record payment](03-record-payment.json) | 3; `OPENED`, payment recorded. |
| 04 | [Close](04-close.json) | 4; `CLOSED`, payment retained. |
| 05 | [Reopen](05-reopen.json) | 5; `OPENED`, payment retained. |
| 06 | [Clear payment record](06-correct-payment-record.json) | 6; `OPENED`, decision retained and payment date cleared. |

This sequence demonstrates that status and payment progress are independent. The final correction
removes an erroneously attributed payment date; it does not reverse money, and the earlier paid
snapshot remains in history. No correction-reason field is requested. The first four files are enough
for a simple open-to-close path.

The authored amounts `"1000.00"` and `"750.00"` remain significant to exact request identity.
Accepted case views render their decimal values canonically as `"1000"` and `"750"`. The exact
case-reference spelling `DEMO-0001` is the immutable top-level target of all six requests.

## Run the sequence

Select the private runtime connection file created during setup, then run from the repository root:

```sh
export CLAIMCORE_CONNECTION_FILE="/absolute/private/path/app.connection"
```

PowerShell:

```powershell
$env:CLAIMCORE_CONNECTION_FILE = 'C:\absolute\private\path\app.connection'
```

Submit the strict JSON invocations in order and stop after any failure:

```text
dotnet run --project src/ClaimCore.Cli -c Release --no-build -- call < examples/01-open.json
dotnet run --project src/ClaimCore.Cli -c Release --no-build -- call < examples/02-decide.json
dotnet run --project src/ClaimCore.Cli -c Release --no-build -- call < examples/03-record-payment.json
dotnet run --project src/ClaimCore.Cli -c Release --no-build -- call < examples/04-close.json
dotnet run --project src/ClaimCore.Cli -c Release --no-build -- call < examples/05-reopen.json
dotnet run --project src/ClaimCore.Cli -c Release --no-build -- call < examples/06-correct-payment-record.json
```

## Replay and reuse

Reapplying an accepted invocation returns its historical receipt, not the current case. Use the
`case.get` endpoint through `call` to read current state and advisory actions. Receipt and history
snapshots advertise no actions. Replaying all six files neither resets the case nor creates more
revisions. Changed content under an already-used operation ID is a conflict.

To create a different synthetic case, change its reference and every operation ID, then preserve the
ordering and expected revisions. Do not reconstruct a retry from canonically rendered response
amounts. The exact recovery procedure is owned by [CLI and
protocol](../docs/cli.md#canonical-request-identity-and-recovery).

These examples do not cover every transition or legal state. Domain and CLI tests include further
scenarios, including differently denominated amounts. Nothing about the example amounts establishes
an insurance coverage rule.

<a id="cc-cli-001"></a>
## CC-CLI-001 — Published synthetic walkthrough

The published CLI must accept this exact six-invocation CLI-v3 sequence through `call` against an
isolated database migrated through 004. The resulting current view is version 6 and `OPENED`, retains
the decision, clears the payment date, exposes exactly the thirteen documented business fields, and
has exactly six ordered accepted-history entries. Exact replay of the final invocation returns its
retained receipt and creates no seventh entry.
