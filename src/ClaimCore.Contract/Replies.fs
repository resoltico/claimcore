namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

module Replies =
    let decode endpoint bytes =
        match endpoint with
        | Endpoint.Session ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.sessionReply bytes
            |> Result.map WireReply.Session
        | Endpoint.SessionLogout ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.sessionLogoutReply bytes
            |> Result.map WireReply.SessionLogout
        | Endpoint.Context ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.contextReply bytes
            |> Result.map WireReply.Context
        | Endpoint.CaseGet ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.caseGetReply bytes
            |> Result.map WireReply.CaseGet
        | Endpoint.CaseList ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.caseListReply bytes
            |> Result.map WireReply.CaseList
        | Endpoint.CaseHistory ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.caseHistoryReply bytes
            |> Result.map WireReply.CaseHistory
        | Endpoint.CasePreview ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.casePreviewReply bytes
            |> Result.map WireReply.CasePreview
        | Endpoint.CaseExecute ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.caseExecuteReply bytes
            |> Result.map WireReply.CaseExecute
        | Endpoint.OperationObserve ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.operationObserveReply bytes
            |> Result.map WireReply.OperationObserve
        | Endpoint.AuthorityList ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.authorityListReply bytes
            |> Result.map WireReply.AuthorityList
        | Endpoint.AuthorityGrants ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.authorityGrantsReply bytes
            |> Result.map WireReply.AuthorityGrants
        | Endpoint.AuthorityExecute ->
            Codec.decode Limits.MaximumResponseBytes ResponseCodecs.authorityExecuteReply bytes
            |> Result.map WireReply.AuthorityExecute

    let encode =
        function
        | WireReply.Session value -> Codec.encode ResponseCodecs.sessionReply value
        | WireReply.SessionLogout value -> Codec.encode ResponseCodecs.sessionLogoutReply value
        | WireReply.Context value -> Codec.encode ResponseCodecs.contextReply value
        | WireReply.CaseGet value -> Codec.encode ResponseCodecs.caseGetReply value
        | WireReply.CaseList value -> Codec.encode ResponseCodecs.caseListReply value
        | WireReply.CaseHistory value -> Codec.encode ResponseCodecs.caseHistoryReply value
        | WireReply.CasePreview value -> Codec.encode ResponseCodecs.casePreviewReply value
        | WireReply.CaseExecute value -> Codec.encode ResponseCodecs.caseExecuteReply value
        | WireReply.OperationObserve value ->
            Codec.encode ResponseCodecs.operationObserveReply value
        | WireReply.AuthorityList value -> Codec.encode ResponseCodecs.authorityListReply value
        | WireReply.AuthorityGrants value -> Codec.encode ResponseCodecs.authorityGrantsReply value
        | WireReply.AuthorityExecute value ->
            Codec.encode ResponseCodecs.authorityExecuteReply value

    let schema =
        function
        | Endpoint.Session ->
            ResponseCodecs.sessionReply.Schema, ResponseCodecs.sessionReply.Definitions
        | Endpoint.SessionLogout ->
            ResponseCodecs.sessionLogoutReply.Schema, ResponseCodecs.sessionLogoutReply.Definitions
        | Endpoint.Context ->
            ResponseCodecs.contextReply.Schema, ResponseCodecs.contextReply.Definitions
        | Endpoint.CaseGet ->
            ResponseCodecs.caseGetReply.Schema, ResponseCodecs.caseGetReply.Definitions
        | Endpoint.CaseList ->
            ResponseCodecs.caseListReply.Schema, ResponseCodecs.caseListReply.Definitions
        | Endpoint.CaseHistory ->
            ResponseCodecs.caseHistoryReply.Schema, ResponseCodecs.caseHistoryReply.Definitions
        | Endpoint.CasePreview ->
            ResponseCodecs.casePreviewReply.Schema, ResponseCodecs.casePreviewReply.Definitions
        | Endpoint.CaseExecute ->
            ResponseCodecs.caseExecuteReply.Schema, ResponseCodecs.caseExecuteReply.Definitions
        | Endpoint.OperationObserve ->
            ResponseCodecs.operationObserveReply.Schema,
            ResponseCodecs.operationObserveReply.Definitions
        | Endpoint.AuthorityList ->
            ResponseCodecs.authorityListReply.Schema, ResponseCodecs.authorityListReply.Definitions
        | Endpoint.AuthorityGrants ->
            ResponseCodecs.authorityGrantsReply.Schema,
            ResponseCodecs.authorityGrantsReply.Definitions
        | Endpoint.AuthorityExecute ->
            ResponseCodecs.authorityExecuteReply.Schema,
            ResponseCodecs.authorityExecuteReply.Definitions
