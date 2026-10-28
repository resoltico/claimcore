namespace ClaimCore.Domain

open Microsoft.FSharp.Reflection

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InputTarget =
    // A total table maps each closed input target to its boundary identity.
    // fsharplint:disable-next-line CyclomaticComplexity
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
        | InputTarget.Text -> "text"

    let private values =
        let technical =
            FSharpType.GetUnionCases typeof<InputTarget>
            |> Array.filter (fun case -> case.GetFields().Length = 0)
            |> Array.map (fun case ->
                (FSharpValue.MakeUnion(case, [||]) |> nonNull) :?> InputTarget)
            |> Array.toList

        (CaseFields.declarations
         |> List.filter _.IsAuthored
         |> List.map (fun row -> InputTarget.Field row.Field))
        @ [ InputTarget.Group RecordGroup.Payment ]
        @ technical

    let vocabulary = Vocabulary.define "field" values token
    let all = values |> List.map (fun value -> value, token value)

    let private orderedTargets =
        [
            yield InputTarget.OperationId
            yield InputTarget.Field BusinessField.CaseReference
            yield InputTarget.ExpectedRevision
            for group in Vocabulary.all RecordGroup.vocabulary do
                yield InputTarget.Group group
                yield! CaseFields.ofGroup group |> List.map InputTarget.Field
            yield InputTarget.Reason
        ]

    let order target =
        orderedTargets
        |> List.tryFindIndex ((=) target)
        |> Option.defaultValue orderedTargets.Length
