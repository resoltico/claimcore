namespace ClaimCore.Wire

[<RequireQualifiedAccess>]
type Endpoint =
    | Session
    | SessionLogout
    | Context
    | CaseGet
    | CaseList
    | CaseHistory
    | CasePreview
    | CaseExecute
    | OperationObserve
    | AuthorityList
    | AuthorityGrants
    | AuthorityExecute

[<RequireQualifiedAccess>]
type ReceiptScope =
    | Case
    | Authority

module Endpoint =
    let all =
        [
            Endpoint.Session
            Endpoint.SessionLogout
            Endpoint.Context
            Endpoint.CaseGet
            Endpoint.CaseList
            Endpoint.CaseHistory
            Endpoint.CasePreview
            Endpoint.CaseExecute
            Endpoint.OperationObserve
            Endpoint.AuthorityList
            Endpoint.AuthorityGrants
            Endpoint.AuthorityExecute
        ]

    let id =
        function
        | Endpoint.Session -> "session"
        | Endpoint.SessionLogout -> "session.logout"
        | Endpoint.Context -> "context"
        | Endpoint.CaseGet -> "case.get"
        | Endpoint.CaseList -> "case.list"
        | Endpoint.CaseHistory -> "case.history"
        | Endpoint.CasePreview -> "case.preview"
        | Endpoint.CaseExecute -> "case.execute"
        | Endpoint.OperationObserve -> "operation.observe"
        | Endpoint.AuthorityList -> "authority.list"
        | Endpoint.AuthorityGrants -> "authority.grants"
        | Endpoint.AuthorityExecute -> "authority.execute"

    let tryParse value = all |> List.tryFind (id >> (=) value)

    let method =
        function
        | Endpoint.Session -> "GET"
        | _ -> "POST"

    let path endpoint = "/api/" + id endpoint

    let mutates =
        function
        | Endpoint.CaseExecute
        | Endpoint.AuthorityExecute -> true
        | _ -> false

    let receiptScope =
        function
        | Endpoint.CasePreview
        | Endpoint.CaseExecute -> Some ReceiptScope.Case
        | Endpoint.AuthorityExecute -> Some ReceiptScope.Authority
        | _ -> None

module Limits =
    [<Literal>]
    let MaximumRequestBytes = 65536

    [<Literal>]
    let MaximumInvocationBytes = 131072

    [<Literal>]
    let MaximumResponseBytes = 4194304

    [<Literal>]
    let MaximumPageSize = 50
