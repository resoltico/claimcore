namespace ClaimCore.Hosting

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
            | PreviewOutcome.Reviewed review ->
                ClaimCore.Contract.Protocol.CasePreviewOutcome.Reviewed { Review = review }
            | PreviewOutcome.AlreadyAccepted receipt ->
                ClaimCore.Contract.Protocol.CasePreviewOutcome.AlreadyAccepted(receipt)
            | PreviewOutcome.Rejected reason ->
                ClaimCore.Contract.Protocol.CasePreviewOutcome.Rejected(reason)
            | PreviewOutcome.Failed fault ->
                ClaimCore.Contract.Protocol.CasePreviewOutcome.Failed(fault)
            | PreviewOutcome.Cancelled -> ClaimCore.Contract.Protocol.CasePreviewOutcome.Cancelled()

        ClaimCore.Contract.Protocol.WireReply.CasePreview
            {
                OperationId = operationId
                Outcome = outcome
            }

    let execute operationId value =
        let outcome =
            match value with
            | ExecuteOutcome.Accepted receipt ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.Accepted(receipt)
            | ExecuteOutcome.AlreadyAccepted receipt ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.AlreadyAccepted(receipt)
            | ExecuteOutcome.OutcomeUnknown id ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.OutcomeUnknown { OperationId = id }
            | ExecuteOutcome.Rejected reason ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.Rejected(reason)
            | ExecuteOutcome.Failed fault ->
                ClaimCore.Contract.Protocol.CaseExecuteOutcome.Failed(fault)
            | ExecuteOutcome.Cancelled -> ClaimCore.Contract.Protocol.CaseExecuteOutcome.Cancelled()

        ClaimCore.Contract.Protocol.WireReply.CaseExecute
            {
                OperationId = operationId
                Outcome = outcome
            }

    let authorityExecute operationId value =
        let outcome =
            match value with
            | ExecuteOutcome.Accepted receipt ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.Accepted(receipt)
            | ExecuteOutcome.AlreadyAccepted receipt ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.AlreadyAccepted(receipt)
            | ExecuteOutcome.OutcomeUnknown id ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.OutcomeUnknown
                    { OperationId = id }
            | ExecuteOutcome.Rejected reason ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.Rejected(reason)
            | ExecuteOutcome.Failed fault ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.Failed(fault)
            | ExecuteOutcome.Cancelled ->
                ClaimCore.Contract.Protocol.AuthorityExecuteOutcome.Cancelled()

        ClaimCore.Contract.Protocol.WireReply.AuthorityExecute
            {
                OperationId = operationId
                Outcome = outcome
            }
