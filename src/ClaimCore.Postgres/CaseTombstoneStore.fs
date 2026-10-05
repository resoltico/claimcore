namespace ClaimCore.Postgres

open Npgsql
open ClaimCore.Application

type internal PostgresCaseTombstoneStore(dataSource: NpgsqlDataSource, witness: WitnessProtocol) =
    interface ITombstoneStore with
        member _.Review(context, caseId, ct) =
            CaseTombstoneReview.review dataSource witness context caseId ct

        member _.ApproveWitnessPrune(context, proposal, approvalId, expiresAt, ct) =
            CaseTombstonePruneApprovalWrite.approve
                dataSource
                witness
                context
                proposal
                approvalId
                expiresAt
                ct

        member _.ApproveTerminal(context, proposal, approvalId, expiresAt, ct) =
            CaseTombstoneTerminalApprovalWrite.approve
                dataSource
                witness
                context
                proposal
                approvalId
                expiresAt
                ct

        member _.ChangeHold(context, change, ct) =
            CaseTombstoneHoldWrite.change dataSource witness context change ct
