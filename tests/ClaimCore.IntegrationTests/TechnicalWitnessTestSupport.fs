module internal ClaimCore.IntegrationTests.TechnicalWitnessTestSupport

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.FixtureWitnessWriterStore

let cancellation = CancellationToken.None

let rowCount owner table operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            $"SELECT count(*) FROM claimcore.{table} WHERE operation_id=@operation",
            connection
        )

    Sql.uuid command "operation" operationId
    command.ExecuteScalar() :?> int64

let witnessCount writer identity eventId =
    use store = FixtureWitnessWriterStore.current writer identity

    [ Intent; SettledAuthority ]
    |> List.sumBy (fun phase ->
        if
            (store.TryReadEvidence(eventId, phase, CancellationToken.None).GetAwaiter().GetResult())
                .IsSome
        then
            1
        else
            0)

let identity owner =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT installation_id,lineage_id,witness_epoch FROM claimcore.installation_lineage",
            connection
        )

    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic installation identity is absent."

    {
        InstallationId = reader.GetGuid(0)
        LineageId = reader.GetGuid(1)
        Epoch = reader.GetInt64(2)
    }

let protocol owner writer fault =
    let installation = identity owner
    let store = FixtureWitnessWriterStore.current writer installation

    let keyId, _ = (store.ReadKeyCheck(CancellationToken.None).GetAwaiter().GetResult())

    new WitnessProtocol(
        store,
        new KeyRing(keyId, [ keyId, witnessKey () ]) :> IKeyCustody,
        installation,
        fault
    )

let setup action =
    withAuthorityRuntimeDatabase (fun owner app writer witness ->
        let principal = human "technical-owner"
        provision owner witness principal |> applied
        use source = RuntimeDataSource.create app
        let grants = source
        let registry = new ActorGrantRegistry(source, witness)

        registry.SetGrant(
            principal,
            actorId grants principal,
            {
                Role = Role.CaseEditor
                Scope = GrantScope.Installation
            },
            true
        )
        |> await
        |> applied

        action owner source writer witness principal)

let actorContext source (witness: WitnessProtocol) principal action request =
    let gate =
        new PostgresActorGate(source, FixturePrivateFiles.syntheticCommitments witness.Identity)
        :> IActorGate

    gate.Command(principal, action, request, cancellation)
    |> await
    |> Option.defaultWith (fun () -> failtest "Synthetic actor context is absent.")

let draft (request: CommandRequest) (context: ActorCallContext) =
    let bytes = RequestRecord.encode request

    {
        OperationId = request.OperationId
        CaseId = context.CaseId |> Option.defaultWith (fun () -> failtest "Case ID is absent.")
        PreparerActorId = context.Binding.ActorId
        ImporterActorId = None
        PreparerGrantRevision = context.Binding.GrantRevision
        CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
        RequestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        CanonicalRequest = bytes
        PreparingApplicationVersion = BuildIdentity.current.Version
        PreparingContractFingerprint =
            SemanticContract.fingerprint SemanticContract.current
            |> SemanticCoreFingerprint.value
        PreparingContractKind = PreparingContractKind.SemanticCoreV1
    }

let recovery source witness context =
    PostgresRecoveryStore(source, PreparationLimits.defaults, witness, context) :> IRecoveryStore
