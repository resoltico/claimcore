module ClaimCore.IntegrationTests.CaseProjectionTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application
open ClaimCore.RecordFormat

let private installation = Guid.Parse "80000000-0000-4000-8000-000000000001"

let private snapshot (reference: string) : CaseSnapshot =
    let reference = CaseFixtures.reference reference

    {
        CaseId = CaseFixtures.caseId (System.Guid.Parse "20000000-0000-4000-8000-000000000001")
        Reference = reference
        Revision = CaseFixtures.revision 1L
        State =
            CaseState.Active(
                {
                    CaseReference = reference
                    Handling = Handling.Open
                    Registration = CaseFixtures.registration DomainInputs.registration
                    Finance = FinancialPosition.undecided
                }
            )
    }

let private stored (snapshot: CaseSnapshot) : StoredCase =
    {
        CaseId = snapshot.CaseId
        Snapshot = snapshot
        Index = CaseIndex.create DateTimeOffset.UnixEpoch snapshot None None None []
        ReportDates = CaseReportDates.record (CalendarDate.ofDate DateOnly.MinValue) None None
        SnapshotBytes = CaseRecord.encodeSnapshot snapshot
    }

let private history (snapshot: CaseSnapshot) = stored snapshot

let private mismatch =
    Error
        {
            Sequence = None
            Finding = VerificationFinding.CaseProjection
            Attachment = None
        }

let private compare histories rows =
    ProjectionVerification.rows
        VerificationFinding.CaseProjection
        (fun (row: StoredCase) -> row.CaseId)
        histories
        rows

let private collection () =
    let a = stored (snapshot "synthetic-a")

    let b =
        stored
            { snapshot "synthetic-b" with
                CaseId = CaseFixtures.caseId (Guid.Parse "20000000-0000-4000-8000-000000000002")
            }

    let histories =
        Map.ofList [ a.CaseId, history a.Snapshot; b.CaseId, history b.Snapshot ]

    Expect.equal (compare Map.empty []) (Ok()) "Empty coverage"

    for rows in [ [ a; b ]; [ b; a ] ] do
        Expect.equal (compare histories rows) (Ok()) "Exact unordered coverage"

    for rows in [ [ a; a ]; [ a ]; [ a; b; a ]; [ a; stored (snapshot "unknown") ] ] do
        Expect.equal (compare histories rows) mismatch "Exact collection finding"

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
                                        (ClaimCore.TestSupport.DomainInputs.registrationStateWith
                                            [
                                                BusinessField.ClaimedAmount,
                                                Admitted.Amount(
                                                    Money.amount (
                                                        CaseFixtures.money "2000.00" "EUR"
                                                    )
                                                )
                                                BusinessField.ClaimedCurrency,
                                                Admitted.Currency(
                                                    Money.currencyValue (
                                                        CaseFixtures.money "2000.00" "EUR"
                                                    )
                                                )
                                            ]
                                            (fields.Registration))
                                }
                            )
                    }
            }
        ] do
        Expect.equal (compare histories [ changed ]) mismatch "Live mismatch"

let private erasedState () =
    Expect.isNone (CaseId.create Guid.Empty) "Erased cases also require admitted identity"
    let live = snapshot "synthetic-erased"

    let erased =
        { live with
            Revision = CaseFixtures.revision 2L
            State = CaseState.Erased
        }

    let row = stored erased

    let accepted = stored erased

    let check (expected: StoredCase) (row: StoredCase) =
        compare (Map.ofList [ row.CaseId, expected ]) [ row ]

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


let private projectionFields: CaseFields =
    {
        CaseReference = CaseFixtures.reference "synthetic-layout"
        Handling =
            Handling.Closed
                {
                    Outcome = ClosureOutcome.Settled
                    Note = None
                }
        Registration =
            CaseFixtures.registration (
                ClaimCore.TestSupport.DomainInputs.registrationWith
                    [
                        BusinessField.PolicyNumber, ("Synthetic claimant")
                        BusinessField.InsurerClaimReference, ("Synthetic insurer")
                        BusinessField.ClaimedAmount, ("1234.5678")
                    ]
                    (DomainInputs.registration)
            )
        Finance =
            ClaimCore.TestSupport.CaseFixtures.finance
                (Some(
                    ClaimCore.TestSupport.DomainInputs.decisionStateFrom
                        [
                            BusinessField.PaymentDecisionDate,
                            Admitted.Date(CaseFixtures.date "2026-08-04")
                            BusinessField.PayableAmount,
                            Admitted.Amount(Money.amount (CaseFixtures.money "987.6543" "USD"))
                            BusinessField.PayableCurrency,
                            Admitted.Currency(
                                Money.currencyValue (CaseFixtures.money "987.6543" "USD")
                            )
                        ]
                ))
                [
                    CaseFixtures.paymentEntry
                        (System.Guid.Parse "20000000-0000-7000-8000-000000000003")
                        (DomainInputs.paymentFrom
                            [
                                BusinessField.PaymentDate, "2026-08-05"
                                BusinessField.PaidAmount, "987.6543"
                                BusinessField.PaidCurrency, "USD"
                            ])
                ]
    }


let private namedRow (columns: Map<string, obj>) =
    use table = new System.Data.DataTable()

    for name, _ in Map.toList columns do
        table.Columns.Add(name, typeof<obj>) |> ignore

    table.Rows.Add(columns |> Map.toArray |> Array.map snd) |> ignore
    use reader = table.CreateDataReader()
    let read = DatabaseSession.namedRow reader
    Expect.isTrue (reader.Read()) "Independent projection columns contain a row"
    read ()

let private projectionBindings () =
    let snapshot =
        {
            CaseId = CaseFixtures.caseId (System.Guid.Parse "20000000-0000-4000-8000-000000000001")
            Reference = projectionFields.CaseReference
            Revision = CaseFixtures.revision 4L
            State = CaseState.Voided projectionFields
        }

    let expected = stored snapshot

    let row =
        Map.ofList
            [
                "case_id", box (CaseId.value expected.CaseId)
                "reserved_reference", box "synthetic-layout"
                "case_reference", box "synthetic-layout"
                "revision", box 4L
                "state", box "VOIDED"
                "snapshot", box expected.SnapshotBytes
                "status", box "CLOSED"
                "progress", box "PAID"
                "changed_at", box "1970-01-01T00:00:00.000000Z AD"
                "search_text", box "synthetic claimant\u001fsynthetic insurer\u001fsynthetic-layout"
                "work_search_text", box DBNull.Value
                "received_on", box "2026-08-03 AD"
                "responsible_actor", box DBNull.Value
                "follow_up", box DBNull.Value
                "deadline_due", box DBNull.Value
                "claimed_amount", box "1234.5678"
                "claimed_currency", box "EUR"
                "payable_amount", box "987.6543"
                "payable_currency", box "USD"
                "payment_review", box false
                "outcome", box "SETTLED"
                "registered_on", box "0001-01-01 AD"
                "closed_on", box DBNull.Value
                "last_activity_on", box "0001-01-01 AD"
            ]

    let row = row |> Map.map (fun _ value -> nonNull value)

    Expect.equal
        (Projections.caseRow installation (namedRow row))
        expected
        "Snapshot bytes round-trip with their independent metadata"

    for name, value in
        [
            "case_reference", box "different"
            "revision", box 5L
            "state", box "ACTIVE"
            "case_id", box (Guid.NewGuid())
            "snapshot", box [| 0uy |]
            "status", box "UNKNOWN"
            "progress", box "UNKNOWN"
            "payment_review", box "TRUE"
            "outcome", box "UNKNOWN"
            "changed_at", box "invalid"
            "search_text", box 44
            "work_search_text", box 44
            "responsible_actor", box Guid.Empty
            "follow_up", box "invalid"
            "deadline_due", box "invalid"
        ] do
        Expect.throwsT<RecordCorruptException>
            (fun () ->
                Projections.caseRow installation (namedRow (Map.add name (nonNull value) row))
                |> ignore)
            "Every identity and snapshot mismatch is an integrity fault"

    let histories = Map.ofList [ expected.CaseId, history snapshot ]

    let noncanonical = Array.append [| byte ' ' |] expected.SnapshotBytes
    Expect.isOk (CaseRecord.decodeSnapshot noncanonical) "Whitespace retains snapshot meaning"

    Expect.equal
        (compare
            histories
            [
                { expected with
                    SnapshotBytes = noncanonical
                }
            ])
        mismatch
        "Verification compares stored bytes, not only decoded values"

let private storedRead (database: PostgresFixture.Database) =
    let id =
        CaseFixtures.caseId (ClaimCore.TestSupport.StoreFixtures.id database.Fixture 10)

    database.Fixture.Store.Read
        {
            Work = (fun tx -> FamilyReads.findCase tx id)
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
                (ClaimCore.TestSupport.StoreFixtures.id database.Fixture 920000)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        ClaimCore.TestSupport.StoreFixtures.acceptedPeople grant

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

        for number, revision, taskName, values, state in
            [
                11,
                1L,
                "case.amendRegistration",
                Submissions.updates (
                    RegistrationUpdateInput.values (
                        DomainInputs.registrationUpdate (
                            DomainInputs.registrationWith
                                [ BusinessField.PolicyNumber, "Changed synthetic" ]
                                DomainInputs.registration
                        )
                    )
                ),
                "ACTIVE"
                12,
                2L,
                "case.void",
                Map.ofList
                    [
                        Submissions.input InputTarget.Reason,
                        SubmissionValue.Text "Synthetic correction"
                    ],
                "VOIDED"
                13, 3L, "case.erase", Map.empty, "ERASED"
            ] do
            let! result =
                Service.executeSubmission
                    actor
                    (StoreFixtures.request
                        database.Fixture
                        number
                        "synthetic-a"
                        revision
                        taskName
                        values)
                    VerificationSupport.token

            StoreFixtures.accepted result |> ignore

            do! check state (revision + 1L)
    }

let private referenceOwnership database =
    task {
        let! actor = VerificationSupport.populated database

        let run number reference revision taskName inputs =
            Service.executeSubmission
                actor
                (StoreFixtures.request database.Fixture number reference revision taskName inputs)
                VerificationSupport.token

        let get reference =
            task {
                let! result =
                    Service.get
                        actor
                        {
                            Target = CaseSelector.Reference reference
                        }
                        VerificationSupport.token

                match result with
                | Outcome.Succeeded value -> return value
                | other ->
                    return failtestf "Expected a case found by its reserved reference: %A" other
            }

        let change number binding revision reference =
            run
                number
                binding
                revision
                "case.changeReference"
                (Map.ofList
                    [
                        Submissions.input InputTarget.NewCaseReference,
                        SubmissionValue.Text reference
                    ])

        let search requester text =
            task {
                let! result =
                    Service.list
                        requester
                        {
                            Page = { AfterCursor = None; Limit = 1 }
                            Filter =
                                CaseListFilter.create
                                    []
                                    [ CaseStatus.Closed ]
                                    []
                                    (Some text)
                                    CaseListOrder.Reference
                        }
                        VerificationSupport.token

                match result with
                | Outcome.Succeeded page ->
                    Expect.isEmpty page.Items "The selected filter excludes the exact result"
                    return page.ExactReference
                | other -> return failtestf "Expected an authorized search: %A" other
            }

        let exact caseId requester selector current matched =
            task {
                let! result = search requester selector

                Expect.equal
                    (result
                     |> Option.map (fun item ->
                         item.CaseId,
                         CaseReference.format item.CaseReference,
                         Option.map CaseReference.format item.MatchedReference))
                    (Some(caseId, current, matched))
                    "Exact search carries the retained selector and current reference independently"
            }

        let! introduced = change 50 "synthetic-a" 1L "REF-ALIAS"
        StoreFixtures.accepted introduced |> ignore
        let! current = get "REF-ALIAS"
        let! matched = get "synthetic-a"

        Expect.equal
            matched.Snapshot
            current.Snapshot
            "Earlier reference resolves to the same current case"

        Expect.equal
            (CaseReference.format matched.Snapshot.Reference)
            "REF-ALIAS"
            "Reads use the current reference"

        Expect.equal
            (matched.MatchedReference |> Option.map CaseReference.format)
            (Some "synthetic-a")
            "Reads report the earlier matched reference"

        let caseId = current.Snapshot.CaseId
        do! exact caseId actor "synthetic-a" "REF-ALIAS" (Some "synthetic-a")
        do! exact caseId actor "REF-ALIAS" "REF-ALIAS" None

        let scopedPrincipal =
            PrincipalKey.human "https://issuer.example/" "reference-discovery-reader"
            |> Result.defaultWith (string >> failtest)

        let! registration =
            StoreFixtures.personSubmission
                actor.Store
                (StoreFixtures.id database.Fixture 920001)
                scopedPrincipal
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! registered =
            Service.executeSubmission actor registration VerificationSupport.token

        StoreFixtures.acceptedPeople registered

        let! decoy = run 920002 "REF-DECOY" 0L "case.open" Map.empty
        let decoyId = (StoreFixtures.accepted decoy |> StoreFixtures.caseReceipt).CaseId

        let grant number id active =
            StoreFixtures.grant
                actor
                (StoreFixtures.id database.Fixture number)
                scopedPrincipal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseId id
                }
                active

        let! decoyGrant = grant 920003 decoyId true
        StoreFixtures.acceptedPeople decoyGrant
        let! recordedGrant = grant 920004 caseId true
        StoreFixtures.acceptedPeople recordedGrant
        let scoped = StoreFixtures.core database.Fixture scopedPrincipal
        do! exact caseId scoped "synthetic-a" "REF-ALIAS" (Some "synthetic-a")

        let! revoked = grant 920005 caseId false
        StoreFixtures.acceptedPeople revoked
        let! hidden = search scoped "synthetic-a"
        Expect.isNone hidden "Revocation hides both current and historical identity in search"

        let! unavailable =
            Service.get
                scoped
                {
                    Target = CaseSelector.Reference "synthetic-a"
                }
                VerificationSupport.token

        match unavailable with
        | Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
        | result -> failtestf "Opening the earlier selector rechecks access: %A" result

        for index, reference in [ 51, "synthetic-a"; 52, "SYNTHETIC-A" ] do
            let! duplicate =
                run
                    index
                    reference
                    0L
                    "case.open"
                    (Submissions.fields (RegistrationInput.values DomainInputs.registration))

            Expect.equal
                duplicate.Outcome
                (ExecuteResult.Rejected(Refusal.Case CaseRefusal.ReferenceUnavailable))
                "Inherited references stay reserved"

        let! returned = change 53 "REF-ALIAS" 2L "synthetic-a"
        StoreFixtures.accepted returned |> ignore
        let! alias = get "REF-ALIAS"

        Expect.equal
            (CaseReference.format alias.Snapshot.Reference)
            "synthetic-a"
            "A later return leaves the intervening reference reserved"

        let! respelled = change 54 "REF-ALIAS" 3L "SYNTHETIC-A"
        StoreFixtures.accepted respelled |> ignore
        let! matched = get "synthetic-a"

        Expect.equal
            (matched.MatchedReference |> Option.map CaseReference.format)
            (Some "synthetic-a")
            "Exact earlier casing is a recorded spelling"

        Expect.equal
            (CaseReference.format matched.Snapshot.Reference)
            "SYNTHETIC-A"
            "Current casing is preserved"

        do! exact caseId actor "synthetic-a" "SYNTHETIC-A" (Some "synthetic-a")
        do! exact caseId actor "Synthetic-A" "SYNTHETIC-A" None

        let! steward =
            StoreFixtures.grant
                actor
                (StoreFixtures.id database.Fixture 920000)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        StoreFixtures.acceptedPeople steward

        let! voided =
            run
                55
                "REF-ALIAS"
                4L
                "case.void"
                (Map.ofList
                    [
                        Submissions.input InputTarget.Reason,
                        SubmissionValue.Text "Entered in error"
                    ])

        StoreFixtures.accepted voided |> ignore
        let! erased = run 56 "REF-ALIAS" 5L "case.erase" Map.empty
        StoreFixtures.accepted erased |> ignore

        do! exact caseId actor "REF-ALIAS" "SYNTHETIC-A" (Some "REF-ALIAS")

        for index, reference in [ 57, "REF-ALIAS"; 58, "synthetic-a" ] do
            let! read = get reference

            Expect.equal
                read.Snapshot.State
                CaseState.Erased
                "Either reserved key resolves the erased case"

            let! duplicate =
                run
                    index
                    reference
                    0L
                    "case.open"
                    (Submissions.fields (RegistrationInput.values DomainInputs.registration))

            Expect.equal
                duplicate.Outcome
                (ExecuteResult.Rejected(Refusal.Case CaseRefusal.ReferenceUnavailable))
                "Erasure does not release a reserved key"

        let! _ = VerificationSupport.verified database
        return ()
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
            testCaseAsync
                "reference changes preserve alias ownership through erasure"
                (VerificationSupport.run referenceOwnership)
            testCase "requires complete unique case membership" collection
            testCase "preserves live identity revision state and snapshot checks" liveState
            testCase "preserves erased identity revision and state checks" erasedState
        ]
