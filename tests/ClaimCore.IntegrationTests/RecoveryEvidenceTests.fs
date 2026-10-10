module ClaimCore.IntegrationTests.RecoveryEvidenceTests

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private openRuntime () =
    witnessedOpen (appConnection ()) CancellationToken.None
    |> await
    |> Result.defaultWith runtimeOpeningFailure

let private prepare (core: IActorClaimsCore) operationId =
    let command = openRequest operationId ("EVIDENCE-" + operationId.ToString("N"))

    match core.Prepare(command, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Retained digest is required.")
    | _ -> failtest "Synthetic preparation must be retained."

let private inspect (core: IActorClaimsCore) operationId afterCursor limit =
    match
        core.Recovery.Inspect(operationId, afterCursor, limit, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection details)) ->
        details
    | _ -> failtest "Retained recovery evidence must be inspectable."

let private resolve (core: IActorClaimsCore) operationId digest =
    match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await with
    | ResolveOutcome.ResolveCompleted(_, _, DefiniteExecution.Accepted _, _) -> ()
    | _ -> failtest "Synthetic exact resolution must be accepted."

let private settledAttempt =
    testCase "[CC-REC-001] inspect projects the accepted attempt and settlement" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()
        let core = runtime.ForActor(ActorBoundStoreFixture.actorPrincipal ())
        let digest = prepare core operationId
        let before = inspect core operationId None recoveryPageLimit
        Expect.isEmpty before.Preparation.Attempts.Items "No attempt before submission"


        resolve core operationId digest
        let after = inspect core operationId None recoveryPageLimit

        match after.Preparation.Attempts.Items with
        | [ attempt ] ->
            Expect.notEqual attempt.AttemptId Guid.Empty "Durable attempt ID"
            Expect.equal attempt.Settlement (Some "ACCEPTED") "Accepted technical settlement"
            Expect.isSome attempt.SettledAt "Settlement timestamp"
        | _ -> failtest "Exactly one accepted attempt must be visible."

        match after.Observation with
        | Lookup.Found receipt -> Expect.equal receipt.OperationId operationId "Receipt separate"
        | Lookup.NotFound _ -> failtest "Accepted operation must remain observable.")

let private preserveUnsettledOnPrune operationId (core: IActorClaimsCore) earlierId =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use age =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET recorded_at=clock_timestamp()-interval '3 days' WHERE operation_id=@operation",
            connection
        )

    Sql.uuid age "operation" operationId
    Expect.equal (age.ExecuteNonQuery()) 1 "Only the synthetic accepted receipt is aged"

    PreparationPruning.prune
        (adminConnection ())
        { PreparationPruneOptions.defaults with
            SettledRetentionDays = 1
        }
    |> completedAdministration
    |> ignore

    let retained = inspect core operationId None recoveryPageLimit

    let unresolved =
        retained.Preparation.Attempts.Items
        |> List.find (fun item -> item.AttemptId = earlierId)

    Expect.isNone
        unresolved.Settlement
        "Pruning cannot erase an earlier unsettled attempt after acceptance"

let private unresolvedAttempt =
    testCase "[CC-REC-001] inspect preserves an earlier unsettled attempt" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()
        let core = runtime.ForActor(ActorBoundStoreFixture.actorPrincipal ())
        let digest = prepare core operationId
        use source = NpgsqlDataSource.Create(appConnection ())

        let gate =
            new PostgresActorGate(
                source,
                FixturePrivateFiles.syntheticCommitments (witnessProtocol ()).Identity
            )
            :> IActorGate

        let context =
            gate.Operation(
                ActorBoundStoreFixture.actorPrincipal (),
                EndpointAction.RecoveryResolve,
                operationId,
                CancellationToken.None
            )
            |> await
            |> Option.defaultWith (fun () -> failtest "Synthetic recovery actor is unavailable.")

        let recovery =
            PostgresRecoveryStore(source, PreparationLimits.defaults, witnessProtocol (), context)
            :> IRecoveryStore

        let firstId =
            match recovery.Start(operationId, CancellationToken.None) |> await with
            | Ok(RecoveryStart.Started(attemptId, _)) -> attemptId
            | _ -> failtest "Technical attempt must be durably admitted."

        let before = inspect core operationId None recoveryPageLimit

        match before.Preparation.Attempts.Items with
        | [ attempt ] ->
            Expect.equal attempt.AttemptId firstId "Unsettled attempt identity"
            Expect.isNone attempt.Settlement "No invented settlement"
            Expect.isNone attempt.SettledAt "No invented settlement time"
        | _ -> failtest "Unsettled attempt must appear in recovery details."

        resolve core operationId digest
        let after = inspect core operationId None recoveryPageLimit
        Expect.equal after.Preparation.Attempts.Items.Length 2 "Distinct exact retry attempt"

        let earlier =
            after.Preparation.Attempts.Items
            |> List.find (fun item -> item.AttemptId = firstId)

        Expect.isNone earlier.Settlement "Earlier uncertainty remains unresolved"

        Expect.equal
            (after.Preparation.Attempts.Items
             |> List.filter (fun item -> item.Settlement.IsSome))
                .Length
            1
            "Only definite later attempt is settled"

        preserveUnsettledOnPrune operationId core firstId)

let private signedOnlyImportSurface =
    testCase "[CC-REC-001] recovery workflow exposes no raw canonical import" (fun () ->
        let methods =
            typeof<IRecoveryWorkflow>.GetMethods() |> Array.map _.Name |> Set.ofArray

        Expect.isTrue (methods.Contains "PreviewEnvelopeImport") "Signed preview remains"
        Expect.isTrue (methods.Contains "RetainEnvelopeImport") "Signed retain remains"
        Expect.isFalse (methods.Contains "PreviewCanonicalRecordImport") "No raw preview"
        Expect.isFalse (methods.Contains "RetainCanonicalRecordImport") "No raw retain")

let private withEditorRecovery action =
    withAuthorityDatabase (fun owner app witness ->
        let principal = human "provenance-owner"
        provision owner witness principal |> applied
        use source = RuntimeDataSource.create app
        let registry = new ActorGrantRegistry(source, witness)
        let editor = human "provenance-editor"
        registry.RegisterActor(principal, editor) |> await |> applied
        let grants = source
        let editorId = actorId grants editor

        let grant =
            {
                Role = Role.CaseEditor
                Scope = GrantScope.Installation
            }

        registry.SetGrant(principal, editorId, grant, true) |> await |> applied

        let authority =
            load grants editor ResourceScope.Installation
            |> Option.defaultWith (fun () -> failtest "Synthetic editor is missing.")

        let caseId = Guid.NewGuid()

        let context: ActorCallContext =
            {
                MayEditCommands = true
                AllowedRecoveryActions = []
                Binding =
                    {
                        Principal = editor
                        ActorId = editorId
                        GrantRevision = authority.GrantRevision
                    }
                CaseId = Some caseId
                Action = EndpointAction.PrepareNewCase
                Suppression = FixturePrivateFiles.syntheticCommitments witness.Identity
            }

        let recovery =
            PostgresRecoveryStore(source, PreparationLimits.defaults, witness, context)
            :> IRecoveryStore

        action recovery caseId editorId authority.GrantRevision)

let private provenanceDraft caseId editorId grantRevision =
    let request = newRequest ()
    let canonical = RequestRecord.encode request

    let fingerprint =
        SemanticContract.fingerprint SemanticContract.current
        |> SemanticCoreFingerprint.value

    let draft: RecoveryPreparationDraft =
        {
            OperationId = request.OperationId
            CaseId = caseId
            PreparerActorId = editorId
            ImporterActorId = None
            PreparerGrantRevision = grantRevision
            CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
            RequestSha256 = canonical |> SHA256.HashData |> Convert.ToHexStringLower
            CanonicalRequest = canonical
            PreparingApplicationVersion = BuildIdentity.current.Version
            PreparingContractFingerprint = fingerprint
            PreparingContractKind = PreparingContractKind.SemanticCoreV1
        }

    draft

let private provenanceReplay =
    testCase "[CC-REC-001] exact replay preserves first producer provenance" (fun () ->
        withEditorRecovery (fun recovery caseId editorId grantRevision ->
            let original = provenanceDraft caseId editorId grantRevision

            let first =
                match recovery.Retain(original, CancellationToken.None) |> await with
                | Ok(RecoveryRetain.Created value) -> value
                | _ -> failtest "First producer must create the exact request."

            let altered =
                { original with
                    PreparingApplicationVersion = "999.0.0"
                    PreparingContractFingerprint = String.replicate 64 "b"
                }

            let replay =
                match recovery.Retain(altered, CancellationToken.None) |> await with
                | Ok(RecoveryRetain.Existing value) -> value
                | _ -> failtest "Exact bytes must replay as existing."

            Expect.equal replay.PreparedAt first.PreparedAt "First technical timestamp preserved"
            Expect.equal replay.CaseId caseId "First reserved case identity preserved"
            Expect.equal replay.PreparerActorId editorId "First preparer remains attributed"

            Expect.equal
                replay.CanonicalRequest
                first.CanonicalRequest
                "Exact request bytes preserved"

            Expect.equal
                replay.PreparingApplicationVersion
                first.PreparingApplicationVersion
                "Version"

            Expect.equal
                replay.PreparingContractFingerprint
                original.PreparingContractFingerprint
                "First fingerprint preserved"))

let tests =
    testList
        "PostgreSQL recovery evidence"
        [ settledAttempt; unresolvedAttempt; signedOnlyImportSurface; provenanceReplay ]
