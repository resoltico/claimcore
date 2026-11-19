namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ScreenEntries

/// Only part wording adapts the Domain's authoritative differences to display values.
module ReviewScreen =
    let flatten sections =
        sections |> List.collect (fun (section: Section) -> section.Entries)

    let private changed key label format (change: ValueChange<'value>) =
        {
            PresentedChange.Key = key
            LabelId = label
            Before = optional format change.Before
            After = optional format change.After
        }

    let private actor references id =
        references.Actors
        |> List.tryFind (fun actor -> actor.ActorId = id)
        |> Option.map PresentedValue.Actor
        |> Option.defaultWith (fun () ->
            invalidOp "A changed account has no authorized current label.")

    let private fieldValues (fields: CaseFields option) =
        let values =
            fields
            |> Option.map (fun fields ->
                FieldPresentation.declarations
                |> List.map (fun declaration ->
                    declaration.Field, CaseFields.typedValue declaration.Field fields)
                |> Map.ofList)

        entries (ScreenEntries.fields FieldPresentation.declarations id) values
        |> List.filter (fun entry ->
            match entry.Value with
            | PresentedValue.Absent _ -> false
            | _ -> true)

    let describePart references (partId: System.Guid) description =
        let prefix = partId.ToString("D") + "."

        match description with
        | PartDescription.Case value ->
            let fields =
                value.Fields
                |> List.map (fun field ->
                    changed
                        (prefix + BusinessField.key field.FieldName)
                        (FieldPresentation.labelId field.FieldName)
                        scalar
                        {
                            Before = field.Before
                            After = field.After
                        })

            let money =
                value.Money
                |> List.map (fun field ->
                    changed
                        (prefix + BusinessField.key field.FieldName)
                        (FieldPresentation.labelId field.FieldName)
                        PresentedValue.money
                        field.Change)

            let lifecycle =
                value.Lifecycle
                |> Option.map (
                    changed
                        (prefix + "lifecycle")
                        "input.lifecycle.label"
                        (code Lifecycle.vocabulary)
                )
                |> Option.toList

            let after = fieldValues value.Values.After

            let changedFields =
                (value.Fields |> List.map _.FieldName) @ (value.Money |> List.map _.FieldName)
                |> List.map BusinessField.key
                |> Set.ofList

            let unchanged =
                if value.Values.Before.IsNone || value.Values.After.IsNone then
                    []
                else
                    after |> List.filter (fun entry -> not (Set.contains entry.Key changedFields))

            let removals =
                fields @ money
                |> List.map (fun change ->
                    if value.Lifecycle |> Option.bind _.After = Some Lifecycle.Erased then
                        match change.After with
                        | PresentedValue.Absent _ ->
                            { change with
                                After = PresentedValue.Absent "lifecycle.ERASED.label"
                            }
                        | _ -> change
                    else
                        change)

            let reason =
                value.Disposition
                |> Option.map (fun disposition ->
                    let reason = CaseDisposition.reason disposition

                    changed
                        (prefix + "reason")
                        "field.reason.label"
                        (Reason.format >> PresentedValue.Narrative)
                        { Before = None; After = Some reason })
                |> Option.toList

            removals @ lifecycle @ reason, unchanged
        | PartDescription.Payment value ->
            let removed = value.Removed |> Option.bind _.After = Some true

            let entry =
                value.Entry
                |> Option.toList
                |> List.collect (fun entry ->
                    let pair item =
                        if removed then
                            { Before = Some item; After = None }
                        else
                            { Before = None; After = Some item }

                    [
                        changed
                            (prefix + "date")
                            "field.paymentDate.label"
                            PresentedValue.Date
                            (pair (PaymentEntry.date entry))
                        changed
                            (prefix + "amount")
                            "field.paidAmount.label"
                            PresentedValue.money
                            (pair (PaymentEntry.money entry))
                        changed
                            (prefix + "direction")
                            "field.direction.label"
                            (code PaymentDirection.vocabulary)
                            (pair (PaymentEntry.direction entry))
                        match PaymentEntry.reference entry with
                        | Some reference ->
                            changed
                                (prefix + "reference")
                                "field.reference.label"
                                (Identifier.format >> PresentedValue.Identifier)
                                (pair reference)
                        | None -> ()
                        match PaymentEntry.payee entry with
                        | Some payee ->
                            changed
                                (prefix + "payee")
                                "field.payee.label"
                                (PartyId.format >> PresentedValue.Identifier)
                                (pair payee)
                        | None -> ()
                    ])

            let balances =
                if value.Totals.Before = value.Totals.After then
                    []
                else
                    [
                        changed
                            (prefix + "netPaid")
                            "label.netPaid"
                            PaymentsScreen.netPaid
                            value.Totals
                    ]

            entry @ balances, []
        | PartDescription.Work value ->
            let differences =
                [
                    value.Responsible
                    |> Option.map (
                        changed
                            (prefix + "responsible")
                            "field.responsibleActor.label"
                            (actor references)
                    )
                    value.Summary
                    |> Option.map (
                        changed
                            (prefix + "summary")
                            "field.handlingSummary.label"
                            (HandlingSummary.format >> PresentedValue.Narrative)
                    )
                ]
                |> List.choose id

            let unchanged =
                [
                    if value.Responsible.IsNone then
                        match value.Values.After |> Option.bind _.Responsible with
                        | Some id ->
                            ScreenEntries.value
                                "responsible"
                                "field.responsibleActor.label"
                                Prominence.Primary
                                (actor references id)
                        | None -> ()
                    if value.Summary.IsNone then
                        match value.Values.After |> Option.bind _.Summary with
                        | Some text ->
                            ScreenEntries.value
                                "summary"
                                "field.handlingSummary.label"
                                Prominence.Primary
                                (PresentedValue.Narrative(HandlingSummary.format text))
                        | None -> ()
                ]

            differences, unchanged
        | PartDescription.Reminder value ->
            let differences =
                [
                    value.Action
                    |> Option.map (
                        changed
                            (prefix + "action")
                            "field.reminderAction.label"
                            (ReminderAction.format >> PresentedValue.Text)
                    )
                    value.Due
                    |> Option.map (
                        changed (prefix + "due") "field.reminderDueDate.label" PresentedValue.Date
                    )
                    value.Kind
                    |> Option.map (
                        changed (prefix + "kind") "field.kind.label" (code ReminderKind.vocabulary)
                    )
                    value.DoneOn
                    |> Option.map (
                        changed (prefix + "doneOn") "field.doneOn.label" PresentedValue.Date
                    )
                    value.State
                    |> Option.map (
                        changed
                            (prefix + "state")
                            "input.reminderState.label"
                            (code ReminderState.vocabulary)
                    )
                ]
                |> List.choose id

            let unchanged =
                [
                    if value.Action.IsNone then
                        match value.Plans.After with
                        | Some plan ->
                            ScreenEntries.value
                                "action"
                                "field.reminderAction.label"
                                Prominence.Primary
                                (PresentedValue.Text(ReminderAction.format plan.Action))
                        | None -> ()
                    if value.Due.IsNone then
                        match value.Plans.After with
                        | Some plan ->
                            ScreenEntries.value
                                "due"
                                "field.reminderDueDate.label"
                                Prominence.Primary
                                (PresentedValue.Date plan.Due)
                        | None -> ()
                ]

            differences, unchanged
        | PartDescription.Party part ->
            let difference key label format select =
                changed
                    (prefix + key)
                    label
                    format
                    {
                        Before = part.Content.Before |> Option.bind select
                        After = part.Content.After |> Option.bind select
                    }

            [
                difference "role" "field.partyRole.label" (code PartyRole.vocabulary) (fun party ->
                    Some party.Role)
                difference "name" "field.partyName.label" PresentedValue.Party _.Name
                difference
                    "details"
                    "field.partyDetails.label"
                    (PartyDetails.format >> PresentedValue.Narrative)
                    _.Details
            ],
            []
        | PartDescription.Link link ->
            [
                changed
                    (prefix + "target")
                    "field.relatedCaseId.label"
                    (fun id -> PresentedValue.Identifier(CaseId.format id))
                    {
                        Before = link.Before |> Option.map _.Target
                        After = link.After |> Option.map _.Target
                    }
                if (link.Before |> Option.bind _.Note) <> (link.After |> Option.bind _.Note) then
                    changed
                        (prefix + "note")
                        "field.linkNote.label"
                        (NoteText.format >> PresentedValue.Narrative)
                        {
                            Before = link.Before |> Option.bind _.Note
                            After = link.After |> Option.bind _.Note
                        }
            ],
            []
        | PartDescription.Attachment attachment ->
            let changes =
                attachment.Display
                |> Option.map (fun display ->
                    [
                        changed
                            (prefix + "name")
                            "field.attachmentName.label"
                            (AttachmentName.value >> PresentedValue.Narrative)
                            {
                                Before = None
                                After = Some display.Name
                            }
                        changed
                            (prefix + "length")
                            "field.attachmentLength.label"
                            PresentedValue.Count
                            {
                                Before = None
                                After = Some display.Length
                            }
                    ])
                |> Option.defaultValue []

            changes, []
        | PartDescription.Note value ->
            [
                value.Text
                |> Option.map (
                    changed
                        (prefix + "text")
                        "field.noteText.label"
                        (NoteText.format >> PresentedValue.Narrative)
                )
                value.Removed
                |> Option.map (changed (prefix + "removed") "change.NOTE.removed" boolean)
            ]
            |> List.choose id,
            []
        | PartDescription.Person value ->
            let changes =
                [
                    value.Enabled
                    |> Option.map (changed (prefix + "enabled") "label.enabled" boolean)
                    value.ProfilePresent
                    |> Option.map (changed (prefix + "profile") "label.profile" boolean)
                    value.DisplayName
                    |> Option.map (
                        changed
                            (prefix + "name")
                            "field.profile.displayName.label"
                            PresentedValue.Party
                    )
                    value.Contact
                    |> Option.map (
                        changed
                            (prefix + "contact")
                            "field.profile.contact.label"
                            (ContactText.format >> PresentedValue.Text)
                    )
                    value.DigestEmail
                    |> Option.map (
                        changed
                            (prefix + "digestEmail")
                            "field.profile.digestEmail.label"
                            (EmailAddress.format >> PresentedValue.Text)
                    )
                ]
                |> List.choose id

            let principal =
                value.Principal
                |> Option.toList
                |> List.collect (fun principal ->
                    let identityFact select format key label =
                        changed
                            (prefix + "principal." + key)
                            label
                            format
                            {
                                Before = principal.Before |> Option.map select
                                After = principal.After |> Option.map select
                            }

                    [
                        identityFact
                            (fun value -> let kind, _, _ = PrincipalKey.parts value in kind)
                            (code PrincipalKind.vocabulary)
                            "kind"
                            "field.principal.kind.label"
                        identityFact
                            (fun value -> let _, issuer, _ = PrincipalKey.parts value in issuer)
                            PresentedValue.Identifier
                            "issuer"
                            "field.principal.issuer.label"
                        identityFact
                            (fun value -> let _, _, subject = PrincipalKey.parts value in subject)
                            PresentedValue.Identifier
                            "subject"
                            "field.principal.subject.label"
                    ])

            let unchanged =
                [
                    match value.Profiles.After with
                    | Some profile ->
                        if value.DisplayName.IsNone then
                            ScreenEntries.value
                                "name"
                                "field.profile.displayName.label"
                                Prominence.Primary
                                (PresentedValue.Party profile.DisplayName)

                        if value.Contact.IsNone then
                            match profile.Contact with
                            | Some contact ->
                                ScreenEntries.value
                                    "contact"
                                    "field.profile.contact.label"
                                    Prominence.Secondary
                                    (PresentedValue.Text(ContactText.format contact))
                            | None -> ()
                    | None -> ()
                ]

            let grants =
                value.Grants
                |> List.mapi (fun index grant ->
                    let scope =
                        match grant.Grant.Scope with
                        | Scope.Installation -> code ScopeKind.vocabulary ScopeKind.Installation
                        | Scope.Case id ->
                            references.Cases
                            |> List.tryFind (fun value -> value.CaseId = id)
                            |> Option.map (fun value -> PresentedValue.Reference value.Reference)
                            |> Option.defaultValue (PresentedValue.Identifier(CaseId.format id))

                    changed
                        (prefix + "role." + string index)
                        ("role." + Role.token grant.Grant.Role + ".label")
                        (fun active -> if active then scope else absent)
                        {
                            Before = Some grant.Before
                            After = Some grant.After
                        })

            principal @ changes @ grants, unchanged

    let operationReview (review: OperationReview) =
        let parts =
            review.Changes
            |> List.map (fun part -> describePart review.References part.PartId part.Description)

        let changes = parts |> List.collect fst
        let unchanged = parts |> List.collect snd

        let effects =
            changes
            |> List.map (fun change ->
                section
                    change.LabelId
                    [
                        value
                            (change.Key + ".before")
                            "label.before"
                            Prominence.Secondary
                            change.Before
                        value (change.Key + ".after") "label.after" Prominence.Primary change.After
                    ])

        {
            ReviewDescription.Changes = changes
            Unchanged = unchanged
            Effects = effects
            Basis = review.Context.Basis
        }
