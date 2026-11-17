namespace ClaimCore.Domain.Tasks

open System
open ClaimCore.Domain
open ClaimCore.Domain.Inputs

module internal PersonInputs =
    let private textId input target (values: InputMap) =
        match Map.tryFind input values.Values with
        | Some(InputValue.Text value) -> Ok value
        | _ -> Error(Refusal.invalidInput (target, Violation.TextRequired))

    let principal inputs =
        let kind =
            Parsing.text InputTarget.PrincipalKind inputs
            |> Result.bind (fun raw ->
                Vocabulary.tryParse PrincipalKind.vocabulary raw
                |> Option.map Ok
                |> Option.defaultValue (
                    Error(
                        Refusal.invalidInput (InputTarget.PrincipalKind, Violation.PrincipalFormat)
                    )
                ))

        Admission.map2
            (fun kind (issuer, subject) -> kind, issuer, subject)
            kind
            (Admission.map2
                (fun issuer subject -> issuer, subject)
                (Parsing.text InputTarget.PrincipalIssuer inputs)
                (Parsing.text InputTarget.PrincipalSubject inputs))
        |> Result.bind (fun (kind, issuer, subject) -> PrincipalKey.ofKind kind issuer subject)

    let profile inputs =
        Admission.map2
            (fun name contact ->
                {
                    ActorProfileInput.DisplayName = name
                    Contact = contact
                })
            (Parsing.text InputTarget.ProfileDisplayName inputs)
            (Parsing.optionalText InputTarget.ProfileContact inputs)
        |> Result.bind (fun input ->
            ActorProfile.restore input
            |> Result.mapError (fun issues ->
                issues
                |> List.map (fun (field, violation) ->
                    {
                        InputIssue.Target =
                            if field = ActorProfileField.DisplayName then
                                InputTarget.ProfileDisplayName
                            else
                                InputTarget.ProfileContact
                        Violation = violation
                        GrantRow = None
                        RelatedDate = None
                    })
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput))

    let admitProfile (context: TaskContext) profile =
        if context.Decision.Lifetime = DecisionLifetime.Recorded then
            Ok profile
        else
            ActorProfile.admit profile
            |> Result.mapError (fun issues ->
                issues
                |> List.map (fun (field, violation) ->
                    {
                        InputIssue.Target =
                            if field = ActorProfileField.DisplayName then
                                InputTarget.ProfileDisplayName
                            else
                                InputTarget.ProfileContact
                        Violation = violation
                        GrantRow = None
                        RelatedDate = None
                    })
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput)

    let grant (inputs: InputMap) =
        let role =
            Parsing.text InputTarget.Role inputs
            |> Result.bind (fun raw ->
                Role.all
                |> List.tryFind (fun role -> Role.token role = raw)
                |> Option.map Ok
                |> Option.defaultValue (
                    Error(Refusal.invalidInput (InputTarget.Role, Violation.TextRequired))
                ))

        let scope =
            textId TaskInputId.Scope InputTarget.Scope inputs
            |> Result.bind (fun scope ->
                let reference =
                    Map.tryFind (Parsing.field BusinessField.CaseReference) inputs.Values

                let id = Map.tryFind TaskInputId.CaseId inputs.Values

                let invalid =
                    Error(
                        Refusal.invalidInput (
                            InputTarget.Field BusinessField.CaseReference,
                            Violation.CaseSelectionInvalid
                        )
                    )

                match scope, reference, id with
                | "INSTALLATION", None, None -> Ok GrantTarget.Installation
                | "CASE", Some(InputValue.Text reference), None ->
                    CaseReference.parse reference
                    |> Parsing.at (InputTarget.Field BusinessField.CaseReference)
                    |> Result.map (fun _ -> GrantTarget.CaseReference reference)
                | "CASE", None, Some(InputValue.Text id) ->
                    match Guid.TryParseExact(id, "D") with
                    | true, id ->
                        CaseId.create id
                        |> Option.map (GrantTarget.CaseId >> Ok)
                        |> Option.defaultValue (
                            Error(
                                Refusal.invalidInput (InputTarget.CaseId, Violation.CaseIdRequired)
                            )
                        )
                    | _ ->
                        Error(Refusal.invalidInput (InputTarget.CaseId, Violation.CaseIdRequired))
                | _ -> invalid)

        Admission.map2
            (fun role scope ->
                {
                    GrantInput.Role = role
                    Scope = scope
                })
            role
            scope

    let grants minimum (inputs: InputMap) =
        let rows =
            Map.tryFind TaskCollectionId.PeopleGrants inputs.Collections
            |> Option.defaultValue []

        if rows.Length < minimum then
            Error(
                Refusal.invalidInput (
                    InputTarget.Grants,
                    Violation.GrantCountRange PeopleAdmission.MaximumGrants
                )
            )
        else
            rows
            |> List.map (fun row ->
                let input = InputMap.create row.Values Map.empty |> Option.get

                grant input
                |> Result.mapError (function
                    | Refusal.InvalidInput issues ->
                        Refusal.InvalidInput(
                            InputIssues.atGrantRow
                                (GrantOrdinal.create row.Ordinal |> Option.get)
                                issues
                        )
                    | refusal -> refusal))
            |> Parsing.collect
