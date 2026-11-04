module ClaimCore.IntegrationTests.ProjectionRebuildTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private rows (database: PostgresFixture.Database) table =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        return!
            DatabaseSession.rows
                connection
                ("SELECT row_to_json(t)::text FROM claimcore." + table + " t ORDER BY 1")
                [||]
                token
    }

let private seeded database =
    task {
        let! actor = populated database

        let! work =
            Service.execute
                actor
                (OperationInput.Work
                    {
                        OperationId = id 100
                        Target = CaseSelector.Reference "synthetic-a"
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible =
                                        WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 1)))
                                    Summary = WorkValue.Set "Await the report"
                                }
                    })
                token

        accepted work |> ignore

        let reminderRequest =
            OperationInput.Reminder
                {
                    OperationId = id 101
                    Target = CaseSelector.Reference "synthetic-a"
                    Act =
                        AuthoredReminderAct.Create
                            {
                                Action = "Request the report"
                                Due = "2027-12-31"
                            }
                }

        let! reminder = Service.execute actor reminderRequest token

        accepted reminder |> ignore

        let! resolved =
            ClaimCore.TestSupport.StoreFixtures.resolve actor { OperationId = id 99 } token

        Expect.equal resolved (Outcome.Succeeded Resolved.NotAccepted) "Control seed commits"
        let! checkpoint = verified database
        return actor, checkpoint
    }

let private repairs database =
    task {
        let! _, checkpoint = seeded database
        do! enterMaintenance database
        let! ledger = rows database "ledger"
        do! sql database "UPDATE claimcore.cases SET status='CLOSED'" [||]
        do! sql database "UPDATE claimcore.actors SET enabled=false" [||]
        do! sql database "DELETE FROM claimcore.grants" [||]
        do! sql database "DELETE FROM claimcore.operation_fences" [||]
        let! result = ProjectionRebuild.rebuild database.OwnerSource checkpoint token

        match result with
        | Error error -> failtestf "Expected reconstruction: %A" error
        | Ok report ->
            Expect.equal report.Position checkpoint "Rebuild does not append"

            Expect.isSome
                report.PreviousDivergence
                "Pre-rebuild divergence is retained in the report"

        let! current = verified database
        Expect.equal current checkpoint "All projections agree after reconstruction"

        let! returned =
            Service.get
                (core database.Fixture database.Fixture.Owner)
                {
                    Target = CaseSelector.Reference "synthetic-a"
                }
                token

        match returned with
        | Outcome.Succeeded current ->
            Expect.equal
                current.Work.Responsible
                (Some
                    {
                        ActorId = CaseFixtures.actorId (id 1)
                        DisplayName =
                            DisplayName.Named(
                                PartyName.parse Inputs.ownerProfile.DisplayName
                                |> Result.defaultWith (string >> failtest)
                            )
                        PrincipalKind = PrincipalKind.Human
                        Availability = ActorAvailability.Available
                    })
                "Actor rows and authority are restored before referenced case work"

            Expect.equal
                current.Work.FollowUp
                (Some(Inputs.date "2027-12-31"))
                "Planned date survives replacement"
        | _ -> failtest "Rebuilt work must remain readable"

        let! unchanged = rows database "ledger"
        Expect.equal unchanged ledger "Every ledger byte remains unchanged"
    }

let private checkpointRefusal database =
    task {
        let! _, checkpoint = seeded database
        do! enterMaintenance database
        do! sql database "UPDATE claimcore.cases SET status='CLOSED'" [||]
        let! before = rows database "cases"

        let wrong =
            { checkpoint with
                EntryHash = EntryHash.zero
            }

        let! refused = ProjectionRebuild.rebuild database.OwnerSource wrong token

        Expect.equal
            (refused |> Result.map ignore)
            (Error
                {
                    Sequence = Some checkpoint.Sequence
                    Finding = VerificationFinding.ExpectedPosition
                })
            "An untrusted checkpoint cannot authorize replacement"

        let! after = rows database "cases"
        Expect.equal after before "Refusal leaves even the damaged projection unchanged"
    }

let private readerSnapshot database =
    task {
        let! _, checkpoint = seeded database
        do! enterMaintenance database
        use! reader = database.OwnerSource.OpenConnectionAsync()

        use! transaction =
            reader.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead)

        let! _ =
            DatabaseSession.rows reader "SELECT sequence FROM claimcore.ledger_head" [||] token
        // The snapshot exists but has never accessed the projection table.
        do! sql database "UPDATE claimcore.cases SET status='CLOSED'" [||]
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk rebuilt "Rebuild repairs the later projection damage"

        let! oldRows =
            DatabaseSession.rows reader "SELECT status FROM claimcore.cases" [||] token

        Expect.equal oldRows.Length 1 "An older reader must not see a TRUNCATE-induced empty table"

        Expect.equal
            (DatabaseSession.value<string> 0 oldRows.Head)
            "OPENED"
            "The original committed reader snapshot is coherent"

        do! transaction.RollbackAsync token
    }

let private erasedIdentity database =
    task {
        let! actor, _ = seeded database

        let! permission =
            grant
                actor
                (id 102)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        match permission.Outcome with
        | ExecuteResult.Accepted _ -> acceptedAuthority permission
        | _ -> failtestf "Erasure grant refused: %A" permission

        let! erased =
            Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request 103 "synthetic-a" 1L (AuthoredCaseAct.Bare BareAct.Erase)
                ))
                token

        accepted erased |> ignore
        do! enterMaintenance database
        let! checkpoint = verified database

        do!
            DatabaseScenarios.exec
                database
                [|
                    "pg_dump"
                    "-U"
                    "postgres"
                    "-d"
                    "postgres"
                    "-f"
                    "/tmp/foundation-erased.sql"
                |]

        do! sql database "DROP SCHEMA claimcore CASCADE" [||]
        do! sql database "DROP EXTENSION pg_trgm" [||]

        let! absentModule =
            DatabaseSession.read database.OwnerSource token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT EXISTS(SELECT FROM pg_extension WHERE extname='pg_trgm')"
                    [||]
                    token)

        Expect.isFalse
            (DatabaseSession.value<bool> 0 absentModule.Head)
            "Restore cannot reuse an installed extension"

        do!
            DatabaseScenarios.exec
                database
                [|
                    "psql"
                    "-U"
                    "postgres"
                    "-d"
                    "postgres"
                    "-v"
                    "ON_ERROR_STOP=1"
                    "-f"
                    "/tmp/foundation-erased.sql"
                |]

        let! restoredModule =
            DatabaseSession.read database.OwnerSource token (fun connection ->
                DatabaseSession.rows
                    connection
                    "SELECT extversion FROM pg_extension WHERE extname='pg_trgm'"
                    [||]
                    token)

        Expect.equal
            (restoredModule |> List.map (DatabaseSession.value<string> 0))
            [ "1.6" ]
            "Full dump restores the required module before its dependent indexes"

        let! restored = Verification.verify database.OwnerSource (Some checkpoint) token

        Expect.equal
            restored
            (VerificationResult.Verified checkpoint)
            "A real full restore preserves accepted writes, erasure and controls"

        do! sql database "DELETE FROM claimcore.grants WHERE case_id IS NOT NULL" [||]
        do! sql database "DELETE FROM claimcore.reminders" [||]
        do! sql database "DELETE FROM claimcore.case_work" [||]
        do! sql database "DELETE FROM claimcore.cases" [||]
        let! result = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isOk result "Retained OPEN identity reconstructs erased cases"

        let! returned =
            Service.get
                actor
                {
                    Target = CaseSelector.Reference("SYNTHETIC-A")
                }
                token

        match returned with
        | Outcome.Succeeded view ->
            Expect.equal
                view.Snapshot.CaseId
                (CaseFixtures.caseId (id 10))
                "The original creation identity survives"

            Expect.equal
                (CaseReference.format view.Snapshot.Reference)
                "synthetic-a"
                "The original reference spelling survives"

            Expect.equal
                view.Snapshot.State
                CaseState.Erased
                "Rebuild recreates no erased business content"
        | _ -> failtest "Erased identity read control"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "projection reconstruction"
        [
            testCaseAsync
                "repairs case actor grant and fence projections without rewriting history"
                (run repairs)
            testCaseAsync "refuses a mismatched checkpoint without writes" (run checkpointRefusal)
            testCaseAsync
                "preserves a reader snapshot established before projection access"
                (run readerSnapshot)
            testCaseAsync
                "reconstructs erased identity from retained creation facts"
                (run erasedIdentity)
        ]
