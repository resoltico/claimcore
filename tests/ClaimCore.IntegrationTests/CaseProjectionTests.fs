module ClaimCore.IntegrationTests.CaseProjectionTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat

let private installation = Guid.Parse "80000000-0000-4000-8000-000000000001"

let private snapshot (reference: string) : CaseSnapshot =
    let reference = CaseFixtures.reference reference

    {
        Reference = reference
        Revision = CaseFixtures.revision 1L
        State =
            CaseState.Active(
                {
                    CaseReference = reference
                    Status = CaseStatus.Opened
                    Registration = CaseFixtures.registration Inputs.registration
                    Progress = ClaimCore.TestSupport.CaseFixtures.progress (None) (None)
                }
            )
    }

let private stored (snapshot: CaseSnapshot) : StoredCase =
    {
        CaseId = CaseReferenceId.encode installation (CaseReference.format snapshot.Reference)
        Snapshot = snapshot
        SnapshotBytes = CaseRecord.encodeSnapshot snapshot
    }

let private history (snapshot: CaseSnapshot) : CaseHistory =
    {
        Revision = snapshot.Revision
        Snapshot = Some snapshot
        Reference = Some snapshot.Reference
        State = CaseState.Active()
        MissingPayloadAt = None
        PresentPayloadAt = Some(Sequence.create 1L |> Option.get)
        ErasedAt = None
    }

let private mismatch =
    Error
        {
            Sequence = None
            Finding = VerificationFinding.CaseProjection
        }

let private collection () =
    let a, b = stored (snapshot "synthetic-a"), stored (snapshot "synthetic-b")

    let histories =
        Map.ofList [ a.CaseId, history a.Snapshot; b.CaseId, history b.Snapshot ]

    Expect.equal (CaseFold.projection installation Map.empty []) (Ok()) "Empty coverage"

    for rows in [ [ a; b ]; [ b; a ] ] do
        Expect.equal
            (CaseFold.projection installation histories rows)
            (Ok())
            "Exact unordered coverage"

    for rows in [ [ a; a ]; [ a ]; [ a; b; a ]; [ a; stored (snapshot "unknown") ] ] do
        Expect.equal
            (CaseFold.projection installation histories rows)
            mismatch
            "Exact collection finding"

let private liveState () =
    Expect.isNone
        (CaseId.create Guid.Empty)
        "An empty projected case identity is refused before construction"

    let a = stored (snapshot "synthetic-a")
    let histories = Map.ofList [ a.CaseId, history a.Snapshot ]

    let fields =
        match a.Snapshot.State with
        | (CaseState.Active(fields) | CaseState.Voided(fields)) -> fields
        | _ -> failtest "Live fixture"

    for changed in
        [
            { a with
                Snapshot =
                    { a.Snapshot with
                        Reference = CaseFixtures.reference "another"
                    }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        Revision = CaseFixtures.revision 2L
                    }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        State = CaseState.Erased
                    }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        State = CaseState.Voided(fields)
                    }
            }
            { a with
                Snapshot =
                    { a.Snapshot with
                        State =
                            CaseState.Active(
                                { fields with
                                    Registration =
                                        { fields.Registration with
                                            ClaimedAmount = CaseFixtures.money "2000.00" "EUR"
                                        }
                                }
                            )
                    }
            }
        ] do
        Expect.equal
            (CaseFold.projection installation histories [ changed ])
            mismatch
            "Live mismatch"

let private erasedState () =
    Expect.isNone (CaseId.create Guid.Empty) "Erased cases also require admitted identity"
    let live = snapshot "synthetic-erased"

    let erased =
        { live with
            Revision = CaseFixtures.revision 2L
            State = CaseState.Erased
        }

    let row = stored erased

    let accepted =
        { history live with
            Revision = CaseFixtures.revision 2L
            Snapshot = None
            State = CaseState.Erased
            MissingPayloadAt = Some(Sequence.create 1L |> Option.get)
            PresentPayloadAt = None
            ErasedAt = Some(Sequence.create 2L |> Option.get)
        }

    let check (history: CaseHistory) (row: StoredCase) =
        CaseFold.projection installation (Map.ofList [ row.CaseId, history ]) [ row ]

    Expect.equal (check accepted row) (Ok()) "Erased identity and revision remain covered"

    for changed in
        [
            { row with
                Snapshot =
                    { erased with
                        Reference = CaseFixtures.reference "another"
                    }
            }
            { row with
                Snapshot =
                    { erased with
                        Revision = CaseFixtures.revision 1L
                    }
            }
            { row with Snapshot = live }
        ] do
        Expect.equal (check accepted changed) mismatch "Erased identity revision and state refuse"

    for changed in
        [
            { accepted with
                State = CaseState.Active()
            }
            { accepted with
                MissingPayloadAt = None
            }
        ] do
        Expect.equal (check changed row) mismatch "Erased evidence remains required"

let private projectionFields: CaseFields =
    {
        CaseReference = CaseFixtures.reference "synthetic-layout"
        Status = CaseStatus.Closed
        Registration =
            CaseFixtures.registration
                { Inputs.registration with
                    ClaimantName = "Synthetic claimant"
                    InsurerName = "Synthetic insurer"
                    ClaimedAmount = "1234.5678"
                }
        Progress =
            ClaimCore.TestSupport.CaseFixtures.progress
                (Some
                    {
                        Decision.PaymentDecisionDate = CaseFixtures.date "2026-08-04"
                        PayableAmount = CaseFixtures.money "987.6543" "USD"
                    })
                (Some
                    {
                        PaymentDate = CaseFixtures.date "2026-08-05"
                    })
    }


let private projectionBindings () =
    let snapshot =
        {
            Reference = projectionFields.CaseReference
            Revision = CaseFixtures.revision 4L
            State = CaseState.Voided projectionFields
        }

    let expected = stored snapshot

    let row =
        Map.ofList
            [
                "case_id", box (CaseId.value expected.CaseId)
                "case_reference", box "synthetic-layout"
                "revision", box 4L
                "state", box "VOIDED"
                "snapshot", box expected.SnapshotBytes
            ]

    let row = row |> Map.map (fun _ value -> nonNull value)

    Expect.equal
        (Projections.caseRow installation row)
        expected
        "Snapshot bytes round-trip with their independent metadata"

    for name, value in
        [
            "case_reference", box "different"
            "revision", box 5L
            "state", box "ACTIVE"
            "case_id", box (Guid.NewGuid())
            "snapshot", box [| 0uy |]
        ] do
        Expect.throwsT<RecordCorruptException>
            (fun () ->
                Projections.caseRow installation (Map.add name (nonNull value) row) |> ignore)
            "Every identity and snapshot mismatch is an integrity fault"

    let histories =
        Map.ofList
            [
                expected.CaseId,
                { history snapshot with
                    State = CaseState.Voided()
                }
            ]

    let noncanonical = Array.append [| byte ' ' |] expected.SnapshotBytes
    Expect.isOk (CaseRecord.decodeSnapshot noncanonical) "Whitespace retains snapshot meaning"

    Expect.equal
        (CaseFold.projection
            installation
            histories
            [
                { expected with
                    SnapshotBytes = noncanonical
                }
            ])
        mismatch
        "Verification compares stored bytes, not only decoded values"

let private storedRead (database: PostgresFixture.Database) =
    let id = CaseReferenceId.encode database.Fixture.InstallationId "synthetic-a"

    database.Fixture.Store.Read
        {
            Work = (fun tx -> tx.FindCase id)
            CancellationToken = VerificationSupport.token
        }

let private corruptReads database =
    task {
        let! _ = VerificationSupport.populated database
        let! original = storedRead database
        let record = original |> Result.defaultWith (string >> failtest) |> Option.get
        let different = CaseFixtures.reference "different"

        let wrongReference =
            { record.Snapshot with
                Reference = different
                State =
                    CaseState.map
                        (fun fields ->
                            { fields with
                                CaseReference = different
                            })
                        record.Snapshot.State
            }

        let wrongState =
            { record.Snapshot with
                State = CaseState.Voided(CaseState.fields record.Snapshot.State |> Option.get)
            }

        for bytes in
            [
                [| 0uy |]
                CaseRecord.encodeSnapshot wrongReference
                CaseRecord.encodeSnapshot
                    { record.Snapshot with
                        Revision = CaseFixtures.revision 2L
                    }
                CaseRecord.encodeSnapshot wrongState
            ] do
            do!
                VerificationSupport.sql
                    database
                    "UPDATE claimcore.cases SET snapshot=$1"
                    [| box bytes |]

            let! read = storedRead database

            Expect.equal
                read
                (Error StoreFailure.Integrity)
                "Real store read rejects corrupt bytes or metadata disagreement"

            do! VerificationSupport.finding VerificationFinding.CaseProjection database

        let noncanonical = Array.append [| byte ' ' |] record.SnapshotBytes

        do!
            VerificationSupport.sql
                database
                "UPDATE claimcore.cases SET snapshot=$1"
                [| box noncanonical |]

        let! decoded = storedRead database

        Expect.equal
            (decoded |> Result.map (Option.map _.Snapshot))
            (Ok(Some record.Snapshot))
            "The read can decode an equivalent representation"

        do! VerificationSupport.finding VerificationFinding.CaseProjection database
    }

let private roundTrips database =
    task {
        let! actor = VerificationSupport.populated database

        let! grant =
            ClaimCore.TestSupport.StoreFixtures.grant
                actor
                (ClaimCore.TestSupport.StoreFixtures.id 920000)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        ClaimCore.TestSupport.StoreFixtures.acceptedAuthority grant

        let check expectedState revision =
            task {
                let! result = storedRead database
                let record = result |> Result.defaultWith (string >> failtest) |> Option.get

                Expect.equal
                    (CaseState.token record.Snapshot.State)
                    expectedState
                    "Round-trip lifecycle"

                Expect.equal
                    (Revision.value record.Snapshot.Revision)
                    revision
                    "Round-trip revision"

                Expect.equal
                    record.SnapshotBytes
                    (CaseRecord.encodeSnapshot record.Snapshot)
                    "Projection retains the exact committed snapshot bytes"

                let! _ = VerificationSupport.verified database
                return ()
            }

        do! check "ACTIVE" 1L

        for number, revision, act, state in
            [
                11,
                1L,
                AuthoredCaseAct.AmendRegistration
                    { Inputs.registration with
                        ClaimantName = "Changed synthetic"
                    },
                "ACTIVE"
                12, 2L, AuthoredCaseAct.Void "Synthetic correction", "VOIDED"
                13, 3L, AuthoredCaseAct.Erase, "ERASED"
            ] do
            let! result =
                Service.execute
                    actor
                    (OperationInput.Case(
                        ClaimCore.TestSupport.StoreFixtures.request
                            number
                            "synthetic-a"
                            revision
                            act
                    ))
                    VerificationSupport.token

            ClaimCore.TestSupport.StoreFixtures.accepted result |> ignore
            do! check state (revision + 1L)
    }

[<Tests>]
let tests =
    testList
        "case projection collection"
        [
            testCase
                "decodes snapshots and checks every projection identity column"
                projectionBindings
            testCaseAsync
                "real reads reject corrupted and mismatched snapshots while verification compares exact bytes"
                (VerificationSupport.run corruptReads)
            testCaseAsync
                "insert update void and erase round-trip exact snapshot bytes"
                (VerificationSupport.run roundTrips)
            testCase "requires complete unique case membership" collection
            testCase "preserves live identity revision state and snapshot checks" liveState
            testCase "preserves erased identity revision and state checks" erasedState
        ]
