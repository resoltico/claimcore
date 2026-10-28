namespace ClaimCore.Domain

open Microsoft.FSharp.Reflection

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InputTarget =
    let token =
        function
        | InputTarget.Field field -> BusinessField.key field
        | InputTarget.OperationId -> "operationId"
        | InputTarget.ExpectedRevision -> "expectedRevision"
        | InputTarget.Group group -> RecordGroup.token group
        | InputTarget.Reason -> "reason"
        | InputTarget.PrincipalKind -> "principal.kind"
        | InputTarget.PrincipalIssuer -> "principal.issuer"
        | InputTarget.PrincipalSubject -> "principal.subject"
        | InputTarget.Role -> "role"
        | InputTarget.Limit -> "limit"
        | InputTarget.Cursor -> "cursor"

    let private values =
        let technical =
            FSharpType.GetUnionCases typeof<InputTarget>
            |> Array.filter (fun case -> case.GetFields().Length = 0)
            |> Array.map (fun case ->
                (FSharpValue.MakeUnion(case, [||]) |> nonNull) :?> InputTarget)
            |> Array.toList

        (CaseFields.declarations
         |> List.filter (fun row -> row.Field <> BusinessField.Status)
         |> List.map (fun row -> InputTarget.Field row.Field))
        @ [ InputTarget.Group RecordGroup.Payment ]
        @ technical

    let vocabulary = Vocabulary.define "field" values token
    let all = values |> List.map (fun value -> value, token value)
