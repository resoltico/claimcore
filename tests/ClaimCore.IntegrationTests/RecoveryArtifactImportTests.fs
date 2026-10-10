module ClaimCore.IntegrationTests.RecoveryArtifactImportTests

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
open ClaimCore.IntegrationTests.RecoveryArtifactExportTestSupport

let private setOperator source witness owner enabled =
    let registry = new ActorGrantRegistry(source, witness)

    registry.SetGrant(
        owner,
        actorId (source) owner,
        {
            Role = Role.RecoveryOperator
            Scope = GrantScope.Installation
        },
        enabled
    )
    |> await
    |> applied

let private export (core: IActorClaimsCore) (request: CommandRequest) =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _) -> ()
    | _ -> failtest "Synthetic case must accept before artifact export."

    let digest =
        request |> RequestRecord.encode |> SHA256.HashData |> Convert.ToHexStringLower

    match
        core.Recovery.ExportEnvelope(request.OperationId, digest, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) -> artifact.Bytes
    | _ -> failtest "Authorized export must return settled encrypted bytes."

let private unavailable =
    function
    | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable -> ()
    | _ -> failtest "Inaccessible and nonexistent artifact identities must have one refusal."

let private checkImport (core: IActorClaimsCore) (request: CommandRequest) (bytes: byte array) =
    match core.Recovery.PreviewEnvelopeImport(bytes, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded preview ->
        Expect.equal preview.DecodedEffect.OperationId request.OperationId "Authenticated operation"
        Expect.notEqual preview.CaseId Guid.Empty "Authenticated case ID"
    | _ -> failtest "Authorized current v3 preview must succeed."

    let sourceSha = bytes |> SHA256.HashData |> Convert.ToHexStringLower

    match
        core.Recovery.RetainEnvelopeImport(bytes, sourceSha, CancellationToken.None)
        |> await
    with
    | RecoveryImportRetainOutcome.ObservedAcceptedImport receipt ->
        Expect.equal receipt.OperationId request.OperationId "Exact accepted effect"
    | _ -> failtest "Authorized exact v3 import must observe acceptance."

let private checkRefusals
    (ownerCore: IActorClaimsCore)
    (strangerCore: IActorClaimsCore)
    (request: CommandRequest)
    bytes
    =
    strangerCore.Recovery.PreviewEnvelopeImport(bytes, CancellationToken.None)
    |> await
    |> unavailable

    let altered = Array.copy bytes
    altered[0] <- altered[0] ^^^ 1uy

    strangerCore.Recovery.PreviewEnvelopeImport(altered, CancellationToken.None)
    |> await
    |> unavailable

    let raw = RequestRecord.encode request

    match ownerCore.Recovery.PreviewEnvelopeImport(raw, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.EnvelopeInvalidOrUnsupported -> ()
    | _ -> failtest "Raw or historical recovery bytes must not enter v3 import."

let private privacyFence ownerConnection (core: IActorClaimsCore) reference bytes =
    use connection = new NpgsqlConnection(ownerConnection)
    connection.Open()

    let change state =
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore.cases SET privacy_phase=@phase WHERE case_reference=@reference",
                connection
            )

        Sql.text command "phase" state
        Sql.text command "reference" reference
        Expect.equal (command.ExecuteNonQuery()) 1 "Only synthetic case privacy changes"

    change "ERASURE_REQUESTED"

    try
        core.Recovery.PreviewEnvelopeImport(bytes, CancellationToken.None)
        |> await
        |> unavailable
    finally
        change "ACTIVE"

let private actorBoundImport () =
    withAuthorityRuntimeDatabase (fun ownerConnection app writer witness ->
        let owner = human "artifact-import-owner"
        let stranger = human "artifact-import-stranger"
        provision ownerConnection witness owner |> applied
        use source = RuntimeDataSource.create app
        let registry = new ActorGrantRegistry(source, witness)
        registry.RegisterActor(owner, stranger) |> await |> applied
        grant source witness owner Role.CaseEditor
        grant source witness owner Role.RecoveryExporter

        use runtime =
            Runtime.OpenPostgres(
                app,
                writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                CancellationToken.None
            )
            |> await
            |> accepted

        let request =
            openRequest (Guid.NewGuid()) ("IMPORT-" + Guid.NewGuid().ToString("N"))

        let bytes = export (runtime.ForActor owner) request
        setOperator source witness owner true
        let ownerCore = runtime.ForActor owner
        let strangerCore = runtime.ForActor stranger
        checkImport ownerCore request bytes
        checkRefusals ownerCore strangerCore request bytes
        privacyFence ownerConnection ownerCore request.CaseReference bytes
        setOperator source witness owner false

        ownerCore.Recovery.PreviewEnvelopeImport(bytes, CancellationToken.None)
        |> await
        |> unavailable)

let tests =
    testList
        "actor-bound v3 recovery import"
        [
            testCase
                "[CC-REC-001] import requires current grant, authenticated artifact and active case"
                actorBoundImport
        ]
