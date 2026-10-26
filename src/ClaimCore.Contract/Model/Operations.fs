namespace ClaimCore.Contract

open ClaimCore.Domain

module OperationSubjects =
    [<Literal>]
    let Case = "CASE"

    [<Literal>]
    let Authority = "AUTHORITY"

[<RequireQualifiedAccess>]
type OperationInput =
    | Case of UnvalidatedCaseRequest
    | Authority of ClaimCore.Domain.AuthorityRequest

module OperationInput =
    let operationId =
        function
        | OperationInput.Case request -> request.OperationId
        | OperationInput.Authority request -> request.OperationId
