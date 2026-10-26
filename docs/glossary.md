# Glossary

## Principal and actor

A principal is an exact issuer and immutable human subject or service client identity. An actor is its
registered identifier and enabled state. Current grants authorize actions on an installation or case.

## Admission and permanent format

Admission applies current rules to newly asserted values. Permanent format describes what a retained
value can represent. Reading retained values uses permanent format.

## Value module

A Domain module owns one value type and its `parse`, `admit`, `format` and `descriptor` functions.
CalendarDate, Money, CountryCode, PartyName, Reason and CaseReference are value modules.

## Declaration

The typed Domain declaration describes record groups, fields, act payloads and endpoint inputs.
Boundary codecs, catalog inputs and projection names derive from it.

## Case act

A requested business or stewardship change to one case. Domain parses its authored inputs into a
`CaseCommand`; transitions apply the typed act to accepted state.

## Decision and verdict

A `Decision` is the accepted payment decision: its date, payable amount and currency.
Application's `Verdict` is Proceed, Replay, Refuse or Fail. Only Proceed permits a transaction to commit;
the other verdicts discard staged changes.

## Applicable

Evidence that a case state permits an act, carrying the state the transition needs. Eligibility derives
from this evidence. Authorization and validation of the supplied values remain separate checks.

## Entry target

The case or authority object named by an accepted ledger entry. Application calls this `EntryTarget`;
the durable metadata grammar calls it `RecordedTarget`. The wire member `subject` keeps its meaning.

## Route

An endpoint description with input and output closures. Binding captures the handler with matching
request and response types, so dispatch can invoke the bound route without recovering erased values.

## Read and write transactions

`IReadTx` supplies primitives over one consistent snapshot. `IWriteTx` adds the head, nonce, append,
projection and fence primitives. The PostgreSQL adapter constructs it after acquiring the ledger head lock.

## Request gate

Web's checks for the connection, origin, credentials, media type, request size and antiforgery token
before Application handles a request. A rejected gate produces a transport failure.

## Runtime leases

Ownership of work using a runtime resource. Closing leases stops new acquisition; active work keeps its
lease until provider cleanup finishes.
