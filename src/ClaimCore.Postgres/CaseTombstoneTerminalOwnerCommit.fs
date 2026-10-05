namespace ClaimCore.Postgres

open System.Threading
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application

/// Witness INTENT precedes the primary transaction; only a committed owner row plus exact
/// independent SETTLED_AUTHORITY readback can produce a definite terminal response.
module internal CaseTombstoneTerminalOwnerCommit =
    let private settle (witness: WitnessProtocol) (value: TerminalCopyProposal) intent phase =
        task {
            try
                let! _ = witness.SettleAuthority(value.EventId, intent)

                do!
                    witness.VerifyAuthorityEvidenceForCase(
                        value.EventId,
                        intent.Ticket.Sequence,
                        intent.Ticket.Epoch,
                        intent.Ticket.EntryHash,
                        intent.CandidateHash,
                        value.CaseId,
                        CancellationToken.None
                    )

                return OwnerTerminalOutcome.Advanced(value.EventId, phase)
            with _ ->
                return OwnerTerminalOutcome.Unconfirmed value.EventId
        }

    let commit
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        actorAuthorityRevision
        proposal
        (ready: OwnerTerminalReady)
        ct
        =
        task {
            let value = TombstoneTerminalProposal.copy proposal

            let canonical =
                CaseTombstoneTerminalEventCandidate.encode
                    proposal
                    ready.Copy
                    ready.Fence
                    ready.Approvals
                    actorAuthorityRevision
                    ready.Stored.AuthorityRevision
                    ready.Stored.AuthorityHash
                    ready.ObservedAt

            try
                try
                    let! intent =
                        witness.BeginAuthority(value.EventId, canonical, Some value.CaseId, ct)

                    do!
                        CaseTombstoneTerminalEventWrite.persist
                            connection
                            transaction
                            proposal
                            ready.Copy
                            ready.Fence
                            ready.Approvals
                            actorAuthorityRevision
                            ready.Stored
                            ready.ObservedAt
                            canonical
                            intent

                    do! transaction.CommitAsync(ct)

                    return! settle witness value intent ready.NextPhase
                with _ ->
                    return OwnerTerminalOutcome.Unconfirmed value.EventId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }
