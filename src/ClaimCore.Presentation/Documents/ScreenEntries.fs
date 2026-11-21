namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts

module ScreenEntries =
    let entries (definitions: EntryDefinition<'reply> list) value =
        definitions
        |> List.map (fun definition ->
            {
                Entry.Key = definition.Key
                Recorded = false
                LabelId = definition.LabelId
                Prominence = definition.Prominence
                Value = definition.Read value
            })

    let absent = PresentedValue.Absent "label.notRecorded"

    let scalar =
        function
        | CaseFieldValue.Reference value -> PresentedValue.Reference value
        | CaseFieldValue.Status value ->
            PresentedValue.Code(Vocabulary.name CaseStatus.vocabulary, CaseStatus.token value)
        | CaseFieldValue.Business(Admitted.Date value) -> PresentedValue.Date value
        | CaseFieldValue.Business(Admitted.Country value) ->
            PresentedValue.Code("country", CountryCode.format value)
        | CaseFieldValue.Business(Admitted.Name value) -> PresentedValue.Party value
        | CaseFieldValue.Business(Admitted.Identifier value) ->
            PresentedValue.Text(Identifier.format value)
        | CaseFieldValue.Business(Admitted.Description value) ->
            PresentedValue.Narrative(IncidentDescription.format value)
        | CaseFieldValue.Business(Admitted.Note value) ->
            PresentedValue.Narrative(NoteText.format value)
        | CaseFieldValue.Business(Admitted.Reason value) ->
            PresentedValue.Narrative(Reason.format value)
        | CaseFieldValue.Business(Admitted.Outcome value) ->
            PresentedValue.Code(
                Vocabulary.name ClosureOutcome.vocabulary,
                ClosureOutcome.token value
            )
        | CaseFieldValue.Business(Admitted.Amount value) -> PresentedValue.Text(Amount.format value)
        | CaseFieldValue.Business(Admitted.Currency value) ->
            PresentedValue.Code("currency", Currency.format value)

    let fields (declarations: FieldDescriptor list) getFields =
        let paired = declarations |> List.choose _.Pairs |> Set.ofList

        declarations
        |> List.filter (fun field -> not (Set.contains field.Field paired))
        |> List.map (fun field ->
            {
                EntryDefinition.Key = field.Key
                LabelId = FieldPresentation.labelId field.Field
                Prominence = FieldPresentation.prominence field.Field
                IsMoney = field.Pairs.IsSome
                Read =
                    fun reply ->
                        let values = getFields reply

                        let value field =
                            values |> Option.bind (Map.tryFind field >> Option.flatten)

                        match field.Pairs with
                        | Some currency ->
                            match value field.Field, value currency with
                            | Some(CaseFieldValue.Business(Admitted.Amount amount)),
                              Some(CaseFieldValue.Business(Admitted.Currency code)) ->
                                PresentedValue.money (Money.create amount code)
                            | None, None -> absent
                            | _ -> invalidOp "A presented money pair must be complete."
                        | None ->
                            value field.Field |> Option.map scalar |> Option.defaultValue absent
            })

    let caseFields declarations getFields =
        fields
            (declarations |> List.filter (fun field -> FieldPresentation.listed field.Field))
            getFields

    let value key label prominence value =
        {
            Entry.Key = key
            Recorded = false
            LabelId = label
            Prominence = prominence
            Value = value
        }

    let partIdentity kind (id: System.Guid) =
        let label =
            match kind with
            | PartKind.Case
            | PartKind.Work -> "input.caseId.label"
            | PartKind.Person -> "input.actorId.label"
            | PartKind.Payment -> "field.paymentId.label"
            | PartKind.Reminder -> "field.reminderId.label"
            | PartKind.Note -> "field.noteId.label"
            | PartKind.Attachment -> "field.attachmentId.label"
            | PartKind.Party -> "field.partyId.label"
            | PartKind.Link -> "field.linkId.label"

        value
            (id.ToString("D") + ".id")
            label
            Prominence.Detail
            (PresentedValue.Identifier(id.ToString("D")))

    let optional format =
        Option.map format >> Option.defaultValue absent

    /// A full card uses all declared facts, including non-listed fields; complete money pairs stay atomic.

    let section title entries =
        {
            Section.TitleId = title
            Entries = entries
        }

    let count key label value =
        {
            Entry.Key = key
            Recorded = false
            LabelId = label
            Prominence = Prominence.Primary
            Value = PresentedValue.Count value
        }

    /// Counts retain known zero values and the producer's order; absence is never inferred.

    let code vocabulary item =
        PresentedValue.Code(Vocabulary.name vocabulary, Vocabulary.token vocabulary item)

    let codes vocabulary items =
        PresentedValue.Codes(
            Vocabulary.name vocabulary,
            items |> List.map (Vocabulary.token vocabulary)
        )

    let boolean item =
        code PresentedBoolean.vocabulary (PresentedBoolean.ofBool item)

    let column key label prominence isMoney read =
        {
            EntryDefinition.Key = key
            LabelId = label
            Prominence = prominence
            IsMoney = isMoney
            Read = read
        }

    let workEntries (snapshot: CaseWorkReadModel) =
        [
            value
                "responsible"
                "field.responsibleActor.label"
                Prominence.Primary
                (optional PresentedValue.Actor snapshot.Responsible)
            value
                "handlingSummary"
                "field.handlingSummary.label"
                Prominence.Primary
                (optional (HandlingSummary.format >> PresentedValue.Narrative) snapshot.Summary)
        ]

    let reminderEntries (reminder: Reminder) =
        let id = ReminderId.format (Reminder.id reminder)

        [
            value
                (id + ".id")
                "field.reminderId.label"
                Prominence.Detail
                (PresentedValue.Identifier id)
            value
                (id + ".action")
                "field.reminderAction.label"
                Prominence.Primary
                (PresentedValue.Text(ReminderAction.format (Reminder.action reminder)))
            value
                (id + ".due")
                "field.reminderDueDate.label"
                Prominence.Primary
                (PresentedValue.Date(Reminder.due reminder))
            value
                (id + ".kind")
                "field.kind.label"
                Prominence.Secondary
                (code ReminderKind.vocabulary (Reminder.kind reminder))
            if Reminder.doneOn reminder |> Option.isSome then
                value
                    (id + ".doneOn")
                    "field.doneOn.label"
                    Prominence.Secondary
                    (optional PresentedValue.Date (Reminder.doneOn reminder))
            value
                (id + ".state")
                "input.reminderState.label"
                Prominence.Primary
                (code ReminderState.vocabulary (Reminder.state reminder))
        ]

    let principalIdentity principal =
        let _, issuer, subject = PrincipalKey.parts principal

        [
            value
                "issuer"
                "field.principal.issuer.label"
                Prominence.Primary
                (PresentedValue.Identifier issuer)
            value
                "subject"
                "field.principal.subject.label"
                Prominence.Primary
                (PresentedValue.Identifier subject)
        ]

    let profileHintStatus (item: RequesterClaims) =
        if item.Name.IsSome || item.PreferredUsername.IsSome then
            PresentedValue.Message "label.unverifiedProviderHint"
        else
            absent

    let emailHintStatus (item: RequesterClaims) =
        match item.Email with
        | None -> absent
        | Some _ when item.EmailVerified -> PresentedValue.Message "label.providerVerifiedEmail"
        | Some _ -> PresentedValue.Message "label.unverifiedProviderHint"

    let claims (item: RequesterClaims) =
        [
            value
                "nameHint"
                "label.requestedName"
                Prominence.Primary
                (optional PresentedValue.Party item.Name)
            value
                "usernameHint"
                "label.requestedUsername"
                Prominence.Secondary
                (optional PresentedValue.Party item.PreferredUsername)
            value
                "emailHint"
                "label.requestedEmail"
                Prominence.Secondary
                (optional PresentedValue.Party item.Email)
            value
                "profileHintStatus"
                "label.profileHintStatus"
                Prominence.Secondary
                (profileHintStatus item)
            value
                "emailHintStatus"
                "label.emailHintStatus"
                Prominence.Secondary
                (emailHintStatus item)
        ]


    let technical (entry: Entry) =
        match entry.Value with
        | PresentedValue.Identifier _ -> true
        | _ -> false

    let region role title content =
        {
            Role = role
            TitleId = title
            Content = content
            Link = None
            Actions = []
        }

    let entryRegion role title entries =
        region role title (RegionContent.Entries entries)

    let factRegions sections =
        sections
        |> List.choose (fun (section: Section) ->
            if section.Entries.IsEmpty then
                None
            else
                Some(entryRegion RegionRole.Facts (Some section.TitleId) section.Entries))

    let factScreen sections = Screen.create (factRegions sections)

    let tableWithTransfers
        key
        describe
        (definitions: EntryDefinition<'row> list)
        controls
        transfers
        rows
        =
        {
            RegionTable.Columns =
                definitions
                |> List.map (fun definition ->
                    {
                        Column.Key = definition.Key
                        LabelId = definition.LabelId
                        Prominence = definition.Prominence
                        IsMoney = definition.IsMoney
                        Recorded = false
                    })
            Rows =
                rows
                |> List.map (fun row ->
                    let link, actions = controls row

                    {
                        RegionRow.Key = key row
                        Values = definitions |> List.map (fun definition -> definition.Read row)
                        Detail = describe row
                        Link = link
                        Actions = actions
                        Transfers = transfers row
                    })
            Groups = []
            Sections = []
            Meter = None
        }

    let tableWithControls key describe definitions controls rows =
        tableWithTransfers key describe definitions controls (fun _ -> []) rows

    let table key describe definitions rows =
        tableWithControls key describe definitions (fun _ -> None, []) rows

    let resultScreenWithControls key definitions controls rows =
        Screen.create
            [
                region
                    RegionRole.Results
                    None
                    (RegionContent.Table(
                        tableWithControls key (fun _ -> None) definitions controls rows
                    ))
            ]

    let resultScreen key definitions rows =
        Screen.create
            [
                region
                    RegionRole.Results
                    None
                    (RegionContent.Table(table key (fun _ -> None) definitions rows))
            ]

    let principalKey principal =
        let kind, issuer, subject = PrincipalKey.parts principal

        ClaimCore.Json.JsonValue.Object
            [
                ("kind",
                 ClaimCore.Json.JsonValue.String(Vocabulary.token PrincipalKind.vocabulary kind))
                ("issuer", ClaimCore.Json.JsonValue.String issuer)
                ("subject", ClaimCore.Json.JsonValue.String subject)
            ]
        |> ClaimCore.Json.CanonicalWriter.bytes
        |> Security.Cryptography.SHA256.HashData
        |> Convert.ToHexStringLower

    let observationEntries (basis: WorkDateBasis) =
        [
            value
                "businessDate"
                "label.businessDate"
                Prominence.Detail
                (PresentedValue.Date(CalendarDate.ofDate basis.BusinessDate))
            value
                "timeZone"
                "label.timeZone"
                Prominence.Detail
                (PresentedValue.Text basis.TimeZoneId)
            value
                "observedAt"
                "label.observedAt"
                Prominence.Detail
                (PresentedValue.Instant basis.ObservedAt)
        ]

    let page (basis: WorkDateBasis) shown cursor (screen: Screen) =
        let footer =
            section
                "label.page"
                [
                    value
                        "shown"
                        "label.shown"
                        Prominence.Secondary
                        (PresentedValue.Count(int64 shown))
                ]

        let metadata =
            observationEntries basis
            @ [
                yield!
                    cursor
                    |> Option.map (fun cursor ->
                        value
                            "cursor"
                            "label.nextCursor"
                            Prominence.Detail
                            (PresentedValue.Identifier cursor))
                    |> Option.toList
            ]

        let mutable foundTable = false

        let regions =
            screen.Regions
            |> List.map (fun region ->
                match region.Content with
                | RegionContent.Table table when not foundTable ->
                    foundTable <- true

                    { region with
                        Content =
                            RegionContent.Table
                                { table with
                                    Sections = table.Sections @ [ footer ]
                                }
                    }
                | _ -> region)

        let details, body =
            regions |> List.partition (fun region -> region.Role = RegionRole.Details)

        let existing =
            details
            |> List.collect (fun region ->
                match region.Content with
                | RegionContent.Entries entries -> entries
                | _ -> invalidOp "Page details contain entries.")

        Screen.create (
            body
            @ [
                entryRegion RegionRole.Details (Some "label.recordDetails") (existing @ metadata)
            ]
        )
