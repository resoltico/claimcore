module ClaimCore.IntegrationTests.AttachmentVerificationTests

open System
open System.Globalization
open System.Security.Cryptography
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

module Attachments = AttachmentPostgresTests

let private draft fixture number (payload: byte array) =
    let inputs =
        [
            InputTarget.AttachmentName, "evidence.bin"
            InputTarget.AttachmentLength, payload.Length.ToString(CultureInfo.InvariantCulture)
            InputTarget.AttachmentDigest, SHA256.HashData(payload) |> Convert.ToHexStringLower
        ]
        |> List.map (fun (target, value) -> Submissions.text (Submissions.input target) value)
        |> Map.ofList

    Submissions.createRaw
        "attachment.add"
        (id fixture number)
        (OperationBinding.Attachment(Attachments.target, None))
        inputs

/// Model a previous writer which accepted a structurally correct, byte-verified addition
/// without checking retained capacity. All ledger, compartment, payload and projection
/// records use the ordinary accepting code.
let private acceptWithoutQuota fixture (actor: ServiceContext) number payload =
    task {
        let submission = draft fixture number payload
        let input = Submissions.command submission
        let selected = ClaimCore.Domain.Tasks.Registry.find submission.Task |> Option.get

        let intent =
            ClaimCore.Domain.Inputs.AttachmentInputs.intent input.Inputs
            |> Result.defaultWith (fun _ -> failtest "Fixture upload intent is valid")

        let verified =
            VerifiedAttachment.verify intent (ReadOnlyMemory<byte> payload)
            |> Option.defaultWith (fun () -> failtest "Fixture upload bytes match the intent")

        let operation = CaseFixtures.operationId submission.OperationId

        let! accepted =
            fixture.Store.Write
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            task {
                                let decision =
                                    {
                                        DecisionContext.BusinessDate =
                                            Some(fixture.Calendar.DateAt tx.ObservedAt)
                                        ObservedAt = tx.ObservedAt
                                        Principal = actor.Principal
                                        Access = AccessView.empty
                                        Maintenance = false
                                        Lifetime = DecisionLifetime.Execution None
                                    }

                                let! reviewed =
                                    Engine.prepare
                                        tx
                                        tx.Head
                                        decision
                                        selected
                                        operation
                                        input.Binding
                                        input.Inputs
                                        None

                                let plan =
                                    reviewed
                                    |> Result.defaultWith (fun error ->
                                        failtestf "Fixture input cannot be reviewed: %A" error)

                                let! committed =
                                    Engine.commit
                                        tx
                                        { plan with
                                            AttachmentPayload = Some verified
                                        }

                                return
                                    match committed with
                                    | Ok accepted -> Verdict.Proceed accepted.Root.Header.Sequence
                                    | Error fault -> Verdict.Fail fault
                            }
                }

        return
            match accepted with
            | Ok(Verdict.Proceed sequence) -> sequence
            | other ->
                failtestf "The quota control did not create a complete accepted root: %A" other
    }

let private joinMaintenance (database: PostgresFixture.Database) generation =
    task {
        let handleId = Guid.NewGuid()

        let! mode =
            RegisterMaintenanceStore.change
                database.OwnerSource
                (MaintenanceChange.Acquire(generation, handleId))
                token

        Expect.isTrue mode.State.Active "The isolated operator fixture owns maintenance"
        return MaintenanceHandle.create (generation + 1L) handleId |> Option.get
    }

let private qualifiedAtBoundary (database: PostgresFixture.Database) =
    task {
        let! trusted = checkpoint database
        let! handle = joinMaintenance database 0L

        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource trusted handle token
        Expect.isOk rebuilt "Exact retained capacity permits projection rebuild"

        let! restored = RestoreAdmission.admit database.OwnerSource trusted handle token
        Expect.isOk restored "Exact retained capacity permits restore admission"

        let! resumed =
            RegisterMaintenanceStore.change
                database.OwnerSource
                (MaintenanceChange.Resume handle)
                token

        Expect.isFalse resumed.State.Active "The same fixture can introduce the negative control"
        return trusted
    }

let private rejectedByAll
    (database: PostgresFixture.Database)
    trusted
    expected
    actualSequence
    generation
    =
    task {
        let! verified = Verification.verify database.OwnerSource (Some trusted) token

        let expect finding =
            Expect.equal
                (finding.Finding, finding.Sequence)
                (expected, actualSequence)
                "The same integrity finding identifies the independent control"

        match verified with
        | VerificationResult.Diverged finding -> expect finding
        | VerificationResult.Verified _ ->
            failtest "Verification admitted invalid attachment evidence"

        let! handle = joinMaintenance database generation
        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource trusted handle token
        let! restored = RestoreAdmission.admit database.OwnerSource trusted handle token

        for result in [ Result.map ignore rebuilt; Result.map ignore restored ] do
            match result with
            | Error finding -> expect finding
            | Ok() -> failtest "A maintenance path admitted invalid attachment evidence"
    }

let private slots (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let fixture = database.Fixture

        for number in 0..99 do
            let bytes = [| byte number |]
            let! added = Attachments.upload actor (draft fixture (500 + number) bytes) bytes
            accepted added |> ignore

        let! withdrawn =
            Service.executeSubmission
                actor
                (Attachments.withdraw fixture 600 (Attachments.attachmentGuid fixture 500))
                token

        accepted withdrawn |> ignore
        let! trusted = qualifiedAtBoundary database
        let! excess = acceptWithoutQuota fixture actor 601 [| 100uy |]

        do! rejectedByAll database trusted VerificationFinding.AttachmentReferences (Some excess) 2L
    }

let private bytes (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let fixture = database.Fixture
        let full = Array.zeroCreate<byte> 26_214_400

        for number in 0..9 do
            full[0] <- byte number
            let! added = Attachments.upload actor (draft fixture (600 + number) full) full
            accepted added |> ignore

        let tail = Array.zeroCreate<byte> 6_291_456
        let! added = Attachments.upload actor (draft fixture 611 tail) tail
        accepted added |> ignore

        let! withdrawn =
            Service.executeSubmission
                actor
                (Attachments.withdraw fixture 612 (Attachments.attachmentGuid fixture 600))
                token

        accepted withdrawn |> ignore
        let! trusted = qualifiedAtBoundary database
        let! excess = acceptWithoutQuota fixture actor 613 [| 1uy |]

        do! rejectedByAll database trusted VerificationFinding.AttachmentReferences (Some excess) 2L
    }

let private malformedErasedDigest (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let fixture = database.Fixture

        let! added =
            Attachments.upload actor (Attachments.add fixture 700 "evidence.bin") Attachments.bytes

        accepted added |> ignore
        let! erased = Service.executeSubmission actor (Attachments.erase fixture 701) token
        accepted erased |> ignore
        let! trusted = qualifiedAtBoundary database

        // A trusted corruption fixture disables the structural pairing constraint, then
        // separately demonstrates the immutable-row guard and the native verifier.
        do!
            sql
                database
                "ALTER TABLE claimcore.attachment_blobs DROP CONSTRAINT attachment_blobs_check"
                [||]

        do!
            guardedSql
                database
                "UPDATE claimcore.attachment_blobs SET digest=decode('00','hex')"
                [||]

        do! rejectedByAll database trusted VerificationFinding.AttachmentPayloads None 2L
    }

let private rootHydrationQueries (connection: Npgsql.NpgsqlConnection) =
    task {
        let! rows =
            DatabaseSession.rows
                connection
                """
                SELECT coalesce(sum(calls) FILTER(WHERE query ILIKE '%FROM claimcore.ledger WHERE operation_id=ANY($1)'),0)::bigint,
                       coalesce(sum(calls) FILTER(WHERE query ILIKE '%FROM claimcore.ledger_effects WHERE operation_sequence=ANY($1)%'),0)::bigint,
                       coalesce(sum(calls) FILTER(WHERE query ILIKE '%FROM claimcore.ledger_compartments WHERE operation_sequence=ANY($1)%'),0)::bigint,
                       coalesce(sum(calls) FILTER(WHERE query ILIKE '%FROM claimcore.attachment_blobs%'),0)::bigint
                FROM public.pg_stat_statements
                """
                [||]
                token

        let row = List.exactlyOne rows

        return
            DatabaseSession.value<int64> 0 row,
            DatabaseSession.value<int64> 1 row,
            DatabaseSession.value<int64> 2 row,
            DatabaseSession.value<int64> 3 row
    }

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected authorized metadata: %A" other

let private payloadQueries connection =
    task {
        let! rows =
            DatabaseSession.rows
                connection
                """
                SELECT coalesce(sum(calls), 0)::bigint FROM public.pg_stat_statements
                WHERE query ILIKE '%SELECT payload FROM claimcore.attachment_blobs%'
                """
                [||]
                token

        return DatabaseSession.value<int64> 0 (List.exactlyOne rows)
    }

let private measure sql action explanation =
    task {
        let! _ =
            DatabaseSession.execute sql "SELECT public.pg_stat_statements_reset()" [||] token

        let! result = action ()
        let! payloadReads = payloadQueries sql
        Expect.equal payloadReads 0L "Metadata reads issue zero payload SELECTs"
        let! acquired = rootHydrationQueries sql
        Expect.equal acquired (1L, 1L, 1L, 0L) explanation
        return result
    }

let private metadataQueries (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let fixture = database.Fixture
        use! sql = database.OwnerSource.OpenConnectionAsync(token)
        let! _ = DatabaseSession.execute sql "CREATE EXTENSION pg_stat_statements" [||] token

        let add number =
            let id = 710 + number
            let payload = [| byte number |]

            task {
                let! added = Attachments.upload actor (draft fixture id payload) payload
                accepted added |> ignore
            }

        for number in 0..1 do
            do! add number

        let! _ =
            DatabaseSession.execute sql "SELECT public.pg_stat_statements_reset()" [||] token

        let! content = Attachments.download actor (Attachments.attachmentGuid fixture 710)
        read content |> ignore
        let! payloadReads = payloadQueries sql

        Expect.equal
            payloadReads
            1L
            "The positive control reaches the payload SELECT, independently of its result"

        let! two =
            measure
                sql
                (fun () -> Attachments.list actor)
                "Two current attachments acquire one root/effect/compartment batch without payload storage"

        Expect.hasLength (read two).Items 2 "The smaller list actually hydrates both attachments"

        for number in 2..11 do
            do! add number

        let! twelve =
            measure
                sql
                (fun () -> Attachments.list actor)
                "Larger lists still acquire one metadata batch"

        Expect.hasLength
            (read twelve).Items
            12
            "The larger list cannot avoid reading the added metadata"

        let history after limit =
            Service.timeline
                actor
                {
                    Target = Attachments.target
                    Filter = Some TimelineFilter.Attachments
                    AfterCursor = after
                    Limit = limit
                }
                token

        let! first = history None 1

        let cursor =
            (read first).NextCursor
            |> Option.defaultWith (fun () -> failtest "Initial page needs a continuation")

        let deactivated = [ 711; 713; 715 ]

        for offset, number in List.indexed deactivated do
            let! withdrawn =
                Service.executeSubmission
                    actor
                    (Attachments.withdraw
                        fixture
                        (740 + offset)
                        (Attachments.attachmentGuid fixture number))
                    token

            accepted withdrawn |> ignore

        let! continued =
            measure
                sql
                (fun () -> history (Some cursor) 20)
                "Latest-state redaction for multiple historical roots uses one metadata batch"

        let page = (read continued).Items

        Expect.equal
            (page |> List.map (fun entry -> entry.Subject.PartId))
            ([ 720..-1..711 ] |> List.map (id fixture))
            "The bounded continuation still covers the ten newest older additions"

        let hidden =
            page
            |> List.choose (fun entry ->
                if entry.Changes = TimelineChanges.Withheld then
                    Some entry.Subject.PartId
                else
                    None)
            |> Set.ofList

        Expect.equal
            hidden
            (deactivated |> List.map (id fixture) |> Set.ofList)
            "Only the withdrawn attachments are withheld on the old page"
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL attachment replay and metadata"
        [
            testCaseAsync
                "retained slot replay refuses a complete 101st blob after withdrawal"
                (run slots)
            testCaseAsync
                "retained byte replay refuses a complete byte over 256 MiB after withdrawal"
                (run bytes)
            testCaseAsync
                "erased-row verification detects a malformed present digest"
                (run malformedErasedDigest)
            testCaseAsync
                "metadata hydration batches lists and a historical continuation"
                (async {
                    let! database = PostgresFixture.createWithStatementCounts () |> Async.AwaitTask

                    try
                        do! metadataQueries database |> Async.AwaitTask
                    finally
                        database.Fixture.Dispose()
                })
        ]
