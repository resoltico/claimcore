namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type InputTarget =
    | Field of BusinessField
    | OperationId
    | ExpectedRevision
    | PaymentGroup
    | Reason
    | PrincipalIssuer
    | PrincipalSubject
    | Limit
    | Cursor

module InputTargets =
    let all =
        (FieldTable.rows
         |> List.filter (fun row -> row.Group <> FieldGroup.Status)
         |> List.map (fun row -> InputTarget.Field row.Field, row.Key))
        @ [
            InputTarget.OperationId, "operationId"
            InputTarget.ExpectedRevision, "expectedRevision"
            InputTarget.PaymentGroup, "payment"
            InputTarget.Reason, "reason"
            InputTarget.PrincipalIssuer, "principal.issuer"
            InputTarget.PrincipalSubject, "principal.subject"
            InputTarget.Limit, "limit"
            InputTarget.Cursor, "cursor"
        ]

    let token =
        function
        | InputTarget.Field field -> BusinessField.token field
        | target -> all |> List.find (fst >> (=) target) |> snd
