namespace ClaimCore.Contract

module OperationScopes =
    [<Literal>]
    let Case = "CASE"

    [<Literal>]
    let Authority = "AUTHORITY"

[<RequireQualifiedAccess>]
type OperationInput =
    | Case of CaseRequest
    | Authority of ClaimCore.Contract.Protocol.AuthorityRequest

module OperationInput =
    let operationId =
        function
        | OperationInput.Case request -> request.OperationId
        | OperationInput.Authority request -> request.OperationId
