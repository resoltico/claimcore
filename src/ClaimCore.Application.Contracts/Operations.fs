namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


[<RequireQualifiedAccess>]
type OperationInput =
    | Case of CaseOperationRequest
    | Authority of ClaimCore.Domain.AuthorityRequest

module OperationInput =
    let operationId =
        function
        | OperationInput.Case request -> request.OperationId
        | OperationInput.Authority request -> request.OperationId
