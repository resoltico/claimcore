namespace ClaimCore.Web.Composition

open ClaimCore.Contract

open ClaimCore.Application

module internal OutcomeMapping =
    let private query succeeded rejected failed =
        function
        | ReadReply.Succeeded value -> succeeded value
        | ReadReply.Rejected value -> rejected value
        | ReadReply.Failed value -> failed value

    let context value =
        let outcome =
            query
                ClaimCore.Contract.Protocol.ContextOutcome.Succeeded
                ClaimCore.Contract.Protocol.ContextOutcome.Rejected
                ClaimCore.Contract.Protocol.ContextOutcome.Failed
                value

        ClaimCore.Contract.Protocol.WireReply.Context { Outcome = outcome }

    let caseGet value =
        let outcome =
            query
                ClaimCore.Contract.Protocol.CaseGetOutcome.Succeeded
                ClaimCore.Contract.Protocol.CaseGetOutcome.Rejected
                ClaimCore.Contract.Protocol.CaseGetOutcome.Failed
                value

        ClaimCore.Contract.Protocol.WireReply.CaseGet { Outcome = outcome }

    let caseList value =
        let outcome =
            query
                ClaimCore.Contract.Protocol.CaseListOutcome.Succeeded
                ClaimCore.Contract.Protocol.CaseListOutcome.Rejected
                ClaimCore.Contract.Protocol.CaseListOutcome.Failed
                value

        ClaimCore.Contract.Protocol.WireReply.CaseList { Outcome = outcome }

    let caseHistory value =
        let outcome =
            query
                ClaimCore.Contract.Protocol.CaseHistoryOutcome.Succeeded
                ClaimCore.Contract.Protocol.CaseHistoryOutcome.Rejected
                ClaimCore.Contract.Protocol.CaseHistoryOutcome.Failed
                value

        ClaimCore.Contract.Protocol.WireReply.CaseHistory { Outcome = outcome }

    let operationObserve value =
        let outcome =
            query
                ClaimCore.Contract.Protocol.OperationObserveOutcome.Succeeded
                ClaimCore.Contract.Protocol.OperationObserveOutcome.Rejected
                ClaimCore.Contract.Protocol.OperationObserveOutcome.Failed
                value

        ClaimCore.Contract.Protocol.WireReply.OperationObserve { Outcome = outcome }

    let authorityList value =
        let outcome =
            query
                ClaimCore.Contract.Protocol.AuthorityListOutcome.Succeeded
                ClaimCore.Contract.Protocol.AuthorityListOutcome.Rejected
                ClaimCore.Contract.Protocol.AuthorityListOutcome.Failed
                value

        ClaimCore.Contract.Protocol.WireReply.AuthorityList { Outcome = outcome }

    let authorityGrants value =
        let outcome =
            query
                ClaimCore.Contract.Protocol.AuthorityGrantsOutcome.Succeeded
                ClaimCore.Contract.Protocol.AuthorityGrantsOutcome.Rejected
                ClaimCore.Contract.Protocol.AuthorityGrantsOutcome.Failed
                value

        ClaimCore.Contract.Protocol.WireReply.AuthorityGrants { Outcome = outcome }

    let preview operationId value =
        let outcome =
            match value with
            | PreviewResult.Reviewed(OperationReview.Case review) ->
                ClaimCore.Contract.Protocol.CasePreviewOutcome.Reviewed { Review = review }
            | PreviewResult.Reviewed(OperationReview.Authority _) ->
                invalidArg (nameof value) "Expected a case preview for this protocol endpoint."
            | PreviewResult.AlreadyAccepted receipt ->
                ClaimCore.Contract.Protocol.CasePreviewOutcome.AlreadyAccepted(receipt)
            | PreviewResult.Rejected reason ->
                ClaimCore.Contract.Protocol.CasePreviewOutcome.Rejected(reason)
            | PreviewResult.Failed fault ->
                ClaimCore.Contract.Protocol.CasePreviewOutcome.Failed(fault)

        ClaimCore.Contract.Protocol.WireReply.CasePreview
            {
                OperationId = operationId
                Outcome = outcome
            }

    let execute operationId value =
        let outcome =
            match value with
            | ExecuteResult.Accepted receipt ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.Accepted(receipt)
            | ExecuteResult.AlreadyAccepted receipt ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.AlreadyAccepted(receipt)
            | ExecuteResult.OutcomeUnknown ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.OutcomeUnknown
                    { OperationId = operationId }
            | ExecuteResult.Rejected reason ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.Rejected(reason)
            | ExecuteResult.Failed fault ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.Failed(fault)

        ClaimCore.Contract.Protocol.WireReply.CaseExecute
            {
                OperationId = operationId
                Outcome = outcome
            }

    let authorityExecute operationId value =
        let outcome =
            match value with
            | ExecuteResult.Accepted receipt ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.Accepted(receipt)
            | ExecuteResult.AlreadyAccepted receipt ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.AlreadyAccepted(receipt)
            | ExecuteResult.OutcomeUnknown ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.OutcomeUnknown
                    { OperationId = operationId }
            | ExecuteResult.Rejected reason ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.Rejected(reason)
            | ExecuteResult.Failed fault ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.Failed(fault)

        ClaimCore.Contract.Protocol.WireReply.AuthorityExecute
            {
                OperationId = operationId
                Outcome = outcome
            }
