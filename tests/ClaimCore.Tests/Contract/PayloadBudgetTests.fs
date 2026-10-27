module ClaimCore.Tests.PayloadBudgetTests

open ClaimCore.Contract.Protocol

open ClaimCore.TestSupport

open ClaimCore.Contract
open ClaimCore.Json

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private id = Guid.Parse "d0000000-0000-4000-8000-000000000001"
let private reference = String('"', 80)
let private instant = DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero)
let private maximum = 9007199254740991L

let private fields length : CaseFields =
    {
        CaseReference = CaseFixtures.reference reference
        Status = CaseStatus.Closed
        Registration =
            CaseFixtures.registration
                { Inputs.registration with
                    ClaimantName = String(char 1, length)
                    InsurerName = String(char 1, length)
                    ClaimedAmount = "999999999999999999.9999"
                }
        Decision = None
        Payment = None
    }

let private snapshot length : CaseSnapshot =
    {
        Reference = CaseFixtures.reference reference
        Revision = maximum
        State = CaseState.Voided(fields length)
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
        Subject =
            ReceiptSubject.Case
                {
                    Reference = CaseFixtures.reference reference
                    Revision = maximum
                    Act = (CaseActKind.Business BusinessActKind.AmendRegistration)
                    Snapshot = disclosure
                }
    }

let private encodedHistory count disclosure =
    Codec.encode
        Api.caseHistory.Output
        (ReadReply.Succeeded
            {
                Entries = List.replicate count (HistoryEntry.FullEntry(receipt disclosure))
                NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
            })

let private fullHistoryBudget () =
    let accepted = snapshot Formats.MaximumFreeTextBytes

    let bytes =
        encodedHistory
            50
            (SnapshotDisclosure.Disclosed(LiveCase.ofState accepted.State |> Option.get))

    let perEntry = 2 * 6 * Formats.MaximumFreeTextBytes + 6 * 80 + 2048
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

    Expect.equal Formats.MaximumPayloadBytes 32768 "Permanent framed payload bound"

    Expect.throws
        (fun () ->
            encodedHistory
                51
                (SnapshotDisclosure.Disclosed(LiveCase.ofState accepted.State |> Option.get))
            |> ignore)
        "Overproduction refuses"

let private authorityFixture () =
    let prefix = "https://issuer.example/"

    let principal =
        PrincipalKey.human (prefix + String('+', 2048 - prefix.Length)) (String(char 1, 512))
        |> Result.defaultWith (AuthorityInputFailures.token >> failwith)

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
        Codec.encode
            Api.authorityList.Output
            (ReadReply.Succeeded
                {
                    Items = List.replicate count actor
                    NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                })

    let grants count =
        Codec.encode
            Api.authorityGrants.Output
            (ReadReply.Succeeded
                {
                    Actor = actor
                    Items = List.replicate count grant
                    NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                })

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
