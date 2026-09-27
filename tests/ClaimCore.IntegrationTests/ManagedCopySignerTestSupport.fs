module internal ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures

let appliedManagement eventId =
    function
    | ActorManagementOutcome.Applied(id, _, actor) when id = eventId && actor <> Guid.Empty -> ()
    | _ -> failtest "Synthetic owner management must be witnessed."

let approved approvalId =
    function
    | CopySignerApprovalOutcome.Approved(id, revision) when id = approvalId && revision > 0L -> ()
    | _ -> failtest "Authenticated signer approval was not witnessed."

let appliedSigner eventId =
    function
    | AuthorityWriteOutcome.Applied(id, revision) when id = eventId && revision > 0L -> ()
    | AuthorityWriteOutcome.Refused -> failtest "Dual-human owner signer action was refused."
    | AuthorityWriteOutcome.Unconfirmed _ ->
        failtest "Dual-human owner signer action remains unconfirmed."
    | _ -> failtest "Dual-human owner signer action identity diverged."

let openRuntime app writer =
    match
        Runtime.OpenPostgres(
            app,
            writer,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await
    with
    | Ok runtime -> runtime
    | Error _ -> failtest "Synthetic runtime admission refused."

let grantCustodian (runtime: Runtime) owner custodian =
    let management = (runtime.ForActor owner).Management
    let registerId = Guid.NewGuid()

    management.RegisterActor(registerId, custodian, CancellationToken.None)
    |> await
    |> appliedManagement registerId

    let grantId = Guid.NewGuid()

    management.SetGrant(
        grantId,
        custodian,
        Role.AuditorCustodian,
        GrantTarget.Installation,
        true,
        CancellationToken.None
    )
    |> await
    |> appliedManagement grantId

let approval keyId digest action purpose role =
    {
        ApprovalId = Guid.NewGuid()
        SigningKeyId = keyId
        Action = action
        Purpose = purpose
        PublicKeySha256 = digest
        Role = role
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15.0)
    }

let approvePair (runtime: Runtime) owner custodian keyId digest action purpose =
    let custodianRequest =
        approval keyId digest action purpose CopySignerApprovalRole.Custodian

    (runtime.ForActor custodian).ApproveCopySigner(custodianRequest, CancellationToken.None)
    |> await
    |> approved custodianRequest.ApprovalId

    let ownerRequest =
        { approval
              keyId
              digest
              action
              purpose
              (CopySignerApprovalRole.Owner custodianRequest.ApprovalId) with
            ExpiresAt = custodianRequest.ExpiresAt
        }

    (runtime.ForActor owner).ApproveCopySigner(ownerRequest, CancellationToken.None)
    |> await
    |> approved ownerRequest.ApprovalId

    ownerRequest.ApprovalId, custodianRequest.ApprovalId

let signerStatus owner keyId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT active,revision,ed25519_public_key FROM claimcore.managed_copy_signers "
            + "WHERE signing_key_id=@key",
            connection
        )

    command.Parameters.AddWithValue("key", keyId) |> ignore
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Signer roster row must exist."

    let result =
        reader.GetBoolean(0), reader.GetInt64(1), reader.GetFieldValue<byte array>(2)

    Expect.isFalse (reader.Read()) "Exactly one signer row is retained."
    result

let diagnosticCounts owner witnessOwner keyId eventId =
    let scalar (connectionString: string) (sql: string) (parameter: string) (value: Guid) =
        use connection = new NpgsqlConnection(connectionString)
        connection.Open()
        use command = new NpgsqlCommand(sql, connection)
        command.Parameters.AddWithValue(parameter, value) |> ignore
        command.ExecuteScalar() :?> int64

    let signer =
        scalar
            owner
            "SELECT count(*) FROM claimcore.managed_copy_signers WHERE signing_key_id=@key"
            "key"
            keyId

    let intent =
        scalar
            witnessOwner
            "SELECT count(*) FROM claimcore_witness.journal WHERE operation_id=@event"
            "event"
            eventId

    signer, intent
