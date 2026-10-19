namespace ClaimCore.Wire

/// Shape admission deliberately leaves business scalar format and policy to Application.
module Admission =
    let private read codec construct bytes =
        Codec.decode Limits.MaximumRequestBytes codec bytes |> Result.map construct

    let admit endpoint bytes =
        match endpoint with
        | Endpoint.Session -> read RequestCodecs.empty (fun () -> WireRequest.Session) bytes
        | Endpoint.SessionLogout ->
            read RequestCodecs.empty (fun () -> WireRequest.SessionLogout) bytes
        | Endpoint.Context -> read RequestCodecs.empty (fun () -> WireRequest.Context) bytes
        | Endpoint.CaseGet -> read RequestCodecs.caseGetRequest WireRequest.CaseGet bytes
        | Endpoint.CaseList -> read RequestCodecs.pageRequest WireRequest.CaseList bytes
        | Endpoint.CaseHistory -> read RequestCodecs.historyRequest WireRequest.CaseHistory bytes
        | Endpoint.CasePreview -> read RequestCodecs.caseRequest WireRequest.CasePreview bytes
        | Endpoint.CaseExecute -> read RequestCodecs.caseRequest WireRequest.CaseExecute bytes
        | Endpoint.OperationObserve ->
            read RequestCodecs.observeRequest WireRequest.OperationObserve bytes
        | Endpoint.AuthorityList -> read RequestCodecs.pageRequest WireRequest.AuthorityList bytes
        | Endpoint.AuthorityGrants ->
            read RequestCodecs.grantsRequest WireRequest.AuthorityGrants bytes
        | Endpoint.AuthorityExecute ->
            read RequestCodecs.authorityRequest WireRequest.AuthorityExecute bytes

    let encode =
        function
        | WireRequest.Session
        | WireRequest.SessionLogout
        | WireRequest.Context -> Codec.encode RequestCodecs.empty ()
        | WireRequest.CaseGet value -> Codec.encode RequestCodecs.caseGetRequest value
        | WireRequest.CaseList value
        | WireRequest.AuthorityList value -> Codec.encode RequestCodecs.pageRequest value
        | WireRequest.CaseHistory value -> Codec.encode RequestCodecs.historyRequest value
        | WireRequest.CasePreview value
        | WireRequest.CaseExecute value -> Codec.encode RequestCodecs.caseRequest value
        | WireRequest.OperationObserve value -> Codec.encode RequestCodecs.observeRequest value
        | WireRequest.AuthorityGrants value -> Codec.encode RequestCodecs.grantsRequest value
        | WireRequest.AuthorityExecute value -> Codec.encode RequestCodecs.authorityRequest value
