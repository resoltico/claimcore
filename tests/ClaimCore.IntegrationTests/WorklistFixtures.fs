module internal ClaimCore.IntegrationTests.WorklistFixtures

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let token = CancellationToken.None

type ExpectedCase =
    {
        Reference: string
        Lifecycle: Lifecycle
        Status: CaseStatus option
        Progress: CaseProgress option
        Claimant: string
        Insurer: string
        Revision: int64
    }

let private live reference lifecycle (status, progress) (claimant, insurer) revision =
    {
        Reference = reference
        Lifecycle = lifecycle
        Status = Some status
        Progress = Some progress
        Claimant = claimant
        Insurer = insurer
        Revision = revision
    }

// Hand-authored states and revisions are independent of projection/filter helpers.
let cases =
    [
        live
            "mix-04"
            Lifecycle.Active
            (CaseStatus.Opened, CaseProgress.Undecided)
            ("Rīgas %", "Ātra ¨")
            3L
        live
            "WORK-09"
            Lifecycle.Voided
            (CaseStatus.Closed, CaseProgress.Paid)
            ("Rīgas_", "Other")
            5L
        live
            "work-02"
            Lifecycle.Active
            (CaseStatus.Closed, CaseProgress.Decided)
            ("Back\\slash", "Other")
            3L
        live
            "Work-12"
            Lifecycle.Voided
            (CaseStatus.Opened, CaseProgress.Undecided)
            ("ＦＵＬＬ claimant", "Other")
            2L
        live
            "work-01"
            Lifecycle.Active
            (CaseStatus.Opened, CaseProgress.Paid)
            ("Ordinary", "Ātra ¨")
            3L
        live
            "Work-10"
            Lifecycle.Voided
            (CaseStatus.Closed, CaseProgress.Undecided)
            ("Ordinary", "Other")
            3L
        live
            "work-06"
            Lifecycle.Active
            (CaseStatus.Closed, CaseProgress.Paid)
            ("Ordinary", "Other")
            4L
        live
            "work-03"
            Lifecycle.Voided
            (CaseStatus.Opened, CaseProgress.Decided)
            ("Ordinary", "Other")
            3L
        live
            "Work-11"
            Lifecycle.Active
            (CaseStatus.Opened, CaseProgress.Decided)
            ("Ordinary", "Other")
            2L
        live
            "work-08"
            Lifecycle.Voided
            (CaseStatus.Closed, CaseProgress.Decided)
            ("Ordinary", "Other")
            4L
        live
            "work-05"
            Lifecycle.Active
            (CaseStatus.Closed, CaseProgress.Undecided)
            ("Ordinary", "Other")
            2L
        live
            "work-07"
            Lifecycle.Voided
            (CaseStatus.Opened, CaseProgress.Paid)
            ("Ordinary", "Other")
            4L
        {
            Reference = "work-13"
            Lifecycle = Lifecycle.Erased
            Status = None
            Progress = None
            Claimant = "Private erased"
            Insurer = "Private insurer"
            Revision = 4L
        }
    ]

let lifecycleSets =
    [
        []
        [ Lifecycle.Active ]
        [ Lifecycle.Voided ]
        [ Lifecycle.Erased ]
        [ Lifecycle.Active; Lifecycle.Voided ]
        [ Lifecycle.Active; Lifecycle.Erased ]
        [ Lifecycle.Voided; Lifecycle.Erased ]
        [ Lifecycle.Active; Lifecycle.Voided; Lifecycle.Erased ]
    ]

let statusSets =
    [
        []
        [ CaseStatus.Opened ]
        [ CaseStatus.Closed ]
        [ CaseStatus.Opened; CaseStatus.Closed ]
    ]

let progressSets =
    [
        []
        [ CaseProgress.Undecided ]
        [ CaseProgress.Decided ]
        [ CaseProgress.Paid ]
        [ CaseProgress.Undecided; CaseProgress.Decided ]
        [ CaseProgress.Undecided; CaseProgress.Paid ]
        [ CaseProgress.Decided; CaseProgress.Paid ]
        [ CaseProgress.Undecided; CaseProgress.Decided; CaseProgress.Paid ]
    ]

let orders =
    [ CaseListOrder.Reference; CaseListOrder.Recent; CaseListOrder.Notified ]

let limits = [ 1; 2; 3; 5; 50 ]

let private recordedAt (database: PostgresFixture.Database) operation =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT recorded_at FROM claimcore.ledger WHERE operation_id=$1"
                [| box operation |]
                token

        return DatabaseSession.instant 0 (List.exactlyOne rows)
    }

[<NoEquality; NoComparison>]
type Pair =
    {
        Postgres: ServiceContext
        Memory: ServiceContext
        Clock: MemoryStore.Store
        Database: PostgresFixture.Database
    }

let private mirrored pair request =
    task {
        let! postgres =
            Service.execute pair.Postgres (ClaimCore.TestSupport.Inputs.caseOperation request) token

        let receipt = accepted postgres
        pair.Clock.ObservedAt <- receipt.RecordedAt

        let! memory =
            Service.execute pair.Memory (ClaimCore.TestSupport.Inputs.caseOperation request) token

        let mirrored = accepted memory

        Expect.equal
            mirrored.RecordedAt
            receipt.RecordedAt
            "Memory uses the actual accepted PostgreSQL timestamp"
    }

let private seedCase pair (sequence: int ref) (expected: ExpectedCase) =
    task {
        let mutable revision = 0L

        let apply act =
            task {
                sequence.Value <- sequence.Value + 1
                do! mirrored pair (request sequence.Value expected.Reference revision act)
                revision <- revision + 1L
            }

        let registration =
            Inputs.registrationWith
                [
                    BusinessField.ClaimantName, expected.Claimant
                    BusinessField.InsurerName, expected.Insurer
                ]
                Inputs.registration

        do! apply (AuthoredCaseAct.Open registration)

        if expected.Progress <> Some CaseProgress.Undecided then
            do! apply (AuthoredCaseAct.Decide Inputs.decision)

        if
            expected.Progress = Some CaseProgress.Paid
            || expected.Lifecycle = Lifecycle.Erased
        then
            do! apply (AuthoredCaseAct.RecordPayment Inputs.payment)

        if expected.Status = Some CaseStatus.Closed then
            do! apply (AuthoredCaseAct.Bare BareAct.Close)

        if expected.Lifecycle = Lifecycle.Voided then
            do! apply (AuthoredCaseAct.Void "Synthetic worklist correction")

        if expected.Lifecycle = Lifecycle.Erased then
            do! apply (AuthoredCaseAct.Bare BareAct.Erase)
    }

let prepare () =
    task {
        let! database = PostgresFixture.create ()
        let mutable ownedClock: MemoryStore.Store option = None

        try
            let! postgres = caseWorker database.Fixture

            let clock =
                new MemoryStore.Store(database.Fixture.InstallationId, DateTimeOffset.UtcNow)

            ownedClock <- Some clock

            let memoryFixture =
                { database.Fixture with
                    Store = clock :> IStore
                    Dispose = ignore
                }

            let! first = recordedAt database (id 1)
            clock.ObservedAt <- first
            let! memory = bootstrap memoryFixture

            for operation, role in [ id 2, Role.CaseHandler; id 900000, Role.PaymentRecorder ] do
                let! instant = recordedAt database operation
                clock.ObservedAt <- instant

                let! result =
                    grant
                        memory
                        operation
                        memoryFixture.Owner
                        {
                            Role = role
                            Scope = GrantTarget.Installation
                        }
                        true

                acceptedAuthority result

            let! stewardship =
                grant
                    postgres
                    (id 902000)
                    database.Fixture.Owner
                    {
                        Role = Role.DataSteward
                        Scope = GrantTarget.Installation
                    }
                    true

            clock.ObservedAt <- (accepted stewardship).RecordedAt

            let! stewardship =
                grant
                    memory
                    (id 902000)
                    memoryFixture.Owner
                    {
                        Role = Role.DataSteward
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedAuthority stewardship

            let pair =
                {
                    Postgres = postgres
                    Memory = memory
                    Clock = clock
                    Database = database
                }

            let sequence = ref 1000

            for expected in cases do
                do! seedCase pair sequence expected
            // An older case changes last, so RECENT cannot accidentally mean creation or reference order.
            do! mirrored pair (request 2001 "mix-04" 1L (AuthoredCaseAct.Bare BareAct.Close))
            do! mirrored pair (request 2002 "mix-04" 2L (AuthoredCaseAct.Bare BareAct.Reopen))
            return pair
        with error ->
            let cleanup =
                [
                    (fun () ->
                        ownedClock |> Option.iter (fun clock -> (clock :> IDisposable).Dispose()))
                    database.Fixture.Dispose
                ]

            FixtureDisposal.afterFailure error cleanup
            return raise error
    }

let run action =
    async {
        let! pair = prepare () |> Async.AwaitTask

        try
            do! action pair |> Async.AwaitTask
        finally
            FixtureDisposal.finish
                [
                    (fun () -> (pair.Clock :> IDisposable).Dispose())
                    pair.Database.Fixture.Dispose
                ]
    }

let private selected values candidate =
    List.isEmpty values
    || candidate |> Option.exists (fun value -> List.contains value values)

let expected (filter: CaseListFilter<string>) =
    let recent = List.head cases :: (List.tail cases |> List.rev)

    let ordered =
        match filter.Order with
        | CaseListOrder.Reference ->
            cases
            |> List.sortWith (fun a b ->
                StringComparer.OrdinalIgnoreCase.Compare(a.Reference, b.Reference))
        | CaseListOrder.Recent -> recent
        | CaseListOrder.Notified ->
            cases
            |> List.sortWith (fun a b ->
                let erased =
                    compare (a.Lifecycle = Lifecycle.Erased) (b.Lifecycle = Lifecycle.Erased)

                if erased <> 0 then
                    erased
                else
                    StringComparer.OrdinalIgnoreCase.Compare(a.Reference, b.Reference))

    ordered
    |> List.filter (fun row ->
        selected filter.Lifecycles (Some row.Lifecycle)
        && selected filter.Statuses row.Status
        && selected filter.Progress row.Progress)
