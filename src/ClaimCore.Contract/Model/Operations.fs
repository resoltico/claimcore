namespace ClaimCore.Contract

module OperationSubjects =
    [<Literal>]
    let Case = "CASE"

    [<Literal>]
    let Authority = "AUTHORITY"

[<RequireQualifiedAccess>]
type OperationInput =
    | Case of CaseRequest
    | Authority of ClaimCore.Contract.AuthorityRequest

module OperationInput =
    let operationId =
        function
        | OperationInput.Case request -> request.OperationId
        | OperationInput.Authority request -> request.OperationId
