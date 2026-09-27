namespace ClaimCore.Postgres

open System
open ClaimCore.Witness

/// After the reviewed recovery cutoff, only the two exact owner approvals may advance tip.
module internal WriterHandoffCutoff =
    let verify (witness: WitnessProtocol) (proposal: WriterHandoffPreparation) =
        let lower = proposal.ReviewedCutoffSequence
        let upper = proposal.ExpectedTipSequence

        if upper - lower <> 4L then
            invalidOp "Writer handoff cutoff includes unreviewed authority."

        let allowed = set [ proposal.ApprovalOneId; proposal.ApprovalTwoId ]
        let mutable after = lower
        let mutable previous = proposal.ReviewedCutoffHash
        let seen = ResizeArray<Guid * Phase>()

        while after < upper do
            let page = witness.EvidenceStore.ReadMetadataPage(after, previous, upper, 32)

            for item in page.Items do
                let ticket = item.Ticket

                if
                    ticket.Sequence <> after + 1L
                    || ticket.ScopeKind <> Installation
                    || not (allowed.Contains ticket.OperationId)
                    || (ticket.Phase <> Intent && ticket.Phase <> SettledAuthority)
                then
                    invalidOp "Writer handoff cutoff includes unreviewed authority."

                seen.Add(ticket.OperationId, ticket.Phase)
                after <- ticket.Sequence
                previous <- ticket.EntryHash

        if
            previous <> proposal.ExpectedTipHash
            || seen.Count <> 4
            || not (
                allowed
                |> Set.forall (fun id ->
                    seen.Contains(id, Intent) && seen.Contains(id, SettledAuthority))
            )
        then
            invalidOp "Writer handoff approval chain is incomplete."
