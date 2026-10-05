namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal SignerCommitInput =
    {
        EventId: Guid
        SigningKeyId: Guid
        Action: CopySignerAction
        Purpose: CopySignerPurpose
        PublicKey: byte array option
        PublicHash: byte array
        Previous: SignerState option
        Owner: SignerApprovalEvidence
        Custodian: SignerApprovalEvidence
    }

/// Candidate construction and one co-commit; an external settlement confirms definite success.
module internal ManagedCopySignerCommit =
    let private revision input =
        input.Previous
        |> Option.map (fun current -> current.Revision + 1L)
        |> Option.defaultValue 1L

    let private priorHash input =
        input.Previous
        |> Option.map _.EventHash
        |> Option.defaultValue (Array.zeroCreate<byte> 32)

    let private candidate input =
        ManagedCopySignerCandidate.roster
            input.EventId
            input.SigningKeyId
            (ManagedCopySignerCandidate.actionName input.Action)
            input.Purpose
            (revision input)
            input.PublicHash
            input.Owner.ActorId
            input.Custodian.ActorId
            input.Owner.ApprovalId
            input.Custodian.ApprovalId
            input.Owner.CandidateSha256
            input.Custodian.CandidateSha256

    let private updateRoster connection transaction input eventHash =
        match input.PublicKey, input.Previous with
        | Some key, None ->
            ManagedCopySignerWrite.register
                connection
                transaction
                input.SigningKeyId
                input.Purpose
                input.Custodian.ActorId
                key
                input.PublicHash
                eventHash
        | None, Some current ->
            ManagedCopySignerWrite.retire
                connection
                transaction
                input.SigningKeyId
                current.Revision
                eventHash
        | _ -> invalidOp "Signer transition is invalid."

    let private appendEvidence connection transaction input canonical previous eventHash intent =
        task {
            do!
                ManagedCopySignerWrite.append
                    connection
                    transaction
                    input.EventId
                    input.SigningKeyId
                    (revision input)
                    (ManagedCopySignerCandidate.actionName input.Action)
                    input.Purpose
                    input.Owner
                    input.Custodian
                    canonical
                    previous
                    eventHash
                    intent

            do!
                ManagedCopySignerWrite.useApproval
                    connection
                    transaction
                    input.EventId
                    input.Owner.ApprovalId
                    "OWNER"

            do!
                ManagedCopySignerWrite.useApproval
                    connection
                    transaction
                    input.EventId
                    input.Custodian.ApprovalId
                    "CUSTODIAN"
        }

    let apply
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (input: SignerCommitInput)
        =
        task {
            let canonical = candidate input

            try
                let previous = priorHash input
                let eventHash = ManagedCopySignerCandidate.eventHash previous canonical

                let! intent =
                    witness.BeginAuthority(input.EventId, canonical, None, CancellationToken.None)

                do! updateRoster connection transaction input eventHash
                do! appendEvidence connection transaction input canonical previous eventHash intent
                do! transaction.CommitAsync()
                let! _ = witness.SettleAuthority(input.EventId, intent)
                return AuthorityWriteOutcome.Applied(input.EventId, revision input)
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }
