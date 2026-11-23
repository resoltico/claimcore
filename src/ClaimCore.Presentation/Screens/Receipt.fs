namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries
open CaseScreen

module ReceiptScreen =
    let receiptHeader reference act =
        section
            "label.recordedChange"
            ([ value "act" "label.action" Prominence.Primary act ]
             @ (reference
                |> Option.toList
                |> List.map (fun reference ->
                    value
                        "reference"
                        "field.caseReference.label"
                        Prominence.Primary
                        (PresentedValue.Reference reference))))

    let receiptDisclosure key =
        [
            section
                "label.recordedFacts"
                [
                    value
                        "disclosure"
                        "label.details"
                        Prominence.Primary
                        (PresentedValue.Absent key)
                ]
        ]

    let receiptReminderVersion (version: ReminderVersion) =
        section
            "label.reminder"
            ([
                value
                    "id"
                    "field.reminderId.label"
                    Prominence.Primary
                    (PresentedValue.Identifier(ReminderId.format version.ReminderId))
                value
                    "revision"
                    "label.reminderRevision"
                    Prominence.Detail
                    (PresentedValue.Identifier(Revision.format version.Revision))
             ]
             @ (match version.Snapshot with
                | ReminderDisclosure.Disclosed item ->
                    reminderEntries item
                    |> List.filter (fun entry ->
                        not (entry.Key.EndsWith(".id", StringComparison.Ordinal)))
                | ReminderDisclosure.Withheld ->
                    [
                        value
                            "disclosure"
                            "label.details"
                            Prominence.Primary
                            (PresentedValue.Absent "label.detailsWithheld")
                    ]
                | ReminderDisclosure.Erased ->
                    [
                        value
                            "disclosure"
                            "label.details"
                            Prominence.Primary
                            (PresentedValue.Absent "lifecycle.ERASED.label")
                    ]))

    let caseReceipt (item: CaseReceipt) =
        [
            receiptHeader
                (Some item.Reference)
                (PresentedValue.Message("task." + TaskId.token item.Act + ".label"))
            section
                "label.recordedFacts"
                [
                    value
                        "revision"
                        "label.revisionLabel"
                        Prominence.Detail
                        (PresentedValue.Identifier(Revision.format item.Revision))
                ]
        ]
        @ (match item.Snapshot with
           | SnapshotDisclosure.Disclosed live ->
               (caseFacts
                   {
                       CaseId = item.CaseId
                       Reference = item.Reference
                       Revision = item.Revision
                       State = LiveCase.toState live.State
                   })
               @ (let fields = CaseState.fields (LiveCase.toState live.State)

                  fields
                  |> Option.bind (_.Handling >> Handling.closure)
                  |> Option.map (fun closure ->
                      section
                          "label.closure"
                          [
                              value
                                  "outcome"
                                  "field.outcome.label"
                                  Prominence.Primary
                                  (code ClosureOutcome.vocabulary closure.Outcome)
                              value
                                  "closingNote"
                                  "field.closingNote.label"
                                  Prominence.Detail
                                  (closure.Note
                                   |> optional (NoteText.format >> PresentedValue.Narrative))
                          ])
                  |> Option.toList)
               @ (live.ReopenReason
                  |> Option.map (fun reason ->
                      section
                          "field.reopenReason.label"
                          [
                              value
                                  "reopenReason"
                                  "field.reopenReason.label"
                                  Prominence.Primary
                                  (PresentedValue.Narrative(Reason.format reason))
                          ])
                  |> Option.toList)
               @ (live.Disposition
                  |> Option.map (fun disposition ->
                      section
                          "field.reason.label"
                          [
                              value
                                  "reason"
                                  "field.reason.label"
                                  Prominence.Primary
                                  (PresentedValue.Narrative(
                                      Reason.format (CaseDisposition.reason disposition)
                                  ))
                          ])
                  |> Option.toList)
           | SnapshotDisclosure.Withheld -> receiptDisclosure "label.detailsWithheld"
           | SnapshotDisclosure.Erased -> receiptDisclosure "lifecycle.ERASED.label"),
        []

    let workReceipt (item: WorkReceipt) =
        [
            receiptHeader
                (Some item.Reference)
                (PresentedValue.Message("task." + TaskId.token item.Act + ".label"))
            section
                "section.work.label"
                [
                    value
                        "revision"
                        "label.workRevision"
                        Prominence.Detail
                        (PresentedValue.Identifier(Revision.format item.Revision))
                ]
        ]
        @ (match item.Snapshot with
           | WorkDisclosure.Disclosed work -> [ section "section.work.label" (workEntries work) ]
           | WorkDisclosure.Withheld -> receiptDisclosure "label.detailsWithheld"
           | WorkDisclosure.Erased -> receiptDisclosure "lifecycle.ERASED.label"),
        []

    let reminderReceipt (item: ReminderReceipt) =
        [
            receiptHeader
                (Some item.Reference)
                (PresentedValue.Message("task." + TaskId.token item.Act + ".label"))
        ]
        @ (match item.Effects with
           | ReminderReceiptEffects.Single version -> [ receiptReminderVersion version ]
           | ReminderReceiptEffects.CompletedAndPlanned(completed, next) ->
               [ receiptReminderVersion completed; receiptReminderVersion next ]),
        []

    let peopleReceipt (item: PeopleReceipt) =
        [
            receiptHeader None (PresentedValue.Message("task." + TaskId.token item.Act + ".label"))
            section
                "label.account"
                [
                    value
                        "target"
                        "label.targetAccount"
                        Prominence.Primary
                        (match item.TargetActor with
                         | PeopleTargetDisclosure.Disclosed actor -> PresentedValue.Actor actor
                         | PeopleTargetDisclosure.Withheld _ ->
                             PresentedValue.Absent "label.detailsWithheld")
                ]
        ]
        @ (item.Effects
           |> List.map (fun effect ->
               section
                   "label.effect"
                   [
                       value
                           "act"
                           "label.action"
                           Prominence.Primary
                           (PresentedValue.Message("task." + TaskId.token effect.Act + ".label"))
                       value
                           ("effect." + string (EffectId.index effect.Id) + ".position")
                           "label.effectPosition"
                           Prominence.Detail
                           (PresentedValue.Identifier(EffectPosition.format effect.Position))
                       value
                           ("effect." + string (EffectId.index effect.Id) + ".id")
                           "label.effectId"
                           Prominence.Detail
                           (PresentedValue.Identifier(EffectId.format effect.Id))
                   ])),
        [
            value
                "targetId"
                "input.actorId.label"
                Prominence.Detail
                (PresentedValue.Identifier(
                    ActorId.format (PeopleTargetDisclosure.actorId item.TargetActor)
                ))
        ]

    let sections (receipt: OperationReceipt) =
        let body, knownTarget =
            match receipt.Subject with
            | ReceiptSubject.Case item -> caseReceipt item
            | ReceiptSubject.Work item -> workReceipt item
            | ReceiptSubject.Party item ->
                let entries =
                    match item.Snapshot with
                    | PartyDisclosure.Disclosed content ->
                        [
                            value
                                "role"
                                "field.partyRole.label"
                                Prominence.Primary
                                (code PartyRole.vocabulary content.Role)
                            value
                                "name"
                                "field.partyName.label"
                                Prominence.Primary
                                (optional PresentedValue.Party content.Name)
                            value
                                "details"
                                "field.partyDetails.label"
                                Prominence.Secondary
                                (optional
                                    (PartyDetails.format >> PresentedValue.Narrative)
                                    content.Details)
                        ]
                    | PartyDisclosure.Erased ->
                        [
                            value
                                "name"
                                "field.partyName.label"
                                Prominence.Primary
                                (PresentedValue.Absent "label.changesErased")
                        ]
                    | PartyDisclosure.Withheld ->
                        [
                            value
                                "name"
                                "field.partyName.label"
                                Prominence.Primary
                                (PresentedValue.Absent "label.changesWithheld")
                        ]

                [
                    section
                        "field.parties.label"
                        (value
                            "caseReference"
                            "field.caseReference.label"
                            Prominence.Primary
                            (PresentedValue.Reference item.Reference)
                         :: entries)
                ],
                [
                    partIdentity
                        ClaimCore.Domain.Changes.PartKind.Party
                        (PartyId.value item.PartyId)
                ]
            | ReceiptSubject.Attachment item ->
                let entries =
                    match item.Snapshot with
                    | AttachmentDisclosure.Disclosed attachment ->
                        AttachmentsScreen.details attachment
                    | AttachmentDisclosure.Erased ->
                        [
                            value
                                "attachmentName"
                                "field.attachmentName.label"
                                Prominence.Secondary
                                (PresentedValue.Absent "label.changesErased")
                        ]
                    | AttachmentDisclosure.Withheld ->
                        [
                            value
                                "attachmentName"
                                "field.attachmentName.label"
                                Prominence.Secondary
                                (PresentedValue.Absent "label.changesWithheld")
                        ]

                [
                    section
                        "section.attachments.label"
                        (value
                            "caseReference"
                            "field.caseReference.label"
                            Prominence.Primary
                            (PresentedValue.Reference item.Reference)
                         :: entries)
                ],
                [
                    partIdentity
                        ClaimCore.Domain.Changes.PartKind.Attachment
                        (AttachmentId.value item.AttachmentId)
                ]
            | ReceiptSubject.Note item ->
                let entries =
                    match item.Snapshot with
                    | NoteDisclosure.Disclosed note -> NotesScreen.entries note
                    | NoteDisclosure.Erased ->
                        [
                            value
                                "noteText"
                                "field.noteText.label"
                                Prominence.Secondary
                                (PresentedValue.Absent "label.changesErased")
                        ]
                    | NoteDisclosure.Withheld ->
                        [
                            value
                                "noteText"
                                "field.noteText.label"
                                Prominence.Secondary
                                (PresentedValue.Absent "label.changesWithheld")
                        ]

                [
                    section
                        "section.notes.label"
                        (value
                            "caseReference"
                            "field.caseReference.label"
                            Prominence.Primary
                            (PresentedValue.Reference item.Reference)
                         :: entries)
                ],
                [
                    value
                        "noteId"
                        "field.noteId.label"
                        Prominence.Detail
                        (PresentedValue.Identifier(NoteId.format item.NoteId))
                ]
            | ReceiptSubject.Link item ->
                [
                    section
                        "section.relatedCases.label"
                        [
                            value
                                "caseReference"
                                "field.caseReference.label"
                                Prominence.Primary
                                (PresentedValue.Reference item.Reference)
                        ]
                ],
                [
                    value
                        "linkId"
                        "field.linkId.label"
                        Prominence.Detail
                        (PresentedValue.Identifier(LinkId.format item.LinkId))
                ]
            | ReceiptSubject.Reminder item -> reminderReceipt item
            | ReceiptSubject.People item -> peopleReceipt item

        body
        @ [
            section
                "label.technicalDetails"
                ([
                    value
                        "operationId"
                        "field.operationId.label"
                        Prominence.Detail
                        (PresentedValue.Identifier(OperationId.format receipt.OperationId))
                    value
                        "sequence"
                        "label.sequence"
                        Prominence.Detail
                        (PresentedValue.Identifier(Sequence.format receipt.Position.Sequence))
                    value
                        "operationHash"
                        "label.operationHash"
                        Prominence.Detail
                        (PresentedValue.Identifier(
                            receipt.Position.EntryHash
                            |> EntryHash.toBytes
                            |> Convert.ToHexStringLower
                        ))
                    value
                        "recordedBy"
                        "label.recordedBy"
                        Prominence.Primary
                        (ActorPresentation.authorityValue receipt.RecordedBy)
                 ]
                 @ knownTarget)
        ]

    /// Host session presentation contains no antiforgery-token value.


    let private recordedFacts (receipt: OperationReceipt) (screen: Screen) =
        let selected =
            match receipt.Subject with
            | ReceiptSubject.Case value ->
                match value.Snapshot with
                | SnapshotDisclosure.Disclosed shown ->
                    Some(shown.RecordedFields |> List.map FieldPresentation.labelId |> Set.ofList)
                | _ -> Some Set.empty
            | ReceiptSubject.Work _ ->
                Some(Set.ofList [ "field.handlingSummary.label"; "field.responsibleActor.label" ])
            | _ -> None

        let isRecorded label =
            label = "field.reason.label"
            || (selected |> Option.map (Set.contains label) |> Option.defaultValue true)

        let markEntry (entry: Entry) =
            { entry with
                Recorded = Some(entry.Recorded <> Some false && isRecorded entry.LabelId)
            }

        Screen.mapContent
            (fun role content ->
                match content with
                | RegionContent.Entries values when
                    role = RegionRole.Facts || role = RegionRole.Work
                    ->
                    RegionContent.Entries(List.map markEntry values)
                | RegionContent.Table table ->
                    RegionContent.Table
                        { table with
                            Columns =
                                table.Columns
                                |> List.map (fun column ->
                                    { column with
                                        Recorded = isRecorded column.LabelId
                                    })
                        }
                | content -> content)
            screen

    let view (receipt: OperationReceipt) =
        let source = sections receipt
        let record = List.last source
        let body = source |> List.take (source.Length - 1)

        let payments =
            match receipt.Subject with
            | ReceiptSubject.Case {
                                      Snapshot = SnapshotDisclosure.Disclosed live
                                  } ->
                let fields =
                    match live.State with
                    | LiveCase.Active fields
                    | LiveCase.Voided fields -> fields

                Some(PaymentsScreen.changes fields.Finance live.Payments)
            | _ -> None

        let facts =
            body
            |> List.filter (fun section ->
                payments.IsNone
                || section.TitleId
                   <> Vocabulary.messageKey RecordGroup.vocabulary RecordGroup.Decision "label")
            |> List.filter (fun section -> section.TitleId <> "label.recordedChange")
            |> List.map (fun section ->
                { section with
                    Entries = section.Entries |> List.filter (technical >> not)
                })

        let technical = body |> List.collect _.Entries |> List.filter technical

        let act =
            match receipt.Subject with
            | ReceiptSubject.Case item -> item.Act
            | ReceiptSubject.Work item -> item.Act
            | ReceiptSubject.Attachment item -> item.Act
            | ReceiptSubject.Note item -> item.Act
            | ReceiptSubject.Party item -> item.Act
            | ReceiptSubject.Link item -> item.Act
            | ReceiptSubject.Reminder item -> item.Act
            | ReceiptSubject.People item -> item.Act

        let reference =
            match receipt.Subject with
            | ReceiptSubject.Case item -> Some item.Reference
            | ReceiptSubject.Work item -> Some item.Reference
            | ReceiptSubject.Attachment item -> Some item.Reference
            | ReceiptSubject.Note item -> Some item.Reference
            | ReceiptSubject.Party item -> Some item.Reference
            | ReceiptSubject.Link item -> Some item.Reference
            | ReceiptSubject.Reminder item -> Some item.Reference
            | ReceiptSubject.People _ -> None

        let stateEntries =
            match receipt.Subject with
            | ReceiptSubject.Case item ->
                match item.Snapshot with
                | SnapshotDisclosure.Disclosed live ->
                    [
                        match live.State with
                        | LiveCase.Active fields ->
                            value
                                "status"
                                "field.status.label"
                                Prominence.Primary
                                (code CaseStatus.vocabulary fields.Status)
                        | LiveCase.Voided _ ->
                            value
                                "lifecycle"
                                "input.lifecycle.label"
                                Prominence.Primary
                                (code Lifecycle.vocabulary Lifecycle.Voided)

                        let fields =
                            match live.State with
                            | LiveCase.Active fields
                            | LiveCase.Voided fields -> fields

                        value
                            "progress"
                            "input.progress.label"
                            Prominence.Primary
                            (code CaseProgress.vocabulary (CaseProgress.ofFinancial fields.Finance))
                    ]
                | SnapshotDisclosure.Erased ->
                    [
                        value
                            "lifecycle"
                            "input.lifecycle.label"
                            Prominence.Primary
                            (code Lifecycle.vocabulary Lifecycle.Erased)
                    ]
                | SnapshotDisclosure.Withheld -> []
            | _ -> []

        let identity =
            (receiptHeader reference (PresentedValue.Message("task." + TaskId.token act + ".label")))
                .Entries
            @ stateEntries
            @ [
                value
                    "recordedAt"
                    "label.recordedAt"
                    Prominence.Primary
                    (PresentedValue.Instant receipt.RecordedAt)
            ]

        let message =
            if (TaskConsequences.declaration act).Exposure = TaskExposure.Operator then
                "label.recorded"
            else
                "task." + TaskId.token act + ".done"

        Screen.create (
            [
                region
                    RegionRole.Notice
                    None
                    (RegionContent.Notice
                        [
                            {
                                MessageId = message
                                Reason = None
                                Tone = NoticeTone.Info
                            }
                        ])
            ]
            @ [ entryRegion RegionRole.Identity (Some "label.recordedChange") identity ]
            @ factRegions facts
            @ (payments
               |> Option.map (fun table ->
                   region
                       RegionRole.Payments
                       (Some "label.paymentDecisionAndPayments")
                       (RegionContent.Table table))
               |> Option.toList)
            @ [
                entryRegion
                    RegionRole.Details
                    (Some "label.technicalDetails")
                    (technical
                     @ record.Entries
                     @ [
                         value
                             "observedAt"
                             "label.observedAt"
                             Prominence.Detail
                             (PresentedValue.Instant receipt.Basis.ObservedAt)
                         value
                             "businessDate"
                             "label.businessDate"
                             Prominence.Detail
                             (PresentedValue.Date(CalendarDate.ofDate receipt.Basis.BusinessDate))
                         value
                             "timeZone"
                             "label.timeZone"
                             Prominence.Detail
                             (PresentedValue.Text receipt.Basis.TimeZoneId)
                     ])
            ]
        )
        |> recordedFacts receipt
