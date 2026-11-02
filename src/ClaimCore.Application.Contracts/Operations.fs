namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


[<RequireQualifiedAccess>]
type OperationInput =
    | Case of CaseOperationRequest
    | Work of WorkOperationRequest
    | Reminder of ReminderOperationRequest
    | Authority of ClaimCore.Domain.AuthorityRequest

module OperationInput =
    let operationId =
        function
        | OperationInput.Case request -> request.OperationId
        | OperationInput.Authority request -> request.OperationId
        | OperationInput.Work request -> request.OperationId
        | OperationInput.Reminder request -> request.OperationId
