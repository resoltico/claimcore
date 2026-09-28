module internal ClaimCore.IntegrationTests.ActorGrantTestSupport

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FreshBaselineSupport

let human value =
    PrincipalKey.human "https://synthetic.example/realm" value
    |> Result.defaultWith (fun _ -> failtest "Synthetic principal must be valid.")

let service value =
    PrincipalKey.service "https://synthetic.example/realm" value
    |> Result.defaultWith (fun _ -> failtest "Synthetic service principal must be valid.")

let private identity owner =
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

let withAuthorityRuntimeDatabase action =
    withDatabase (fun owner app ->
        initialize owner

        withWitnessFor owner (fun witnessWriter capability ->
            let installation = identity owner
            let store = new Store(witnessWriter, installation, capability)
            let keyId, _ = store.ReadKeyCheck()
            use custody = new KeyRing(keyId, [ keyId, witnessKey () ]) :> IKeyCustody
            use witness = new WitnessProtocol(store, custody, installation)
            witness.Admit()
            action owner app witnessWriter witness))

let withAuthorityDatabase action =
    withAuthorityRuntimeDatabase (fun owner app _ witness -> action owner app witness)

let provision owner witness principal =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    ActorGrantAdministration.provisionInitialOwner connection witness principal
    |> await

let load source principal scope =
    (source :> IActorGrantSource).LoadForScope(principal, scope, CancellationToken.None)
    |> await

let actorId source principal =
    load source principal ResourceScope.Installation
    |> Option.map _.ActorId
    |> Option.defaultWith (fun () -> failtest "Registered synthetic actor is absent.")

let applied result =
    match result with
    | AuthorityWriteOutcome.Applied _ -> ()
    | _ -> failtest "Expected exact witnessed authority application."

let assertRevoked principal (before: ActorAuthority) (after: ActorAuthority) =
    Expect.isGreaterThan after.GrantRevision before.GrantRevision "Revocation advances the fence."

    Expect.isFalse
        (ActorAuthorization.can principal after EndpointAction.ListCases ResourceScope.Installation)
        "Revoked authority is denied."

    Expect.equal
        (ActorAuthorization.authorizeAtRevision
            principal
            after
            before.GrantRevision
            EndpointAction.ListCases
            ResourceScope.Installation)
        AuthorizationDecision.Unavailable
        "A stale grant revision cannot authorize a later commit."

let assertProjectionTamper owner (source: NpgsqlDataSource) witness actorId expectedEnabled =
    use auditConnection = RuntimeDatabase.openConnection source
    DataAudit.run auditConnection witness CancellationToken.None |> await |> ignore
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use rewrite =
        new NpgsqlCommand(
            "UPDATE claimcore.actors SET enabled=false WHERE actor_id=@actor",
            connection
        )

    rewrite.Parameters.AddWithValue("actor", actorId) |> ignore
    Expect.equal (rewrite.ExecuteNonQuery()) 1 "Synthetic owner rewrites one projection."

    use observed =
        new NpgsqlCommand("SELECT enabled FROM claimcore.actors WHERE actor_id=@actor", connection)

    observed.Parameters.AddWithValue("actor", actorId) |> ignore

    Expect.notEqual
        (observed.ExecuteScalar() :?> bool)
        expectedEnabled
        "A primary-owner rewrite cannot alter replayed authority."

    try
        Expect.throwsT<InvalidDataException>
            (fun () ->
                DataAudit.run auditConnection witness CancellationToken.None |> await |> ignore)
            "Full data audit must reject a rewritten actor projection."
    finally
        use restore =
            new NpgsqlCommand(
                "UPDATE claimcore.actors SET enabled=true WHERE actor_id=@actor",
                connection
            )

        restore.Parameters.AddWithValue("actor", actorId) |> ignore
        Expect.equal (restore.ExecuteNonQuery()) 1 "Synthetic projection restored."
