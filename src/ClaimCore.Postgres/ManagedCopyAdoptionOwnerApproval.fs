namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal ApprovedCopyAdoption =
    {
        Request: CopyAdoptionApprovalRequest
        ActorId: Guid
        GrantRevision: int64
        CandidateHash: byte array
        WitnessSequence: int64
    }

/// One witnessed HUMAN OWNER draft is rechecked and consumed by the technical owner. Caller
/// bytes cannot assert actor identity, role or current grant; those come from locked primary.
module internal ManagedCopyAdoptionOwnerApproval =
    let private lockApproval connection transaction approvalId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approval_id FROM claimcore.managed_copy_adoption_approvals "
                    + "WHERE approval_id=@approval FOR UPDATE",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            let! value = command.ExecuteScalarAsync()

            return
                match value with
                | :? Guid as found -> found = approvalId
                | _ -> false
        }

    let private principal connection transaction actorId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT principal_kind,issuer,principal_value FROM claimcore.actors "
                    + "WHERE actor_id=@actor",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" actorId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) || reader.GetString(0) <> "HUMAN" then
                return None
            else
                let value =
                    PrincipalKey.fromStorage
                        (reader.GetString(0))
                        (reader.GetString(1))
                        (reader.GetString(2))
                    |> Result.toOption

                return if reader.Read() then None else value
        }

    let private currentOwner
        connection
        transaction
        revision
        caseId
        (stored: StoredAdoptionApproval)
        =
        task {
            let! key = principal connection transaction stored.ActorId

            match key with
            | None -> return false
            | Some principal ->
                let! authority =
                    ActorGrantRead.loadUnderLock
                        connection
                        transaction
                        principal
                        (ResourceScope.Case caseId)
                        revision
                        CancellationToken.None

                return
                    match authority with
                    | None -> false
                    | Some actor ->
                        match
                            ActorAuthorization.authorizeAtRevision
                                principal
                                actor
                                stored.GrantRevision
                                EndpointAction.ApproveCopyAdoption
                                (ResourceScope.Case caseId)
                        with
                        | AuthorizationDecision.Available(actorId, _) -> actorId = stored.ActorId
                        | _ -> false
        }

    let private exact
        (witness: WitnessProtocol)
        (submission: CopyAdoptionSubmission)
        (stored: StoredAdoptionApproval)
        (decoded: DecodedCopyAdoptionApproval)
        now
        =
        let request = decoded.Request

        stored.ActorId = decoded.ActorId
        && stored.GrantRevision = decoded.GrantRevision
        && stored.ApprovedAt = decoded.ApprovedAt
        && stored.ExpiresAt = request.ExpiresAt
        && stored.CaseId = request.CaseId
        && stored.CopyId = request.CopyId
        && stored.AdoptionEventId = request.AdoptionEventId
        && stored.CandidateHash = SHA256.HashData(stored.Canonical)
        && stored.WitnessEpoch = witness.Identity.Epoch
        && stored.ApprovedAt < now
        && stored.ExpiresAt > now
        && submission.ApprovalId = request.ApprovalId
        && submission.AdoptionEventId = request.AdoptionEventId

    let private verifyApproval
        (witness: WitnessProtocol)
        (submission: CopyAdoptionSubmission)
        (stored: StoredAdoptionApproval)
        ct
        =
        task {
            do!
                witness.VerifyAuthorityEvidenceForCase(
                    submission.ApprovalId,
                    stored.WitnessSequence,
                    stored.WitnessEpoch,
                    stored.WitnessHash,
                    stored.CandidateHash,
                    stored.CaseId,
                    ct
                )

        }

    let read
        connection
        transaction
        (witness: WitnessProtocol)
        revision
        (submission: CopyAdoptionSubmission)
        now
        ct
        =
        task {
            let! locked = lockApproval connection transaction submission.ApprovalId

            let! found =
                if locked then
                    ManagedCopyAdoptionApprovalRead.find
                        connection
                        transaction
                        submission.ApprovalId
                else
                    task { return None }

            match found with
            | None -> return None
            | Some stored ->
                let decoded = ManagedCopyAdoptionApprovalCodec.decode stored.Canonical

                let! used =
                    ManagedCopyAdoptionOwnerRead.used connection transaction submission.ApprovalId

                match decoded with
                | Some value when not used && exact witness submission stored value now ->
                    let! current = currentOwner connection transaction revision stored.CaseId stored

                    if not current then
                        return None
                    else
                        do! verifyApproval witness submission stored ct

                        return
                            Some
                                {
                                    Request = value.Request
                                    ActorId = stored.ActorId
                                    GrantRevision = stored.GrantRevision
                                    CandidateHash = stored.CandidateHash
                                    WitnessSequence = stored.WitnessSequence
                                }
                | _ -> return None
        }
