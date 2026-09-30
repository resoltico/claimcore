namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

/// Registration writes use the caller's locked authority revision and transaction.
module internal ActorRegistrationWrite =
    let private principalExists connection transaction principal =
        task {
            let kind, issuer, stable = PrincipalKey.storageParts principal

            use query =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.actors "
                    + "WHERE principal_kind=@kind AND issuer=@issuer AND principal_value=@value)",
                    connection,
                    transaction
                )

            Sql.text query "kind" kind
            Sql.text query "issuer" issuer
            Sql.text query "value" stable
            let! exists = query.ExecuteScalarAsync()
            return exists :?> bool
        }

    let register connection transaction witness approverId revision targetPrincipal =
        task {
            let! exists = principalExists connection transaction targetPrincipal

            if exists then
                return AuthorityWriteOutcome.Refused
            else
                let targetId = Guid.NewGuid()

                let action: ActorAuthorityAction =
                    {
                        EventId = Guid.NewGuid()
                        Revision = revision + 1L
                        ActionName = "REGISTER_ACTOR"
                        TargetActorId = targetId
                        ApproverActorId = Some approverId
                        Principal = Some targetPrincipal
                        Grant = None
                        Enabled = Some true
                    }

                return!
                    ActorGrantWrite.run connection transaction witness action (fun () ->
                        ActorGrantWrite.insertActor
                            connection
                            transaction
                            targetId
                            targetPrincipal
                            action.Revision)
        }
