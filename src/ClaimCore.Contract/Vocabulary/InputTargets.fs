namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type InputTarget =
    | Field of BusinessField
    | OperationId
    | ExpectedRevision
    | Group of RecordGroup
    | Reason
    | PrincipalIssuer
    | PrincipalSubject
    | Limit
    | Cursor

module InputTargets =
    let private definitions =
        (FieldTable.rows
         |> List.filter (fun row -> row.Field <> BusinessField.Status)
         |> List.map (fun row -> InputTarget.Field row.Field, row.Key))
        @ (Vocabulary.entries RecordGroup.vocabulary
           |> List.map (fun (group, token) -> InputTarget.Group group, token))
        @ [
            InputTarget.OperationId, "operationId"
            InputTarget.ExpectedRevision, "expectedRevision"

            InputTarget.Reason, "reason"
            InputTarget.PrincipalIssuer, "principal.issuer"
            InputTarget.PrincipalSubject, "principal.subject"
            InputTarget.Limit, "limit"
            InputTarget.Cursor, "cursor"
        ]

    let vocabulary = Vocabulary.define "field" definitions
    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary
