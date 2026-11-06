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

    /// Offline checks cover authored values only; the service owns business-date and state admission.
    let syntax input =
        let reference =
            function
            | CaseSelector.Reference raw -> Some raw
            | CaseSelector.Id _ -> None

        let authored, target =
            match input with
            | OperationInput.Case request ->
                AuthoredInput.Case(
                    request.OperationId,
                    reference request.Target,
                    request.ExpectedRevision,
                    request.Act
                ),
                None
            | OperationInput.Work request ->
                let (WorkAct.UpdateWork update) = request.Act

                AuthoredInput.Work(request.OperationId, request.ExpectedRevision, update),
                reference request.Target
            | OperationInput.Reminder request ->
                AuthoredInput.Reminder(request.OperationId, request.Act), reference request.Target
            | OperationInput.Authority request -> AuthoredInput.Authority request, None

        let issues =
            Validation.syntax authored
            |> Option.map InputIssues.toList
            |> Option.defaultValue []

        let targetIssues =
            target
            |> Option.toList
            |> List.choose (fun raw ->
                match CaseReference.parse raw with
                | Ok _ -> None
                | Error violation ->
                    Some
                        {
                            Target = InputTarget.Field BusinessField.CaseReference
                            Violation = violation
                            RelatedDate = None
                        })

        issues @ targetIssues
        |> List.sortBy (fun issue -> InputTarget.order issue.Target)
        |> InputIssues.ofList
