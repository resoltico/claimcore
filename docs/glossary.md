# Glossary

## Principal and actor

A principal is an exact issuer and immutable human subject or service client identity. An actor is its
registered identifier and enabled state. Current grants authorize actions on an installation or case.
Principal kinds and roles are closed typed vocabularies before Domain admission. `PrincipalInput` holds
authored identity fields; admitted state and outputs hold `PrincipalKey`. Domain admits issuer/subject and
scope values, then decides from authority facts loaded under the transaction. Storage adapters interpret
permanent spellings and report corrupted kinds/roles as integrity faults.

## Admission and permanent format

Admission applies current rules to newly asserted values. Permanent format describes what a retained
value can represent. Reading retained values uses permanent format.

## Value module

A Domain module owns one value type and its `parse`, `admit`, `format` and `descriptor` functions.
`CalendarDate`, `Money`, `CountryCode`, `PartyName`, `Reason` and `CaseReference` are value modules.
`Money` owns amount and currency together.

## Declaration

Domain declares business fields and typed act slots pairing injection with projection. Protocol owns
endpoint and interaction declarations. Request codecs own wire structure; catalog paths, kinds, constants
and conditions derive from their schemas. Semantic annotations attach by named definition/member;
projection columns derive from Domain field declarations.

## Case act

One of twelve flat requested changes to a case, including business work and stewardship. Domain parses
its authored inputs into a `CaseCommand`; transitions apply the typed act to accepted state.

## Decision and verdict

A `Decision` is the accepted payment decision: its date, payable amount and currency.
Application's `Verdict` is Proceed, Replay, Refuse or Fail. Only Proceed permits a transaction to commit;
the other verdicts discard staged changes.

## Public outcome

`Outcome<'success>` is Succeeded, Rejected or Failed. Preview and resolution use their own success payloads
inside that common shape. Execute retains Accepted, AlreadyAccepted, Rejected, Failed and OutcomeUnknown;
unknown commit/delivery is not a known failure. `Verdict` remains the internal decision-flow type.

## Input issues

A nonempty ordered collection of field-specific violations from independent checks in the same admission
phase. The wire carries `INVALID_INPUT.issues`; the CLI and browser present every issue. Parsing and
state/today admission stay sequential, and transition refusals stay single.

## Payment progress

Undecided, Decided or Paid, with a decision included in every Paid value. `CaseFields.Progress` is the
stored in-memory authority; separate decision/payment wire members are boundary projections.

## Ledger position

One sequence/hash model shared by receipts, heads and operator expectations. `EntryHash` owns a copied
32-byte value; boundaries choose hexadecimal formatting. Genesis is sequence zero with the zero hash.

## Applicable

Evidence that a case state permits an act, carrying the state the transition needs. Eligibility derives
from this evidence. Authorization and validation of the supplied values remain separate checks.

## Entry target

The case or authority object named by an accepted ledger entry. Application calls this `EntryTarget`;
the durable metadata grammar calls it `RecordedTarget`. The wire member `subject` keeps its meaning.

## Route

A Web-owned endpoint binding with input and output closures. Binding captures the handler with matching
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
