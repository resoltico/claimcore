namespace ClaimCore.Postgres

open Npgsql
open ClaimCore.Application

type internal PostgresCaseTombstoneStore(dataSource: NpgsqlDataSource, witness: WitnessProtocol) =
    interface ITombstoneStore with
        member _.Review(context, caseId) =
            CaseTombstoneReview.review dataSource witness context caseId

        member _.ApproveWitnessPrune(context, proposal, approvalId, expiresAt) =
            CaseTombstonePruneApprovalWrite.approve
                dataSource
                witness
                context
                proposal
                approvalId
                expiresAt

        member _.ApproveTerminal(context, proposal, approvalId, expiresAt) =
            CaseTombstoneTerminalApprovalWrite.approve
                dataSource
                witness
                context
                proposal
                approvalId
                expiresAt

        member _.ChangeHold(context, change) =
            CaseTombstoneHoldWrite.change dataSource witness context change
