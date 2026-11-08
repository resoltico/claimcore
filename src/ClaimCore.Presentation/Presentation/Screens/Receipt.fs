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
            receiptHeader (Some item.Reference) (code CaseActKind.vocabulary item.Act)
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
               caseFacts
                   {
                       CaseId = item.CaseId
                       Reference = item.Reference
                       Revision = item.Revision
                       State = LiveCase.toState live
                   }
           | SnapshotDisclosure.Withheld -> receiptDisclosure "label.detailsWithheld"
           | SnapshotDisclosure.Erased -> receiptDisclosure "lifecycle.ERASED.label"),
        []

    let workReceipt (item: WorkReceipt) =
        [
            receiptHeader (Some item.Reference) (code WorkActKind.vocabulary item.Act)
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
            receiptHeader (Some item.Reference) (code ReminderActKind.vocabulary item.Act)
        ]
        @ (match item.Effects with
           | ReminderReceiptEffects.Single version -> [ receiptReminderVersion version ]
           | ReminderReceiptEffects.CompletedAndPlanned(completed, next) ->
               [ receiptReminderVersion completed; receiptReminderVersion next ]),
        []

    let authorityReceipt (item: AuthorityReceipt) =
        [
            receiptHeader None (code AuthorityKind.vocabulary item.Act)
            section
                "label.account"
                [
                    value
                        "target"
                        "label.targetAccount"
                        Prominence.Primary
                        (match item.TargetActor with
                         | AuthorityTargetDisclosure.Disclosed actor -> PresentedValue.Actor actor
                         | AuthorityTargetDisclosure.Withheld _ ->
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
                           (code AuthorityKind.vocabulary effect.Act)
                       value
                           "sequence"
                           "label.sequence"
                           Prominence.Detail
                           (PresentedValue.Identifier(Sequence.format effect.Position.Sequence))
                   ])),
        [
            value
                "targetId"
                "input.actorId.label"
                Prominence.Detail
                (PresentedValue.Identifier(
                    ActorId.format (AuthorityTargetDisclosure.actorId item.TargetActor)
                ))
        ]

    let sections (receipt: OperationReceipt) =
        let body, knownTarget =
            match receipt.Subject with
            | ReceiptSubject.Case item -> caseReceipt item
            | ReceiptSubject.Work item -> workReceipt item
            | ReceiptSubject.Reminder item -> reminderReceipt item
            | ReceiptSubject.Authority item -> authorityReceipt item

        body
        @ [
            section
                "label.recordDetails"
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
                        "recordedAt"
                        "label.recordedAt"
                        Prominence.Primary
                        (PresentedValue.Instant receipt.RecordedAt)
                    value
                        "recordedBy"
                        "label.recordedBy"
                        Prominence.Primary
                        (PresentedValue.Actor receipt.RecordedBy)
                 ]
                 @ knownTarget)
        ]

    /// Host session presentation contains no antiforgery-token value.


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
                    match live with
                    | LiveCase.Active fields
                    | LiveCase.Voided fields -> fields

                Some(PaymentsScreen.table fields)
            | _ -> None

        let facts =
            body
            |> List.filter (fun section ->
                payments.IsNone
                || section.TitleId
                   <> Vocabulary.messageKey RecordGroup.vocabulary RecordGroup.Decision "label")
            |> List.map (fun section ->
                { section with
                    Entries = section.Entries |> List.filter (technical >> not)
                })

        let technical = body |> List.collect _.Entries |> List.filter technical

        let act =
            match receipt.Subject with
            | ReceiptSubject.Case item -> ActKind.Case item.Act
            | ReceiptSubject.Work item -> ActKind.Work item.Act
            | ReceiptSubject.Reminder item -> ActKind.Reminder item.Act
            | ReceiptSubject.Authority item -> ActKind.Authority item.Act

        let message =
            if act = ActKind.Authority AuthorityKind.ProvisionOwner then
                "label.recorded"
            else
                Vocabulary.messageKey ActKind.vocabulary act "done"

        Screen.create (
            [
                region
                    RegionRole.Notice
                    None
                    (RegionContent.Notice
                        {
                            MessageId = message
                            Reason = None
                            Tone = NoticeTone.Info
                        })
            ]
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
                    (Some "label.recordDetails")
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
