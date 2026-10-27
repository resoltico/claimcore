namespace ClaimCore.Domain

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InputTarget =
    let private definitions =
        (CaseFields.declarations
         |> List.filter (fun row -> row.Field <> BusinessField.Status)
         |> List.map (fun row -> InputTarget.Field row.Field, row.Key))
        @ [
            InputTarget.OperationId, "operationId"
            InputTarget.ExpectedRevision, "expectedRevision"
            InputTarget.Group RecordGroup.Payment, "payment"

            InputTarget.Reason, "reason"
            InputTarget.PrincipalIssuer, "principal.issuer"
            InputTarget.PrincipalSubject, "principal.subject"
            InputTarget.Role, "role"
            InputTarget.Limit, "limit"
            InputTarget.Cursor, "cursor"
        ]

    let vocabulary =
        Vocabulary.define "field" (List.map fst definitions) (function
            | InputTarget.Field field -> BusinessField.key field
            | InputTarget.OperationId -> "operationId"
            | InputTarget.ExpectedRevision -> "expectedRevision"
            | InputTarget.Group group -> RecordGroup.token group
            | InputTarget.Reason -> "reason"
            | InputTarget.PrincipalIssuer -> "principal.issuer"
            | InputTarget.PrincipalSubject -> "principal.subject"
            | InputTarget.Role -> "role"
            | InputTarget.Limit -> "limit"
            | InputTarget.Cursor -> "cursor")

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary
