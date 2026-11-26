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
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

module Attachments = AttachmentPostgresTests

let private draftWithLength fixture number length (payload: byte array) =
    let inputs =
        [
            InputTarget.AttachmentName, "evidence.bin"
            InputTarget.AttachmentLength, (length: int).ToString(CultureInfo.InvariantCulture)
            InputTarget.AttachmentDigest, SHA256.HashData(payload) |> Convert.ToHexStringLower
        ]
        |> List.map (fun (target, value) -> Submissions.text (Submissions.input target) value)
        |> Map.ofList

    Submissions.createRaw
        "attachment.add"
        (id fixture number)
        (OperationBinding.Attachment(Attachments.target, None))
        inputs

let private draft fixture number (payload: byte array) =
    draftWithLength fixture number payload.Length payload

/// Model a previous writer which accepted a structurally correct, byte-verified addition
/// without checking retained capacity. All ledger, compartment, payload and projection
/// records use the ordinary accepting code.
let private acceptAttachment fixture (actor: ServiceContext) number preview payload =
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
                                    match reviewed with
                                    | Ok(PreparedDecision.Ordinary(plan, _)) -> plan
                                    | other -> failtestf "Expected an attachment decision: %A" other

                                let! committed =
                                    let changes =
                                        plan.Changes
                                        |> List.map (fun change ->
                                            match change.Value with
                                            | PartValue.Attachment(AttachmentEffect.Added(true,
                                                                                          Some descriptor)) ->
                                                { change with
                                                    Value =
                                                        PartValue.Attachment(
                                                            AttachmentEffect.Added(
                                                                true,
                                                                Some
                                                                    { descriptor with
                                                                        Preview = preview
                                                                    }
                                                            )
                                                        )
                                                }
                                            | _ ->
                                                failtest "Expected an accepted attachment shape.")

                                    Engine.commit
                                        tx
                                        { plan with
                                            Changes = changes
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
                database.MasterKey
                (MaintenanceChange.Acquire(generation, handleId))
                token

        Expect.isTrue mode.State.Active "The isolated operator fixture owns maintenance"
        return MaintenanceHandle.create (generation + 1L) handleId |> Option.get
    }

let private qualifiedAtBoundary (database: PostgresFixture.Database) =
    task {
        let! trusted = checkpoint database
        let! handle = joinMaintenance database 0L

        let! rebuilt =
            ProjectionRebuild.rebuild database.OwnerSource database.MasterKey trusted handle token

        Expect.isOk rebuilt "Exact retained capacity permits projection rebuild"

        let! restored =
            RestoreAdmission.admit database.OwnerSource database.MasterKey trusted handle token

        Expect.isOk restored "Exact retained capacity permits restore admission"

        let! resumed =
            RegisterMaintenanceStore.change
                database.OwnerSource
                database.MasterKey
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
        let! verified =
            Verification.verify database.OwnerSource database.MasterKey (Some trusted) token

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

        let! rebuilt =
            ProjectionRebuild.rebuild database.OwnerSource database.MasterKey trusted handle token

        let! restored =
            RestoreAdmission.admit database.OwnerSource database.MasterKey trusted handle token

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
        let! excess = acceptAttachment fixture actor 601 None [| 100uy |]

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
        let! excess = acceptAttachment fixture actor 613 None [| 1uy |]

        do! rejectedByAll database trusted VerificationFinding.AttachmentReferences (Some excess) 2L
    }

let private inconsistentPreview (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let! trusted = qualifiedAtBoundary database
        let fixture = database.Fixture

        let! added =
            acceptAttachment fixture actor 620 (Some AttachmentPreview.Png) [| 1uy |]

        let! offered =
            Service.previewAttachment
                actor
                {
                    Target = Attachments.target
                    AttachmentId = Attachments.attachmentGuid fixture 620
                }
                token

        Expect.equal
            (match offered with
             | Outcome.Failed CoreFault.StoreIntegrityError -> true
             | _ -> false)
            true
            "An offered transfer independently rejects inconsistent media evidence"

        do! rejectedByAll database trusted VerificationFinding.AttachmentPayloads (Some added) 2L
    }

let private malformedErasedDigest (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let fixture = database.Fixture

        let! added =
            Attachments.upload actor (Attachments.add fixture 700 "evidence.bin") Attachments.bytes

        let added = accepted added
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

        do!
            rejectedByAll
                database
                trusted
                VerificationFinding.AttachmentPayloads
                (Some added.Position.Sequence)
                2L
    }

let private rootHydrationQueries (connection: Npgsql.NpgsqlConnection) =
    task {
        let! rows =
            DatabaseSession.rows
                connection
                """
                SELECT coalesce(sum(calls) FILTER(WHERE query ILIKE '%FROM claimcore.ledger WHERE operation_id=ANY($1)'),0)::bigint,
                       coalesce(sum(calls) FILTER(WHERE query ILIKE '%FROM claimcore.ledger_effects WHERE operation_sequence=ANY($1)%'),0)::bigint,
                       coalesce(sum(calls) FILTER(WHERE query ILIKE '%FROM claimcore.ledger_compartments WHERE operation_sequence=ANY($1)%'),0)::bigint
                FROM public.pg_stat_statements
                """
                [||]
                token

        let row = List.exactlyOne rows

        return
            DatabaseSession.value<int64> 0 row,
            DatabaseSession.value<int64> 1 row,
            DatabaseSession.value<int64> 2 row
    }

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected authorized metadata: %A" other

let private attachmentTimeline actor after limit =
    Service.timeline
        actor
        {
            Target = Attachments.target
            Filter = Some TimelineFilter.Attachments
            AfterCursor = after
            Limit = limit
        }
        token

let private metadataWithoutPayloadAccess (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let fixture = database.Fixture

        for number in [ 710; 711 ] do
            let payload = [| byte (number - 710) |]
            let! added = Attachments.upload actor (draft fixture number payload) payload
            accepted added |> ignore

        // This database belongs to this case. Keep column grants for metadata only.
        do!
            sql
                database
                """
                REVOKE SELECT ON claimcore.attachment_blobs FROM claimcore_runtime;
                GRANT SELECT (case_id, blob_id, operation_sequence, effect_index, length, digest)
                    ON claimcore.attachment_blobs TO claimcore_runtime;
                """
                [||]

        use! connection = database.RuntimeSource.OpenConnectionAsync(token)

        let! denied =
            task {
                try
                    let! _ =
                        DatabaseSession.rows
                            connection
                            "SELECT payload FROM claimcore.attachment_blobs"
                            [||]
                            token

                    return false
                with :? Npgsql.PostgresException as error when
                    error.SqlState = Npgsql.PostgresErrorCodes.InsufficientPrivilege ->
                    return true
            }

        Expect.isTrue denied "The runtime role is denied a direct payload SELECT"

        let! duplicate = Service.previewSubmission actor (draft fixture 712 [| 0uy |]) token

        match duplicate.Outcome with
        | Outcome.Rejected(Refusal.AttachmentDuplicate attachment) ->
            Expect.equal (AttachmentId.value attachment) (id fixture 710) "The retained attachment"
        | other -> failtestf "A preview matches retained metadata with no payload SELECT: %A" other

        let! wrongLength =
            Service.previewSubmission actor (draftWithLength fixture 714 2 [| 0uy |]) token

        match wrongLength.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other ->
            failtestf "Contradictory authored length does not imply corrupt storage: %A" other

        let! distinct = Service.previewSubmission actor (draft fixture 713 [| 2uy |]) token

        match distinct.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other -> failtestf "Expected a review when no metadata matched: %A" other

        let! listed = Attachments.list actor

        match listed with
        | Outcome.Succeeded page ->
            Expect.sequenceEqual
                (page.Items |> List.map _.AttachmentId |> List.sort)
                ([ 710; 711 ] |> List.map (Attachments.attachmentGuid fixture) |> List.sort)
                "Listing still returns both seeded attachments"
        | other -> failtestf "Listing must succeed without payload-column access: %A" other

        let! first = attachmentTimeline actor None 1
        let page = read first

        Expect.sequenceEqual
            (page.Items |> List.map _.Subject.PartId)
            [ id fixture 711 ]
            "The first page returns the newest attachment"

        let cursor =
            page.NextCursor
            |> Option.defaultWith (fun () -> failtest "The first page has a continuation")

        let! continued = attachmentTimeline actor (Some cursor) 1

        match continued with
        | Outcome.Succeeded page ->
            Expect.sequenceEqual
                (page.Items |> List.map _.Subject.PartId)
                [ id fixture 710 ]
                "The continuation returns the older attachment"
        | other ->
            failtestf "Timeline continuation must succeed without payload-column access: %A" other

        let retained = id fixture 710

        do!
            sql
                database
                "DELETE FROM claimcore.case_attachments WHERE attachment_id=$1"
                [| box retained |]

        let! withoutProjection =
            Service.previewSubmission actor (draft fixture 715 [| 0uy |]) token

        match withoutProjection.Outcome with
        | Outcome.Rejected(Refusal.AttachmentDuplicate attachment) ->
            Expect.equal
                (AttachmentId.value attachment)
                retained
                "Ledger-backed match ignores projection absence"
        | other -> failtestf "Expected duplicate without listing projection: %A" other

        // Keep the encrypted width consistent with the damaged length. The denied payload
        // column forces the preview to detect the disagreement in native metadata.
        do!
            sql
                database
                "ALTER TABLE claimcore.attachment_blobs DISABLE TRIGGER attachment_blobs_protect"
                [||]

        do!
            sql
                database
                "UPDATE claimcore.attachment_blobs SET length=2, payload=payload||decode('00','hex') WHERE blob_id=$1"
                [| box retained |]

        do!
            sql
                database
                "ALTER TABLE claimcore.attachment_blobs ENABLE TRIGGER attachment_blobs_protect"
                [||]

        let! corrupt =
            Service.previewSubmission actor (draftWithLength fixture 716 2 [| 0uy |]) token

        Expect.equal
            corrupt.Outcome
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "Corrupt retained metadata fails even if it agrees with the submitted length"
    }

let private measure sql action explanation =
    task {
        let! _ =
            DatabaseSession.execute sql "SELECT public.pg_stat_statements_reset()" [||] token

        let! result = action ()
        let! acquired = rootHydrationQueries sql
        Expect.equal acquired (1L, 1L, 1L) explanation
        return result
    }

let private metadataQueries (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let fixture = database.Fixture
        // This witness resets cluster counters and reads runtime query statistics.
        use! sql = database.SuperuserSource.OpenConnectionAsync(token)
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

        let! two =
            measure
                sql
                (fun () -> Attachments.list actor)
                "Two current attachments acquire one root/effect/compartment batch"

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

        let! first = attachmentTimeline actor None 1

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
                (fun () -> attachmentTimeline actor (Some cursor) 20)
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

let private externalPayloadStorage (database: PostgresFixture.Database) =
    task {
        let! actor = Attachments.setup database
        let payload = Array.create (64 * 1024) 0x41uy
        let! added = Attachments.upload actor (draft database.Fixture 712 payload) payload
        accepted added |> ignore

        use! connection = database.OwnerSource.OpenConnectionAsync(token)

        let! columns =
            DatabaseSession.rows
                connection
                """
                SELECT attrelid::regclass::text, attstorage::text
                FROM pg_attribute
                WHERE attname = 'payload'
                  AND attrelid IN ('claimcore.attachment_blobs'::regclass, 'claimcore.ledger_compartments'::regclass)
                ORDER BY attrelid::regclass::text
                """
                [||]
                token

        Expect.equal
            (columns
             |> List.map (fun row ->
                 DatabaseSession.value<string> 0 row, DatabaseSession.value<string> 1 row))
            [ "claimcore.attachment_blobs", "e"; "claimcore.ledger_compartments", "e" ]
            "Attachment bytes and ledger ciphertext use EXTERNAL storage"

        let! stored =
            DatabaseSession.rows
                connection
                "SELECT octet_length(payload), pg_column_compression(payload) IS NULL FROM claimcore.attachment_blobs"
                [||]
                token

        let row = List.exactlyOne stored

        Expect.equal
            (DatabaseSession.value<int> 0 row)
            (65536 + InstallationCipher.envelopeOverhead)
            "The 64 KiB upload is stored in its authenticated envelope"

        Expect.isTrue
            (DatabaseSession.value<bool> 1 row)
            "The uploaded payload is stored without compression"
    }

let private repairPayload (database: PostgresFixture.Database) =
    task {
        let! owner = Attachments.setup database
        let fixture = database.Fixture
        let blob = Attachments.attachmentGuid fixture 720

        let! uploaded =
            Attachments.upload owner (Attachments.add fixture 720 "evidence.bin") Attachments.bytes

        let acceptedUpload = accepted uploaded
        let! case = Service.get owner { Target = Attachments.target } token
        let caseId = (read case).Snapshot.CaseId
        let! trusted = checkpoint database
        let! handle = joinMaintenance database 0L

        do!
            guardedSql
                database
                "UPDATE claimcore.attachment_blobs SET payload=set_byte(payload,0,1-get_byte(payload,0)%2)"
                [||]

        let! failure =
            Verification.verify database.OwnerSource database.MasterKey (Some trusted) token

        match failure with
        | VerificationResult.Diverged finding ->
            Expect.equal
                finding.Sequence
                (Some acceptedUpload.Position.Sequence)
                "The original accepting operation is identified"

            Expect.equal
                (finding.Attachment
                 |> Option.map (fun evidence ->
                     evidence.Issue, evidence.CaseId, evidence.AttachmentId))
                (Some(AttachmentEvidenceIssue.Unreadable, Some caseId, Some blob))
                "Only authenticated identifiers identify the damaged payload"
        | _ -> failtest "The damaged ciphertext must refuse verification"

        let repair content =
            ProjectionRebuild.repairAttachment
                database.OwnerSource
                database.MasterKey
                trusted
                handle
                caseId
                blob
                content
                token

        let! wrong =
            task {
                try
                    let! _ = repair (ReadOnlyMemory [| 0uy; 0uy; 0uy |])
                    return None
                with :? OperatorFaultException as error ->
                    return Some error.Fault
            }

        Expect.equal
            wrong
            (Some OperatorFault.AttachmentRepairRefused)
            "Same length alone does not authorize repair"

        let! repaired = repair (ReadOnlyMemory Attachments.bytes)

        let repairReport =
            repaired
            |> Result.defaultWith (fun error -> failtestf "Repair refused: %A" error)

        Expect.equal
            (EffectPosition.sequence repairReport.AcceptedAt)
            acceptedUpload.Position.Sequence
            "Repair preserves accepting provenance"

        let! verifiedAgain = verified database
        Expect.equal verifiedAgain trusted.Position "Repair did not append an operation"

        let! status = InstallationStatus.read database.OwnerSource database.MasterKey token

        Expect.equal
            status.RecoveryPublicationGeneration
            1L
            "Repair advances the independent publication fence"

        use! runtime = database.RuntimeSource.OpenConnectionAsync(token)

        let! permission =
            task {
                try
                    let! _ =
                        DatabaseSession.rows
                            runtime
                            "SELECT claimcore.repair_attachment_payload(NULL,NULL,NULL,NULL,NULL,NULL,NULL)"
                            [||]
                            token

                    return None
                with :? Npgsql.PostgresException as error ->
                    return Some error.SqlState
            }

        Expect.equal
            permission
            (Some "42501")
            "The ordinary runtime role cannot invoke the owner repair"

        do! guardedSql database "DELETE FROM claimcore.attachment_blobs" [||]

        let! missing =
            Verification.verify database.OwnerSource database.MasterKey (Some trusted) token

        match missing with
        | VerificationResult.Diverged finding ->
            Expect.equal
                (finding.Attachment |> Option.map _.Issue)
                (Some AttachmentEvidenceIssue.Missing)
                "Missing bytes have their own authenticated finding"
        | _ -> failtest "The missing row must be reported"

        let directory = PrivateTestPaths.newPrivateDirectory "claimcore-attachment-repair-"

        try
            let configuration =
                typeof<Administration>
                    .Assembly.GetCustomAttributes(
                        typeof<System.Reflection.AssemblyConfigurationAttribute>,
                        false
                    )
                |> Array.exactlyOne
                |> unbox<System.Reflection.AssemblyConfigurationAttribute>
                |> _.Configuration

            let! binary =
                AdministrationProcess.publishConfiguration
                    configuration
                    (IO.Path.Combine(directory, "published"))

            let privateText = AdministrationProcess.privateText directory

            let connection =
                privateText "owner.connection" (PostgresFixture.ownerConnectionString database)

            let payload = privateText "protected-payload" "abc"

            let proof =
                privateText
                    "checkpoint.json"
                    (CheckpointRecord.encode trusted |> Text.Encoding.UTF8.GetString)

            let! restored =
                AdministrationProcess.invoke
                    binary
                    connection
                    database.DataKeyFile
                    [
                        "repair-attachment"
                        "--checkpoint"
                        proof
                        "--pause-generation"
                        string handle.Generation
                        "--pause-id"
                        handle.PauseId.ToString("D")
                        "--case-id"
                        CaseId.format caseId
                        "--blob-id"
                        AttachmentId.format blob
                        "--payload-file"
                        payload
                    ]
                    0

            Expect.equal
                (restored.GetProperty("kind").GetString())
                "ATTACHMENT_REPAIRED"
                "Published owner tool repaired the original missing payload"

            Expect.equal
                (restored.GetProperty("acceptedAt").GetProperty("sequence").GetInt64())
                (Sequence.value acceptedUpload.Position.Sequence)
                "Repair preserves the original acceptance"

            for privateValue in [ payload; "evidence.bin"; "ba7816bf" ] do
                Expect.isFalse
                    (restored.GetRawText().Contains privateValue)
                    "Protected data is absent from the owner reply"
        finally
            IO.Directory.Delete(directory, true)


        let! _ =
            RegisterMaintenanceStore.change
                database.OwnerSource
                database.MasterKey
                (MaintenanceChange.Resume handle)
                token

        let! erased = Service.executeSubmission owner (Attachments.erase fixture 721) token
        accepted erased |> ignore
        let! erasedCheckpoint = checkpoint database
        let! erasedHandle = joinMaintenance database 2L

        let! refusal =
            task {
                try
                    let! _ =
                        ProjectionRebuild.repairAttachment
                            database.OwnerSource
                            database.MasterKey
                            erasedCheckpoint
                            erasedHandle
                            caseId
                            blob
                            (ReadOnlyMemory Attachments.bytes)
                            token

                    return None
                with :? OperatorFaultException as error ->
                    return Some error.Fault
            }

        Expect.equal
            refusal
            (Some OperatorFault.AttachmentRepairRefused)
            "Exact pre-erasure backup bytes cannot resurrect legally erased content"

        let! stillErased = verified database

        Expect.equal
            stillErased
            erasedCheckpoint.Position
            "Rejected repair preserves the erased history"

    }

let private addVerificationCases fixture actor =
    task {
        for number in 800..929 do
            let submission =
                Submissions.create
                    "case.open"
                    (id fixture number)
                    (Binding.CaseNew Revision.zero)
                    (Map.ofList
                        [
                            Submissions.field BusinessField.CaseReference,
                            SubmissionValue.Text $"VERIFY-{number}"
                        ])

            let! reply = Service.executeSubmission actor submission token
            accepted reply |> ignore

    }

let private verificationBatches () =
    async {
        let! database = PostgresFixture.createWithStatementCounts () |> Async.AwaitTask

        try
            let fixture = database.Fixture
            let! actor = Attachments.setup database |> Async.AwaitTask

            do! addVerificationCases fixture actor |> Async.AwaitTask

            use! sql =
                database.SuperuserSource.OpenConnectionAsync(token)
                |> _.AsTask()
                |> Async.AwaitTask

            let! _ =
                DatabaseSession.execute sql "CREATE EXTENSION pg_stat_statements" [||] token
                |> Async.AwaitTask

            let! before = rootHydrationQueries sql |> Async.AwaitTask
            let! _ = verified database |> Async.AwaitTask
            let! after = rootHydrationQueries sql |> Async.AwaitTask
            let (a, b, c), (x, y, z) = before, after

            Expect.equal
                (x - a, y - b, z - c)
                (2L, 2L, 2L)
                "The complete ledger crosses two batches with one query per row family per batch"

            do!
                guardedSql
                    database
                    "UPDATE claimcore.ledger SET previous_hash=decode(repeat('ff',32),'hex') WHERE sequence=5"
                    [||]
                |> Async.AwaitTask

            do!
                guardedSql
                    database
                    "UPDATE claimcore.ledger_compartments SET payload=set_byte(payload,0,1-get_byte(payload,0)%2) WHERE operation_sequence=10"
                    [||]
                |> Async.AwaitTask

            let! checkedLedger =
                Verification.verify database.OwnerSource database.MasterKey None token
                |> Async.AwaitTask

            match checkedLedger with
            | VerificationResult.Diverged finding ->
                Expect.equal
                    (finding.Finding, finding.Sequence)
                    (VerificationFinding.Link, Sequence.create 5L)
                    "A later unreadable payload cannot displace the earlier link failure in the same batch"
            | _ -> failtest "The earlier broken link must be reported"
        finally
            database.Fixture.Dispose()
    }

let private publicationWork () =
    async {
        let! database = PostgresFixture.createWithStatementCounts () |> Async.AwaitTask

        try
            let! actor = Attachments.setup database |> Async.AwaitTask

            use! observer =
                database.SuperuserSource.OpenConnectionAsync(token)
                |> _.AsTask()
                |> Async.AwaitTask

            let! _ =
                DatabaseSession.execute observer "CREATE EXTENSION pg_stat_statements" [||] token
                |> Async.AwaitTask

            let measure repair generation =
                task {
                    let! trusted = checkpoint database
                    let! handle = joinMaintenance database generation

                    let! before =
                        InstallationStatus.read database.OwnerSource database.MasterKey token

                    if repair then
                        do!
                            sql
                                database
                                "UPDATE claimcore.cases SET search_text='synthetic projection corruption' WHERE case_reference='synthetic-a'"
                                [||]

                    let! _ =
                        DatabaseSession.execute
                            observer
                            "SELECT public.pg_stat_statements_reset()"
                            [||]
                            token

                    let! result =
                        ProjectionRebuild.rebuild
                            database.OwnerSource
                            database.MasterKey
                            trusted
                            handle
                            token

                    let report =
                        result
                        |> Result.defaultWith (fun finding ->
                            failtestf "Rebuild refused: %A" finding)

                    let! statements =
                        DatabaseSession.rows
                            observer
                            """SELECT
                                 coalesce(sum(calls) FILTER(WHERE query ~ '^(INSERT INTO|DELETE FROM|UPDATE) claimcore\.(actors|cases|case_parties|case_payments|case_payment_totals|case_work|reminders|notes|case_links|grants|case_attachments)($|[ (])'),0)::bigint,
                                 coalesce(sum(calls) FILTER(WHERE query LIKE 'COPY pg_temp.%'),0)::bigint
                               FROM public.pg_stat_statements"""
                            [||]
                            token

                    let row = List.exactlyOne statements

                    let work =
                        DatabaseSession.value<int64> 0 row, DatabaseSession.value<int64> 1 row

                    let! after =
                        InstallationStatus.read database.OwnerSource database.MasterKey token

                    let! confirmed = verified database

                    if repair then
                        Expect.equal
                            (report.PreviousDivergence |> Option.map _.Finding)
                            (Some VerificationFinding.CaseSearchProjection)
                            "An actual damaged row requires replacement"

                        Expect.equal
                            after.RecoveryPublicationGeneration
                            (before.RecoveryPublicationGeneration + 1L)
                            "Publication advances the fence"
                    else
                        Expect.isNone
                            report.PreviousDivergence
                            "Healthy projections require no repair"

                        Expect.equal
                            after.RecoveryPublicationGeneration
                            before.RecoveryPublicationGeneration
                            "Healthy rebuild is a fenced no-write check"

                    Expect.equal
                        confirmed
                        trusted.Position
                        "Readback agrees independently with accepted history"

                    let! _ =
                        RegisterMaintenanceStore.change
                            database.OwnerSource
                            database.MasterKey
                            (MaintenanceChange.Resume handle)
                            token

                    return work
                }

            let! small = measure true 0L |> Async.AwaitTask
            do! addVerificationCases database.Fixture actor |> Async.AwaitTask
            let! healthy = measure false 2L |> Async.AwaitTask
            let! large = measure true 4L |> Async.AwaitTask

            Expect.equal
                healthy
                (0L, 0L)
                "A healthy 131-case register has zero projection writes or COPY"

            Expect.equal
                small
                (22L, 11L)
                "One set-based delete and insert plus one staged stream per projection table"

            Expect.equal large small "Publication statement count is independent of case count"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let rebuildWork =
    testCaseAsync
        "projection publication uses fixed statement counts and a fenced healthy no-op"
        (publicationWork ())

let private orderedAttachmentPages (database: PostgresFixture.Database) =
    task {
        let! owner = Attachments.setup database
        let fixture = database.Fixture

        let! acceptedRows =
            [ 1 .. AttachmentStorage.evidencePageSize + 2 ]
            |> List.fold
                (fun previous index ->
                    task {
                        let! rows = previous
                        let payload = [| byte index |]

                        let! added =
                            Attachments.upload owner (draft fixture (740 + index) payload) payload

                        return (accepted added).Position.Sequence :: rows
                    })
                (System.Threading.Tasks.Task.FromResult [])

        let! _ = verified database
        let ordered = List.rev acceptedRows
        let first = ordered[AttachmentStorage.evidencePageSize]
        let later = List.last ordered

        do!
            guardedSql
                database
                """UPDATE claimcore.attachment_blobs
                   SET payload=set_byte(payload,0,1-get_byte(payload,0)%2)
                   WHERE operation_sequence IN ($1,$2)"""
                [| box (Sequence.value first); box (Sequence.value later) |]

        let! check = Verification.verify database.OwnerSource database.MasterKey None token

        match check with
        | VerificationResult.Diverged finding ->
            Expect.equal
                (finding.Sequence, finding.Attachment |> Option.map _.Issue)
                (Some first, Some AttachmentEvidenceIssue.Unreadable)
                "The first unreadable blob beyond one full page outranks later damage"
        | _ -> failtest "Unreadable blob must refuse verification"
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL attachment replay and metadata"
        [
            testCaseAsync
                "attachment payload and ledger ciphertext use uncompressed external storage"
                (run externalPayloadStorage)
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
                "exact owner repair authenticates bytes and keeps the accepting position"
                (run repairPayload)
            testCaseAsync
                "verified bytes reject a sealed descriptor with inconsistent media evidence"
                (run inconsistentPreview)
            testCaseAsync
                "attachment listing and timeline continuation succeed without payload-column access"
                (run metadataWithoutPayloadAccess)
            testCaseAsync
                "complete verification batches authoritative roots and keeps earliest finding precedence"
                (verificationBatches ())
            testCaseAsync
                "ordered attachment verification keeps earliest finding beyond a full page"
                (run orderedAttachmentPages)
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
