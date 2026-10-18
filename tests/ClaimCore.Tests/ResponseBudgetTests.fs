module ClaimCore.Tests.ResponseBudgetTests

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.ClientProtocol

let private id = Guid.Parse "d0000000-0000-4000-8000-000000000001"
let private reference = String('<', 80)
let private instant = DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero)
let private maximum = 9007199254740991L

let private fields length : CaseFields =
    {
        CaseReference = reference
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "LV"
        ClaimantName = String('<', length)
        InsurerName = "Synthetic"
        ClaimedAmount = "999999999999999999.9999"
        ClaimedCurrency = "EUR"
        PaymentDecisionDate = None
        PayableAmount = None
        PayableCurrency = None
        PaymentDate = None
        Status = CaseStatus.Closed
    }

let private snapshot length : CaseSnapshot =
    {
        Reference = reference
        Revision = maximum
        View = CaseView.Live(fields length, Disposition.Voided)
    }

let private receipt disclosure : OperationReceipt =
    {
        OperationId = id
        Position =
            {
                Sequence = maximum
                EntryHash = String('a', 64)
            }
        RecordedAt = instant
        RecordedBy = id
        Scope =
            ReceiptScope.Case
                {
                    Reference = reference
                    Revision = maximum
                    Act = (CaseActKind.Business BusinessActKind.AmendRegistration)
                    Snapshot = disclosure
                }
    }

let private encodedHistory count disclosure =
    OutcomeCodec.history (
        QueryOutcome.Succeeded
            {
                Entries = List.replicate count (HistoryEntry.FullEntry(receipt disclosure))
                NextCursor = Some(String('A', CursorRecord.MaximumTokenCharacters))
            }
    )
    |> OutcomeCodec.encode "case.history"

let private maximalPayload () =
    let request: CaseRequest =
        {
            OperationId = id
            CaseReference = reference
            ExpectedRevision = maximum - 1L
            Act = CaseAct.Business BusinessCommand.Close
        }

    let requestBytes = RequestRecord.encode request

    let payload length =
        LedgerEntry.encodePayload
            {
                Request = requestBytes
                Snapshot = CaseRecord.encodeSnapshot (snapshot length)
            }

    let mutable low, high = 1, 262144

    while low < high do
        let middle = (low + high + 1) / 2

        if (payload middle).Length <= 262144 then
            low <- middle
        else
            high <- middle - 1

    let accepted = snapshot low

    Expect.equal
        (payload low).Length
        262144
        "Fixture reaches the independent permanent payload maximum"

    Expect.isOk
        (LedgerEntry.decodePayload FormatLimits.MaximumPayloadBytes (payload low))
        "Permanent framing admits the maximum"

    Expect.isOk
        (CaseRecord.decodeSnapshot (CaseRecord.encodeSnapshot accepted))
        "Accepted history follows permanent format, not current name admission"

    accepted

let private fullHistoryBudget () =
    Expect.equal FormatLimits.MaximumPayloadBytes 262144 "Permanent format bound stays byte stable"

    Expect.equal
        CursorRecord.MaximumTokenCharacters
        512
        "Continuation bound stays independently covered"

    Expect.equal
        ProtocolProfile.MaximumResponseBytes
        16777216
        "Wire budget is not raised to fit history"

    Expect.equal
        ServiceLimits.MaximumFullHistoryPageSize
        10
        "Technical capacity independently remains ten"

    let metadata = encodedHistory 10 SnapshotDisclosure.Withheld

    Expect.isLessThanOrEqual
        metadata.Length
        (10 * 2048 + 1024)
        "Maximal fixed metadata and 512-byte continuation fit the conservative reserve"

    let bound = 10 * 6 * FormatLimits.MaximumPayloadBytes + 10 * 2048 + 1024

    Expect.isLessThan
        bound
        ProtocolProfile.MaximumResponseBytes
        "Sixfold escaping plus metadata stays below the response budget"

    let accepted = maximalPayload ()
    let bytes = encodedHistory 10 (SnapshotDisclosure.Disclosed accepted.View)

    Expect.isGreaterThan
        bytes.Length
        15000000
        "Real codec exercises worst escaping, not a small proxy"

    Expect.isLessThan
        bytes.Length
        bound
        "Actual full history fits the independently justified budget"

    Expect.throws
        (fun () -> encodedHistory 11 (SnapshotDisclosure.Disclosed accepted.View) |> ignore)
        "Encoder rejects overproduction independently of caller advice"

let private authorityFixture () =
    let prefix = "https://issuer.example/"

    let principal =
        PrincipalKey.human (prefix + String('+', 2048 - prefix.Length)) (String(char 1, 512))
        |> Result.defaultWith failwith

    let actor: ActorSummary =
        {
            ActorId = id
            Principal = principal
            Enabled = false
        }

    let grant: AuthorityGrant =
        {
            Role = Role.PaymentRecorder
            Scope = GrantTarget.CaseReference reference
        }

    actor, grant

let private boundedAuthority () =
    let actor, grant = authorityFixture ()

    let actors count =
        OutcomeCodec.actors (
            QueryOutcome.Succeeded
                {
                    Items = List.replicate count actor
                    NextCursor = Some(String('A', CursorRecord.MaximumTokenCharacters))
                }
        )
        |> OutcomeCodec.encode "authority.list"

    let grants count =
        OutcomeCodec.grants (
            QueryOutcome.Succeeded
                {
                    Actor = actor
                    Items = List.replicate count grant
                    NextCursor = Some(String('A', CursorRecord.MaximumTokenCharacters))
                }
        )
        |> OutcomeCodec.encode "authority.grants"

    Expect.isLessThan
        (actors 50).Length
        ProtocolProfile.MaximumResponseBytes
        "Maximal admitted actor headers fit"

    Expect.isLessThan
        (grants 50).Length
        ProtocolProfile.MaximumResponseBytes
        "Maximal scoped grants and target header fit"

    Expect.throws (fun () -> actors 51 |> ignore) "Actor overproduction refuses, never truncates"
    Expect.throws (fun () -> grants 51 |> ignore) "Grant overproduction refuses, never truncates"

[<Tests>]
let tests =
    testList
        "bounded response codecs"
        [
            testCase
                "full retained history fits sixfold escaping and bounded metadata"
                fullHistoryBudget
            testCase "authority headers and grants have actual bounded codecs" boundedAuthority
        ]
