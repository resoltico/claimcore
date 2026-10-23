module ClaimCore.Tests.PayloadBudgetTests

open ClaimCore.Contract

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private id = Guid.Parse "d0000000-0000-4000-8000-000000000001"
let private reference = String('"', 80)
let private instant = DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero)
let private maximum = 9007199254740991L

let private fields length : CaseFields =
    {
        CaseReference = reference
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "LV"
        ClaimantName = String(char 1, length)
        InsurerName = String(char 1, length)
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
    OutcomeMapping.caseHistory (
        ReadReply.Succeeded
            {
                Entries = List.replicate count (HistoryEntry.FullEntry(receipt disclosure))
                NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
            }
    )
    |> ClaimCore.Contract.Protocol.Replies.encode

let private fullHistoryBudget () =
    let accepted = snapshot TextFormat.MaximumFreeTextBytes
    let bytes = encodedHistory 50 (SnapshotDisclosure.Disclosed accepted.View)
    let perEntry = 2 * 6 * TextFormat.MaximumFreeTextBytes + 6 * 80 + 2048
    let bound = 50 * perEntry + 1024

    Expect.isOk
        (CaseRecord.decodeSnapshot (CaseRecord.encodeSnapshot accepted))
        "Permanent format admits worst escaping"

    Expect.isLessThan
        bytes.Length
        bound
        "Real full-page codec fits the computed field and metadata budget"

    Expect.isLessThan
        bound
        ClaimCore.Contract.Limits.MaximumResponseBytes
        "All fifty entries fit the response limit"

    Expect.equal ClaimCore.Contract.Limits.MaximumResponseBytes 4194304 "Public response limit"

    Expect.equal FormatLimits.MaximumPayloadBytes 32768 "Permanent framed payload bound"

    Expect.throws
        (fun () -> encodedHistory 51 (SnapshotDisclosure.Disclosed accepted.View) |> ignore)
        "Overproduction refuses"

let private authorityFixture () =
    let prefix = "https://issuer.example/"

    let principal =
        PrincipalKey.human (prefix + String('+', 2048 - prefix.Length)) (String(char 1, 512))
        |> Result.defaultWith failwith

    let actor: ActorSummary =
        {
            ActorId = id
            Principal = PrincipalKey.describe principal
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
        OutcomeMapping.authorityList (
            ReadReply.Succeeded
                {
                    Items = List.replicate count actor
                    NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                }
        )
        |> ClaimCore.Contract.Protocol.Replies.encode

    let grants count =
        OutcomeMapping.authorityGrants (
            ReadReply.Succeeded
                {
                    Actor = actor
                    Items = List.replicate count grant
                    NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                }
        )
        |> ClaimCore.Contract.Protocol.Replies.encode

    Expect.isLessThan
        (actors 50).Length
        ClaimCore.Contract.Limits.MaximumResponseBytes
        "Maximal admitted actor headers fit"

    Expect.isLessThan
        (grants 50).Length
        ClaimCore.Contract.Limits.MaximumResponseBytes
        "Maximal scoped grants and target header fit"

    Expect.throws (fun () -> actors 51 |> ignore) "Actor overproduction refuses, never truncates"
    Expect.throws (fun () -> grants 51 |> ignore) "Grant overproduction refuses, never truncates"

[<Tests>]
let tests =
    testList
        "bounded response codecs"
        [
            testCase
                "fifty full entries fit the budget derived from permanent field bounds"
                fullHistoryBudget
            testCase "authority headers and grants have actual bounded codecs" boundedAuthority
        ]
