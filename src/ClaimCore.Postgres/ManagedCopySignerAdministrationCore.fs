namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ManagedCopySignerPolicy

/// Separate schema-owner execution consumes two witnessed human approvals.
/// No caller-provided actor UUID or copy-file key can authorize this mutation.
module internal ManagedCopySignerAdministration =
    let private replay
        (witness: WitnessProtocol)
        eventId
        keyId
        action
        purpose
        firstId
        secondId
        (prior: SignerEventEvidence)
        =
        if
            prior.SigningKeyId <> keyId
            || prior.Action <> ManagedCopySignerCandidate.actionName action
            || prior.Purpose <> purpose
            || Set.ofList [ prior.OwnerApprovalId; prior.CustodianApprovalId ]
               <> Set.ofList [ firstId; secondId ]
        then
            AuthorityWriteOutcome.Refused
        else
            witness.VerifyAuthorityEvidence(
                eventId,
                prior.WitnessSequence,
                prior.WitnessEpoch,
                prior.WitnessEntryHash,
                prior.CandidateSha256
            )

            AuthorityWriteOutcome.Applied(eventId, prior.Revision)

    let private validInput eventId keyId action (publicKey: byte array option) firstId secondId =
        eventId <> Guid.Empty
        && keyId <> Guid.Empty
        && firstId <> Guid.Empty
        && secondId <> Guid.Empty
        && firstId <> secondId
        && (match action, publicKey with
            | CopySignerAction.Register, Some key -> ManagedCopySignature.validPublicKey key
            | CopySignerAction.Retire, None -> true
            | _ -> false)

    let private target
        action
        purpose
        (publicKey: byte array option)
        (previous: SignerState option)
        =
        match action, publicKey, previous with
        | CopySignerAction.Register, Some key, None -> Some(SHA256.HashData(key))
        | CopySignerAction.Retire, None, Some current when
            current.Active && current.Revision < Int64.MaxValue && current.Purpose = purpose
            ->
            Some current.PublicKeySha256
        | _ -> None

    let private loadPair
        connection
        transaction
        witness
        now
        keyId
        action
        purpose
        publicHash
        firstId
        secondId
        =
        task {
            let! first = ManagedCopySignerApprovalRead.load connection transaction firstId
            let! second = ManagedCopySignerApprovalRead.load connection transaction secondId

            return
                match first, second with
                | Some one, Some two -> approved witness now keyId action purpose publicHash one two
                | _ -> None
        }

    let private commitInput
        eventId
        keyId
        action
        purpose
        publicKey
        publicHash
        previous
        owner
        custodian
        : SignerCommitInput =
        {
            EventId = eventId
            SigningKeyId = keyId
            Action = action
            Purpose = purpose
            PublicKey = publicKey
            PublicHash = publicHash
            Previous = previous
            Owner = owner
            Custodian = custodian
        }

    let private holderContinues
        action
        (previous: SignerState option)
        (custodian: SignerApprovalEvidence)
        =
        action <> CopySignerAction.Retire
        || (previous |> Option.exists (fun state -> state.HolderActorId = custodian.ActorId))

    let private newAction
        connection
        transaction
        witness
        eventId
        keyId
        action
        purpose
        publicKey
        firstId
        secondId
        =
        task {
            let! previous = ManagedCopySignerWrite.state connection transaction keyId

            match target action purpose publicKey previous with
            | None -> return AuthorityWriteOutcome.Refused
            | Some publicHash ->
                let! now = Sql.databaseNow connection transaction

                let! pair =
                    loadPair
                        connection
                        transaction
                        witness
                        now
                        keyId
                        action
                        purpose
                        publicHash
                        firstId
                        secondId

                match pair with
                | None -> return AuthorityWriteOutcome.Refused
                | Some(owner, custodian) when holderContinues action previous custodian ->
                    let input =
                        commitInput
                            eventId
                            keyId
                            action
                            purpose
                            publicKey
                            publicHash
                            previous
                            owner
                            custodian

                    return! ManagedCopySignerCommit.apply connection transaction witness input
                | _ -> return AuthorityWriteOutcome.Refused
        }

    let private underLock
        connection
        transaction
        witness
        eventId
        keyId
        action
        purpose
        publicKey
        firstId
        secondId
        =
        task {
            let! installation = matchesInstallation connection transaction witness

            if not installation then
                return AuthorityWriteOutcome.Refused
            else
                let! prior = ManagedCopySignerWrite.event connection transaction eventId

                match prior with
                | None ->
                    return!
                        newAction
                            connection
                            transaction
                            witness
                            eventId
                            keyId
                            action
                            purpose
                            publicKey
                            firstId
                            secondId
                | Some previous ->
                    let! current = ManagedCopySignerWrite.state connection transaction keyId

                    let sameKey =
                        match publicKey, current with
                        | Some supplied, Some stored ->
                            stored.PublicKeySha256 = SHA256.HashData(supplied)
                        | None, Some _ -> true
                        | _ -> false

                    return
                        if sameKey then
                            replay witness eventId keyId action purpose firstId secondId previous
                        else
                            AuthorityWriteOutcome.Refused
        }

    let private run
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        eventId
        keyId
        action
        purpose
        (publicKey: byte array option)
        firstId
        secondId
        =
        task {
            if not (validInput eventId keyId action publicKey firstId secondId) then
                return AuthorityWriteOutcome.Refused
            else
                try
                    OwnerConnection.requireIdentity connection
                    SchemaBaseline.requireCurrent connection
                    witness.Admit()

                    use! _authorityFence =
                        AuthorityOperationFence.acquireShared None connection CancellationToken.None

                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                    let! _ =
                        ActorGrantRead.lockRevision
                            connection
                            transaction
                            true
                            CancellationToken.None

                    return!
                        underLock
                            connection
                            transaction
                            witness
                            eventId
                            keyId
                            action
                            purpose
                            publicKey
                            firstId
                            secondId
                with _ ->
                    return AuthorityWriteOutcome.Unconfirmed eventId
        }

    let register connection witness eventId keyId purpose publicKey firstApproval secondApproval =
        run
            connection
            witness
            eventId
            keyId
            CopySignerAction.Register
            purpose
            (Some publicKey)
            firstApproval
            secondApproval

    let retire connection witness eventId keyId purpose firstApproval secondApproval =
        run
            connection
            witness
            eventId
            keyId
            CopySignerAction.Retire
            purpose
            None
            firstApproval
            secondApproval
