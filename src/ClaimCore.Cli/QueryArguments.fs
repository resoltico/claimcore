namespace ClaimCore.Cli

open System
open System.Collections.Generic
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json

/// Read commands acquire supplied identities and construct their declared typed query directly.
module internal QueryArguments =
    let build
        query
        (options: Dictionary<string, string option list>)
        operands
        (problems: ResizeArray<UsageError>)
        =
        let flag input =
            "--" + CommandGrammar.kebab (CommandGrammar.inputName input)

        let target input = TaskInputId.Target input
        let field input = target (InputTarget.Field input)
        let used = HashSet<string>()

        let values input =
            let name = flag input
            used.Add name |> ignore

            match options.TryGetValue name with
            | true, values -> values |> List.rev |> List.choose id
            | _ -> []

        let raw input = values input |> List.tryHead

        let read input (codec: Codec<'a>) (value: string) =
            match codec.Read "$" (JsonValue.String value) with
            | Ok value -> Some value
            | Error _ ->
                problems.Add(UsageError.InvalidValue(flag input, value))
                None

        let text input maximum =
            raw input |> Option.bind (read input (Codec.boundedText 1 maximum))

        let uuid input (value: string) =
            match Guid.TryParseExact(value, "D") with
            | true, value -> Some value
            | _ ->
                problems.Add(UsageError.InvalidChoice(flag input, value, []))
                None

        let choice input vocabulary value =
            Vocabulary.all vocabulary
            |> List.tryFind (fun item ->
                let token = Vocabulary.token vocabulary item in
                value = token || value = CommandGrammar.kebab token)
            |> Option.orElseWith (fun () ->
                problems.Add(
                    UsageError.InvalidChoice(
                        flag input,
                        value,
                        Vocabulary.all vocabulary
                        |> List.map (Vocabulary.token vocabulary >> CommandGrammar.kebab)
                    )
                )

                None)

        let optionalChoice input vocabulary =
            raw input |> Option.bind (choice input vocabulary)

        let choices input vocabulary =
            values input |> List.choose (choice input vocabulary) |> List.distinct

        let cursor maximum =
            text (target InputTarget.Cursor) maximum

        let limit maximum fallback =
            let input = target InputTarget.Limit

            match raw input with
            | None -> fallback
            | Some value ->
                match
                    Int32.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture
                    )
                with
                | true, value when value >= 1 && value <= maximum -> value
                | _ ->
                    problems.Add(UsageError.InvalidRange(flag input, value, 1, maximum))
                    fallback

        let mutable consumed = 0

        let operand name =
            consumed <- consumed + 1

            match List.tryItem (consumed - 1) operands with
            | Some value -> value
            | None ->
                problems.Add(UsageError.MissingOperand name)
                ""

        let caseTarget () =
            CaseSelector.Reference(operand "CASE-REFERENCE")

        let filter () =
            let result =
                CaseListFilter.create
                    (choices TaskInputId.Lifecycle Lifecycle.vocabulary)
                    (choices TaskInputId.Status CaseStatus.vocabulary)
                    (choices TaskInputId.Progress CaseProgress.vocabulary)
                    (raw (target InputTarget.Text))
                    (optionalChoice TaskInputId.Order CaseListOrder.vocabulary
                     |> Option.defaultValue CaseListOrder.Reference)

            { result with
                Assignment = optionalChoice TaskInputId.Assignment CaseAssignment.vocabulary
                Due = optionalChoice TaskInputId.Due CaseDue.vocabulary
                View = optionalChoice TaskInputId.CaseView CaseView.vocabulary
                ResponsibleActor =
                    raw TaskInputId.ResponsibleActorFilter
                    |> Option.bind (uuid TaskInputId.ResponsibleActorFilter)
                    |> Option.bind (fun id ->
                        match ActorId.create id with
                        | Some value -> Some value
                        | None ->
                            problems.Add(
                                UsageError.InvalidValue(
                                    flag TaskInputId.ResponsibleActorFilter,
                                    ""
                                )
                            )

                            None)
            }

        let typed =
            match query with
            | QueryTaskId.CaseGet -> EndpointValues.input Api.caseGet { Target = caseTarget () }
            | QueryTaskId.CaseAssignees ->
                EndpointValues.input
                    Api.caseAssignees
                    {
                        Target = caseTarget ()
                        AfterCursor = cursor 512
                        Limit = limit Paging.MaximumPageSize Paging.DefaultPageSize
                    }
            | QueryTaskId.CaseNotes ->
                EndpointValues.input
                    Api.caseNotes
                    {
                        Target = caseTarget ()
                        AfterCursor = cursor 512
                        Limit = limit NotePaging.MaximumLimit NotePaging.DefaultLimit
                    }
            | QueryTaskId.CasePayments ->
                EndpointValues.input
                    Api.casePayments
                    {
                        Target = caseTarget ()
                        AfterCursor = cursor 512
                        Limit = limit PaymentPaging.MaximumLimit PaymentPaging.DefaultLimit
                    }
            | QueryTaskId.CaseTimeline ->
                EndpointValues.input
                    Api.caseTimeline
                    {
                        Target = caseTarget ()
                        Filter = optionalChoice TaskInputId.TimelineFilter TimelineFilter.vocabulary
                        AfterCursor = cursor 512
                        Limit = limit Paging.MaximumPageSize Paging.DefaultPageSize
                    }
            | QueryTaskId.CaseList ->
                EndpointValues.input
                    Api.caseList
                    {
                        Filter = filter ()
                        Page =
                            {
                                AfterCursor = cursor Paging.MaximumCaseCursorCharacters
                                Limit = limit Paging.MaximumPageSize Paging.DefaultPageSize
                            }
                    }
            | QueryTaskId.CaseSummary -> EndpointValues.input Api.caseSummary (filter ())
            | QueryTaskId.ReminderGet ->
                EndpointValues.input
                    Api.reminder
                    {
                        Target = caseTarget ()
                        ReminderId =
                            raw (target InputTarget.ReminderId)
                            |> Option.bind (uuid (target InputTarget.ReminderId))
                            |> Option.defaultWith (fun () ->
                                problems.Add(
                                    UsageError.MissingOption(flag (target InputTarget.ReminderId))
                                )

                                Guid.Empty)
                    }
            | QueryTaskId.ReminderList ->
                let inactive =
                    match raw TaskInputId.IncludeInactive with
                    | None -> None
                    | Some "true" -> Some true
                    | Some "false" -> Some false
                    | Some value ->
                        problems.Add(
                            UsageError.InvalidChoice(
                                flag TaskInputId.IncludeInactive,
                                value,
                                [ "true"; "false" ]
                            )
                        )

                        None

                EndpointValues.input
                    Api.reminders
                    {
                        Target =
                            raw (field BusinessField.CaseReference)
                            |> Option.map CaseSelector.Reference
                        State = optionalChoice TaskInputId.ReminderState ReminderState.vocabulary
                        View = optionalChoice TaskInputId.ReminderView ReminderView.vocabulary
                        Due = optionalChoice TaskInputId.ReminderDue ReminderDue.vocabulary
                        Assignment = optionalChoice TaskInputId.Assignment CaseAssignment.vocabulary
                        Text = raw (target InputTarget.Text)
                        IncludeInactive = inactive
                        AfterCursor = cursor Paging.MaximumCaseCursorCharacters
                        Limit = limit Paging.MaximumPageSize Paging.DefaultPageSize
                    }
            | QueryTaskId.OperationGet ->
                EndpointValues.input
                    Api.operationGet
                    {
                        OperationId =
                            operand "OPERATION-ID"
                            |> uuid (target InputTarget.OperationId)
                            |> Option.defaultValue Guid.Empty
                    }
            | QueryTaskId.AuthorityList ->
                EndpointValues.input
                    Api.authorityList
                    {
                        Role = optionalChoice TaskInputId.RoleFilter Role.vocabulary
                        Text = raw (target InputTarget.DirectorySearch)
                        AfterCursor = cursor Paging.MaximumActorCursorCharacters
                        Limit = limit Paging.MaximumPageSize Paging.DefaultPageSize
                    }
            | QueryTaskId.AuthorityGrants ->
                EndpointValues.input
                    Api.authorityGrants
                    {
                        ActorId =
                            operand "ACTOR-ID"
                            |> uuid TaskInputId.ActorId
                            |> Option.defaultValue Guid.Empty
                        AfterCursor = cursor 512
                        Limit = limit Paging.MaximumPageSize Paging.DefaultPageSize
                    }
            | QueryTaskId.AuthorityHistory ->
                EndpointValues.input
                    Api.authorityHistory
                    {
                        TargetActorId =
                            raw TaskInputId.ActorId |> Option.bind (uuid TaskInputId.ActorId)
                        AfterCursor = cursor 512
                        Limit = limit Paging.MaximumPageSize Paging.DefaultPageSize
                    }
            | QueryTaskId.AuthorityRequests ->
                EndpointValues.input
                    Api.authorityRequests
                    {
                        AfterCursor = cursor Paging.MaximumAccessRequestCursorCharacters
                        Limit = limit Paging.MaximumPageSize Paging.DefaultPageSize
                    }
            | QueryTaskId.AuthorityRequestsDismiss ->
                let required input =
                    raw input
                    |> Option.defaultWith (fun () ->
                        problems.Add(UsageError.MissingOption(flag input))
                        "")

                EndpointValues.input
                    Api.authorityRequestsDismiss
                    {
                        Kind =
                            required (target InputTarget.PrincipalKind)
                            |> choice (target InputTarget.PrincipalKind) PrincipalKind.vocabulary
                            |> Option.defaultValue PrincipalKind.Human
                        Issuer = required (target InputTarget.PrincipalIssuer)
                        Subject = required (target InputTarget.PrincipalSubject)
                    }
            | QueryTaskId.Context -> EndpointValues.input Api.context ()
            | QueryTaskId.WorkViews -> EndpointValues.input Api.workViews ()

        operands
        |> List.skip (min consumed operands.Length)
        |> List.iter (UsageError.ExtraOperand >> problems.Add)

        for name in options.Keys do
            if not (used.Contains name || List.contains name CommandGrammar.globalFlags) then
                problems.Add(UsageError.NotApplicable(name, QueryTaskId.key query))

        typed
