namespace ClaimCore.Hosting

open ClaimCore.Application

module internal OutcomeMapping =
    let private query succeeded rejected failed cancelled map =
        function
        | QueryOutcome.Succeeded value -> succeeded (map value)
        | QueryOutcome.Rejected value -> rejected (ResponseMapping.rejection value)
        | QueryOutcome.Failed value -> failed (ResponseMapping.fault value)
        | QueryOutcome.Cancelled -> cancelled ()

    let context value =
        let outcome =
            query
                ClaimCore.Wire.ContextOutcome.Succeeded
                ClaimCore.Wire.ContextOutcome.Rejected
                ClaimCore.Wire.ContextOutcome.Failed
                ClaimCore.Wire.ContextOutcome.Cancelled
                ResponseMapping.context
                value

        ClaimCore.Wire.WireReply.Context { Outcome = outcome }

    let caseGet value =
        let outcome =
            query
                ClaimCore.Wire.CaseGetOutcome.Succeeded
                ClaimCore.Wire.CaseGetOutcome.Rejected
                ClaimCore.Wire.CaseGetOutcome.Failed
                ClaimCore.Wire.CaseGetOutcome.Cancelled
                ResponseMapping.caseRecord
                value

        ClaimCore.Wire.WireReply.CaseGet { Outcome = outcome }

    let caseList value =
        let outcome =
            query
                ClaimCore.Wire.CaseListOutcome.Succeeded
                ClaimCore.Wire.CaseListOutcome.Rejected
                ClaimCore.Wire.CaseListOutcome.Failed
                ClaimCore.Wire.CaseListOutcome.Cancelled
                ResponseMapping.casesPage
                value

        ClaimCore.Wire.WireReply.CaseList { Outcome = outcome }

    let caseHistory value =
        let outcome =
            query
                ClaimCore.Wire.CaseHistoryOutcome.Succeeded
                ClaimCore.Wire.CaseHistoryOutcome.Rejected
                ClaimCore.Wire.CaseHistoryOutcome.Failed
                ClaimCore.Wire.CaseHistoryOutcome.Cancelled
                ResponseMapping.historyPage
                value

        ClaimCore.Wire.WireReply.CaseHistory { Outcome = outcome }

    let operationObserve value =
        let outcome =
            query
                ClaimCore.Wire.OperationObserveOutcome.Succeeded
                ClaimCore.Wire.OperationObserveOutcome.Rejected
                ClaimCore.Wire.OperationObserveOutcome.Failed
                ClaimCore.Wire.OperationObserveOutcome.Cancelled
                ResponseMapping.receipt
                value

        ClaimCore.Wire.WireReply.OperationObserve { Outcome = outcome }

    let authorityList value =
        let outcome =
            query
                ClaimCore.Wire.AuthorityListOutcome.Succeeded
                ClaimCore.Wire.AuthorityListOutcome.Rejected
                ClaimCore.Wire.AuthorityListOutcome.Failed
                ClaimCore.Wire.AuthorityListOutcome.Cancelled
                ResponseMapping.actorsPage
                value

        ClaimCore.Wire.WireReply.AuthorityList { Outcome = outcome }

    let authorityGrants value =
        let outcome =
            query
                ClaimCore.Wire.AuthorityGrantsOutcome.Succeeded
                ClaimCore.Wire.AuthorityGrantsOutcome.Rejected
                ClaimCore.Wire.AuthorityGrantsOutcome.Failed
                ClaimCore.Wire.AuthorityGrantsOutcome.Cancelled
                ResponseMapping.grantsPage
                value

        ClaimCore.Wire.WireReply.AuthorityGrants { Outcome = outcome }

    let preview operationId value =
        let outcome =
            match value with
            | PreviewOutcome.Reviewed review ->
                ClaimCore.Wire.CasePreviewOutcome.Reviewed
                    {
                        Review = ResponseMapping.review review
                    }
            | PreviewOutcome.AlreadyAccepted receipt ->
                ClaimCore.Wire.CasePreviewOutcome.AlreadyAccepted(
                    ResponseMapping.caseReceipt receipt
                )
            | PreviewOutcome.Rejected reason ->
                ClaimCore.Wire.CasePreviewOutcome.Rejected(ResponseMapping.rejection reason)
            | PreviewOutcome.Failed fault ->
                ClaimCore.Wire.CasePreviewOutcome.Failed(ResponseMapping.fault fault)
            | PreviewOutcome.Cancelled -> ClaimCore.Wire.CasePreviewOutcome.Cancelled()

        ClaimCore.Wire.WireReply.CasePreview
            {
                OperationId = operationId
                Outcome = outcome
            }

    let execute operationId value =
        let outcome =
            match value with
            | ExecuteOutcome.Accepted receipt ->
                ClaimCore.Wire.CaseExecuteOutcome.Accepted(ResponseMapping.caseReceipt receipt)
            | ExecuteOutcome.AlreadyAccepted receipt ->
                ClaimCore.Wire.CaseExecuteOutcome.AlreadyAccepted(
                    ResponseMapping.caseReceipt receipt
                )
            | ExecuteOutcome.OutcomeUnknown id ->
                ClaimCore.Wire.CaseExecuteOutcome.OutcomeUnknown { OperationId = id }
            | ExecuteOutcome.Rejected reason ->
                ClaimCore.Wire.CaseExecuteOutcome.Rejected(ResponseMapping.rejection reason)
            | ExecuteOutcome.Failed fault ->
                ClaimCore.Wire.CaseExecuteOutcome.Failed(ResponseMapping.fault fault)
            | ExecuteOutcome.Cancelled -> ClaimCore.Wire.CaseExecuteOutcome.Cancelled()

        ClaimCore.Wire.WireReply.CaseExecute
            {
                OperationId = operationId
                Outcome = outcome
            }

    let authorityExecute operationId value =
        let outcome =
            match value with
            | ExecuteOutcome.Accepted receipt ->
                ClaimCore.Wire.AuthorityExecuteOutcome.Accepted(
                    ResponseMapping.authorityReceipt receipt
                )
            | ExecuteOutcome.AlreadyAccepted receipt ->
                ClaimCore.Wire.AuthorityExecuteOutcome.AlreadyAccepted(
                    ResponseMapping.authorityReceipt receipt
                )
            | ExecuteOutcome.OutcomeUnknown id ->
                ClaimCore.Wire.AuthorityExecuteOutcome.OutcomeUnknown { OperationId = id }
            | ExecuteOutcome.Rejected reason ->
                ClaimCore.Wire.AuthorityExecuteOutcome.Rejected(ResponseMapping.rejection reason)
            | ExecuteOutcome.Failed fault ->
                ClaimCore.Wire.AuthorityExecuteOutcome.Failed(ResponseMapping.fault fault)
            | ExecuteOutcome.Cancelled -> ClaimCore.Wire.AuthorityExecuteOutcome.Cancelled()

        ClaimCore.Wire.WireReply.AuthorityExecute
            {
                OperationId = operationId
                Outcome = outcome
            }
