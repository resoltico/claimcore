module ClaimCore.Tests.PayloadBudgetTests

open ClaimCore.Protocol

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

let private id = Guid.Parse "d0000000-0000-4000-8000-000000000001"
let private reference = String('"', 80)
let private instant = DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero)
let private maximum = 9007199254740991L

let private fields length : CaseFields =
    {
        Work = CaseWork.empty
        CaseReference = CaseFixtures.reference reference
        Status = CaseStatus.Closed
        Registration =
            CaseFixtures.registration (
                ClaimCore.TestSupport.Inputs.registrationWith
                    [
                        BusinessField.ClaimantName, (String(char 1, length))
                        BusinessField.InsurerName, (String(char 1, length))
                        BusinessField.ClaimedAmount, ("999999999999999999.9999")
                    ]
                    (Inputs.registration)
            )
        Progress = ClaimCore.TestSupport.CaseFixtures.progress (None) (None)
    }

let private snapshot length : CaseSnapshot =
    {
        CaseId = CaseFixtures.caseId (System.Guid.Parse "20000000-0000-4000-8000-000000000001")
        Reference = CaseFixtures.reference reference
        Revision = CaseFixtures.revision maximum
        State = CaseState.Voided(fields length)
    }

let private receipt disclosure : OperationReceipt =
    {
        OperationId = (CaseFixtures.operationId (id))
        Position =
            {
                Sequence = CaseFixtures.sequence maximum
                EntryHash =
                    (String('a', 64)
                     |> System.Convert.FromHexString
                     |> EntryHash.ofBytes
                     |> Option.get)
            }
        RecordedAt = instant
        RecordedBy = (CaseFixtures.actorId (id))
        Subject =
            ReceiptSubject.Case
                {
                    CaseId =
                        CaseFixtures.caseId (
                            System.Guid.Parse "20000000-0000-4000-8000-000000000001"
                        )
                    Reference = CaseFixtures.reference reference
                    Revision = CaseFixtures.revision maximum
                    Act = CaseActKind.AmendRegistration
                    Snapshot = disclosure
                }
    }

let private encodedHistory count disclosure =
    let difference =
        match disclosure with
        | SnapshotDisclosure.Disclosed(LiveCase.Active fields | LiveCase.Voided fields) ->
            Some
                {
                    Lifecycle = None
                    Fields =
                        [
                            {
                                FieldName = BusinessField.ClaimantName
                                Before = Some(String(char 2, PartyName.MaximumBytes))
                                After =
                                    Some(
                                        PartyName.format (Registration.claimant fields.Registration)
                                    )
                            }
                            {
                                FieldName = BusinessField.InsurerName
                                Before = Some(String(char 2, PartyName.MaximumBytes))
                                After =
                                    Some(
                                        PartyName.format (Registration.insurer fields.Registration)
                                    )
                            }
                        ]
                }
        | _ -> None

    Codec.encode
        Api.caseHistory.Output
        (Outcome.Succeeded
            {
                CaseId = (snapshot 1).CaseId
                Reference = (snapshot 1).Reference
                Entries =
                    List.replicate
                        count
                        (HistoryEntry.FullEntry
                            {
                                Receipt = receipt disclosure
                                Difference = difference
                            })
                NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
            })

let private fullHistoryBudget () =
    let accepted = snapshot PartyName.MaximumBytes

    let bytes =
        encodedHistory
            50
            (SnapshotDisclosure.Disclosed(LiveCase.ofState accepted.State |> Option.get))

    let perEntry = 6 * 6 * PartyName.MaximumBytes + 6 * 80 + 2048
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
        ClaimCore.Protocol.Limits.MaximumResponseBytes
        "All fifty entries fit the response limit"

    Expect.equal ClaimCore.Protocol.Limits.MaximumResponseBytes 4194304 "Public response limit"

    Expect.equal LedgerPayload.MaximumBytes 32768 "Permanent framed payload bound"

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
        |> Result.defaultWith (string >> failwith)

    let actor: Actor =
        {
            ActorId = (CaseFixtures.actorId (id))
            Principal = principal
            Enabled = false
        }

    let grant: ScopedGrant =
        {
            Role = Role.PaymentRecorder
            Scope = GrantScope.Case(CaseFixtures.caseId id, CaseFixtures.reference reference)
        }

    actor, grant

let private boundedAuthority () =
    let actor, grant = authorityFixture ()

    let actors count =
        Codec.encode
            Api.authorityList.Output
            (Outcome.Succeeded
                {
                    Items = List.replicate count actor
                    NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                })

    let grants count =
        Codec.encode
            Api.authorityGrants.Output
            (Outcome.Succeeded
                {
                    Actor = actor
                    Items = List.replicate count grant
                    NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                })

    Expect.isLessThan
        (actors 50).Length
        ClaimCore.Protocol.Limits.MaximumResponseBytes
        "Maximal admitted actor headers fit"

    Expect.isLessThan
        (grants 50).Length
        ClaimCore.Protocol.Limits.MaximumResponseBytes
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
