namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type GuidanceArgument =
    | Count of int
    | Code of string

type ScalarGuidance =
    {
        Key: string
        Arguments: Map<string, GuidanceArgument>
    }

/// Guidance combines core wording with Domain-owned scalar bounds; hosts only format arguments.
module ScalarGuidance =
    let private message key arguments =
        {
            Key = key
            Arguments = Map.ofList arguments
        }

    let private maximum descriptor =
        descriptor.MaximumCharacters
        |> Option.defaultWith (fun () ->
            invalidOp "Text guidance requires its Domain admission bound.")

    let private scalarForField field =
        match (BusinessField.describe field).Kind with
        | FieldValueKind.ActorId -> Some(message "scalar.actorId.guidance" [])
        | FieldValueKind.PlannedDate -> Some(message "scalar.plannedDate.guidance" [])
        | FieldValueKind.CalendarDate -> Some(message "scalar.calendarDate.guidance" [])
        | FieldValueKind.Amount ->
            Some(
                message
                    "scalar.amount.guidance"
                    [
                        "maximumIntegerDigits",
                        GuidanceArgument.Count Money.descriptor.Amount.MaximumIntegerDigits
                        "maximumFractionDigits",
                        GuidanceArgument.Count Money.descriptor.Amount.MaximumFractionalDigits
                    ]
            )
        | FieldValueKind.Currency ->
            Some(
                message
                    "scalar.currency.guidance"
                    [ "length", GuidanceArgument.Count Money.descriptor.Currency.ExactCharacters ]
            )
        | FieldValueKind.Country ->
            Some(
                message
                    "scalar.country.guidance"
                    [
                        "unknownCode",
                        GuidanceArgument.Code(
                            CountryCode.descriptor.Format.UnknownCode
                            |> Option.defaultWith (fun () ->
                                invalidOp "Country guidance requires the Domain unknown code.")
                        )
                    ]
            )
        // The reference descriptor publishes its retained grammar; the owning scalar module supplies its bound.
        | FieldValueKind.Reference ->
            Some(
                message
                    "scalar.reference.guidance"
                    [ "maximumCharacters", GuidanceArgument.Count CaseReference.MaximumCharacters ]
            )
        | FieldValueKind.HandlingSummary ->
            Some(
                message
                    "scalar.narrative.guidance"
                    [ "maximumScalars", GuidanceArgument.Count HandlingSummary.MaximumScalars ]
            )
        | FieldValueKind.IncidentDescription ->
            Some(
                message
                    "scalar.narrative.guidance"
                    [ "maximumScalars", GuidanceArgument.Count IncidentDescription.MaximumScalars ]
            )
        | FieldValueKind.Identifier ->
            Some(
                message
                    "scalar.text.guidance"
                    [ "maximumCharacters", GuidanceArgument.Count Identifier.MaximumCharacters ]
            )
        | FieldValueKind.PartyName ->
            Some(
                message
                    "scalar.text.guidance"
                    [ "maximumCharacters", GuidanceArgument.Count(maximum PartyName.descriptor) ]
            )
        | FieldValueKind.Status -> None

    let forField field =
        let guidance = scalarForField field |> Option.defaultValue (message "" [])

        Some
            { guidance with
                Key = FieldPresentation.ruleId field
            }

    let forReason =
        message
            (TaskInputPresentation.messageKey (TaskInputId.Target InputTarget.Reason) "rule")
            [ "maximumCharacters", GuidanceArgument.Count(maximum Reason.descriptor) ]

    let forSearchText =
        message
            (TaskInputPresentation.messageKey (TaskInputId.Target InputTarget.Text) "rule")
            [ "maximumCharacters", GuidanceArgument.Count CaseSearchText.MaximumCharacters ]

    let forProfileName =
        message
            (TaskInputPresentation.messageKey
                (TaskInputId.Target InputTarget.ProfileDisplayName)
                "rule")
            [ "maximumCharacters", GuidanceArgument.Count(maximum PartyName.descriptor) ]

    let forProfileContact =
        message
            (TaskInputPresentation.messageKey (TaskInputId.Target InputTarget.ProfileContact) "rule")
            [ "maximumCharacters", GuidanceArgument.Count(maximum ContactText.descriptor) ]

    let forInput input =
        let key = TaskInputPresentation.messageKey input "rule"

        match input with
        | TaskInputId.Target(InputTarget.Field field) -> forField field |> Option.get
        | TaskInputId.Target InputTarget.Reason -> forReason
        | TaskInputId.Target InputTarget.Text -> forSearchText
        | TaskInputId.Target InputTarget.DirectorySearch ->
            message
                key
                [
                    "maximumCharacters",
                    GuidanceArgument.Count DirectorySearchText.MaximumCharacters
                ]
        | TaskInputId.Target InputTarget.ProfileDisplayName -> forProfileName
        | TaskInputId.Target InputTarget.ProfileContact -> forProfileContact
        | TaskInputId.Target InputTarget.NoteText ->
            message key [ "maximumScalars", GuidanceArgument.Count NoteText.MaximumScalars ]
        | TaskInputId.Target InputTarget.ReminderAction ->
            message key [ "maximumScalars", GuidanceArgument.Count ReminderAction.MaximumScalars ]
        | TaskInputId.Target InputTarget.ReminderDueDate -> message key []
        | TaskInputId.Target InputTarget.Grants ->
            message key [ "maximumGrants", GuidanceArgument.Count AuthorityAdmission.MaximumGrants ]
        | _ -> message key []

    let byInput =
        (CaseFields.declarations
         |> List.map (fun field -> TaskInputId.Target(InputTarget.Field field.Field)))
        @ TaskInputId.technical
        @ (Vocabulary.all RecordGroup.vocabulary
           |> List.map (InputTarget.Group >> TaskInputId.Target))
        |> List.map (fun input -> TaskInputId.key input, forInput input)
        |> List.distinctBy fst

    let all = byInput |> List.map snd |> List.distinctBy _.Key
