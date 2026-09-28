module ClaimCore.IntegrationTests.RestoreReportOwnerRosterTests

open System
open System.IO
open System.Text.Json
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures

let private noPublication =
    testCase "[CC-BACKUP-001] source checkout cannot self-authorize a restore report" (fun _ ->
        Expect.isNone
            (DatabaseRestorePublication.current ())
            "Only independently signed publication may anchor a real restore recheck."

        Expect.isFalse
            typeof<TrustedRestorePublication>.IsPublic
            "The injected publication fixture is not an ordinary consumer API.")

let private ownerW1WithoutPublication =
    testCase
        "[CC-BACKUP-001] owner W1 commands refuse absent publication before authority access"
        (fun _ ->
            let temporary = Path.GetTempPath()

            let physical =
                if
                    OperatingSystem.IsMacOS()
                    && temporary.StartsWith("/var/", StringComparison.Ordinal)
                then
                    "/private" + temporary
                else
                    temporary

            let root =
                Path.Combine(physical, "claimcore-w1-refusal-" + Guid.NewGuid().ToString("N"))

            let mode =
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

            Directory.CreateDirectory(root, mode) |> ignore
            let canonical = Path.Combine(root, "candidate.json")
            let signature = Path.Combine(root, "candidate.sig")

            try
                match
                    PrivateFileService.writeNew 16 canonical (Text.Encoding.ASCII.GetBytes("{}\n"))
                with
                | Ok() -> ()
                | Error _ -> failtest "Private W1 candidate could not be created"

                match PrivateFileService.writeNew 64 signature (Array.zeroCreate 64) with
                | Ok() -> ()
                | Error _ -> failtest "Private W1 signature could not be created"

                for command, stage in
                    [ "prepare-writer-handoff", "PREPARE"; "settle-writer-handoff", "COMMIT" ] do
                    match DatabaseArguments.parse [ command; canonical; signature ] with
                    | Ok(DatabaseCommand.PrepareWriterHandoff _) when stage = "PREPARE" -> ()
                    | Ok(DatabaseCommand.SettleWriterHandoff _) when stage = "COMMIT" -> ()
                    | _ -> failtest "Owner W1 command grammar diverged"

                    match DatabaseWriterHandoffExecution.run "" canonical signature stage with
                    | Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed) ->
                        ()
                    | _ -> failtest "No publication root must refuse before W1 authority access"
            finally
                Directory.Delete(root, true))

let private fencedTailGrammar =
    testCase
        "[CC-BACKUP-001] owner fenced-tail and activation grammar binds seven private files"
        (fun _ ->
            let files =
                [ "report"; "report.sig"; "index"; "fence"; "fence.sig"; "tail"; "tail.sig" ]

            let nonce = String('a', 64)

            match DatabaseArguments.parse (("verify-fenced-tail" :: files) @ [ nonce ]) with
            | Ok(DatabaseCommand.VerifyFencedTail(paths, actual)) ->
                Expect.equal actual nonce "Read-only recheck preserves exact caller nonce"

                Expect.equal
                    paths.SupplementSignature
                    "tail.sig"
                    "Tail signature is a distinct input"

                Expect.equal
                    (DatabaseArguments.commandToken (
                        DatabaseCommand.VerifyFencedTail(paths, actual)
                    ))
                    "VERIFY_FENCED_TAIL"
                    "Read-only recheck has an exact diagnostic command token"

                use output = new MemoryStream()
                use errors = new MemoryStream()

                Expect.equal
                    (DatabaseFencedTailOwnerExecution.verify "" paths nonce output errors)
                    3
                    "Generic checkout refuses before owner authority access"

                use refusal = JsonDocument.Parse(errors.ToArray())

                Expect.equal
                    (refusal.RootElement.GetProperty("reason").GetString())
                    "TRUST_ANCHOR_UNAVAILABLE"
                    "Absent source-pinned publication root has a bounded refusal"

                Expect.isFalse
                    (refusal.RootElement.GetProperty("realDataReady").GetBoolean())
                    "No independent host proof can claim real-data readiness"
            | _ -> failtest "Fenced-tail owner grammar diverged"

            match DatabaseArguments.parse ("activate-writer-handoff" :: files) with
            | Ok(DatabaseCommand.ActivateWriterHandoff paths) ->
                Expect.equal paths.Fence "fence" "Activation binds exact signed fence"

                Expect.equal
                    (DatabaseArguments.commandToken (DatabaseCommand.ActivateWriterHandoff paths))
                    "ACTIVATE_WRITER_HANDOFF"
                    "Activation has an exact diagnostic command token"

                match DatabaseFencedTailOwnerExecution.activate "" paths with
                | Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed) -> ()
                | _ -> failtest "Absent reviewed root must refuse W2 before authority access"
            | _ -> failtest "Writer activation owner grammar diverged"

            match DatabaseArguments.parse (("verify-fenced-tail" :: files) @ [ "short" ]) with
            | Error DatabaseInputProblem.UnsupportedInvocation -> ()
            | _ -> failtest "Fenced-tail nonce must use exact lowercase digest grammar")

let private approverShape =
    testCase "[CC-BACKUP-001] restore report refuses unregistered owner key claims" (fun _ ->
        let first = Guid.NewGuid()
        let second = Guid.NewGuid()
        let approvalOne = Guid.NewGuid()
        let approvalTwo = Guid.NewGuid()

        let encoded =
            JsonSerializer.Serialize(
                {|
                    authorizedApprovers =
                        [|
                            {|
                                actorId = first
                                approvalEventId = approvalOne
                                active = true
                                role = "owner"
                                grantRevision = 1L
                            |}
                            {|
                                actorId = second
                                approvalEventId = approvalTwo
                                active = true
                                role = "owner"
                                grantRevision = 2L
                            |}
                        |]
                |}
            )

        use document = JsonDocument.Parse(encoded)
        let accepted = DatabaseRestoreOwnerClaims.parse document.RootElement 2L
        Expect.equal accepted.Length 2 "Two exact human owner grants are represented."

        let fabricated =
            encoded.Replace("approvalEventId", "signingKeyId", StringComparison.Ordinal)

        use unregistered = JsonDocument.Parse(fabricated)

        Expect.throws
            (fun () -> DatabaseRestoreOwnerClaims.parse unregistered.RootElement 2L |> ignore)
            "A caller-supplied actor signing key cannot replace witnessed approval identity.")

let private witnessedGrant connection transaction actorId : RestoreOwnerApprover =
    use command =
        new NpgsqlCommand(
            "SELECT g.changed_revision,e.event_id FROM claimcore.actor_grants g "
            + "JOIN claimcore.actor_authority_events e ON e.revision=g.changed_revision "
            + "WHERE g.actor_id=@actor AND g.active AND g.role_name='OWNER' "
            + "AND g.scope_kind='INSTALLATION'",
            connection,
            transaction
        )

    command.Parameters.AddWithValue("actor", actorId) |> ignore
    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic owner lacks a witnessed installation grant."

    let item =
        {
            ActorId = actorId
            GrantRevision = reader.GetInt64(0)
            ApprovalEventId = reader.GetGuid(1)
        }

    Expect.isFalse (reader.Read()) "One exact owner grant is expected."
    item

let private setup owner app witness =
    let first = human "restore-report-owner-one"
    let second = human "restore-report-owner-two"
    provision owner witness first |> applied
    use source = RuntimeDataSource.create app
    let grants = new ActorGrantStore(source)
    let registry = new ActorGrantRegistry(source, witness)
    registry.RegisterActor(first, second) |> await |> applied

    registry.SetGrant(
        first,
        actorId grants second,
        {
            Role = Role.Owner
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

    actorId grants first, actorId grants second

let private assertRefusals
    connection
    transaction
    (first: RestoreOwnerApprover)
    (second: RestoreOwnerApprover)
    =
    let refusals =
        [
            [ first ]
            [
                { first with
                    GrantRevision = first.GrantRevision + 1L
                }
                second
            ]
            [
                first
                { second with
                    ApprovalEventId = Guid.NewGuid()
                }
            ]
            [ first; { second with ActorId = Guid.NewGuid() } ]
        ]

    for claimed in refusals do
        Expect.throws
            (fun () -> DatabaseRestoreOwnerRoster.verify connection transaction claimed)
            "Missing, stale, or fabricated owner evidence must fail closed."

let private binding =
    testCase
        "[CC-BACKUP-001] restore report owner roster binds current witnessed human grants"
        (fun _ ->
            withAuthorityDatabase (fun owner app witness ->
                let firstId, secondId = setup owner app witness
                use connection = new NpgsqlConnection(owner)
                connection.Open()
                use transaction = connection.BeginTransaction()
                let first = witnessedGrant connection transaction firstId
                let second = witnessedGrant connection transaction secondId
                DatabaseRestoreOwnerRoster.verify connection transaction [ first; second ]
                assertRefusals connection transaction first second))

let tests =
    testList
        "Restore report owner roster"
        [
            noPublication
            ownerW1WithoutPublication
            fencedTailGrammar
            approverShape
            binding
        ]
