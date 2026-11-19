namespace ClaimCore.Wire.Export

open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// CSV exports the supplied typed differences as canonical, unlocalized scalars.
module TimelineCsv =
    let private difference key format (change: ValueChange<'value>) =
        key,
        (change.Before |> Option.map format |> Option.defaultValue ""),
        (change.After |> Option.map format |> Option.defaultValue "")

    let scalarDifferences =
        function
        | TimelineChange.CaseField change ->
            [
                difference
                    (BusinessField.key change.FieldName)
                    CaseFieldValue.format
                    {
                        Before = change.Before
                        After = change.After
                    }
            ]
        | TimelineChange.CaseMoney change ->
            [
                difference
                    (BusinessField.key change.FieldName)
                    (fun money -> Money.format money + " " + Money.currency money)
                    change.Change
            ]
        | TimelineChange.Disposition disposition ->
            [ "reason", "", (CaseDisposition.reason disposition |> Reason.format) ]
        | TimelineChange.Lifecycle change ->
            [
                difference
                    "lifecycle"
                    Lifecycle.token
                    {
                        Before = change.Before
                        After = Some change.After
                    }
            ]
        | TimelineChange.Responsibility change ->
            [
                difference
                    "responsibleActor"
                    (fun (actor: ActorRef) -> ActorId.format actor.ActorId)
                    change
            ]
        | TimelineChange.HandlingSummary change ->
            [ difference "handlingSummary" HandlingSummary.format change ]
        | TimelineChange.Payments change ->
            [
                for changed in change.Entries do
                    let entry = PaymentDifference.entry changed
                    let prefix = "payment." + OperationId.format (PaymentEntry.id entry) + "."
                    let date = CalendarDate.format (PaymentEntry.date entry)
                    let money = PaymentEntry.money entry

                    let cells =
                        [
                            "paymentDate", date
                            "amount", Money.format money
                            "currency", Money.currency money
                            "direction", PaymentDirection.token (PaymentEntry.direction entry)
                            "reference",
                            PaymentEntry.reference entry
                            |> Option.map Identifier.format
                            |> Option.defaultValue ""
                            "payee",
                            PaymentEntry.payee entry
                            |> Option.map PartyId.format
                            |> Option.defaultValue ""
                        ]

                    for name, value in cells do
                        match changed with
                        | PaymentDifference.Recorded _ -> yield prefix + name, "", value
                        | PaymentDifference.Removed _ -> yield prefix + name, value, ""
                yield
                    difference
                        "netPaid"
                        (fun (totals: PaymentTotals) ->
                            DecimalTotal.format totals.NetPaid
                            + " "
                            + Money.currency totals.Payable)
                        {
                            Before = change.Before
                            After = change.After
                        }
            ]
        | TimelineChange.Attachment _ -> []
        | TimelineChange.Note(_, id, text) ->
            [
                "note." + NoteId.format id + ".text",
                "",
                (text |> Option.map NoteText.format |> Option.defaultValue "")
            ]
        | TimelineChange.Party(id, change) ->
            let prefix = "party." + PartyId.format id + "."

            [
                difference
                    (prefix + "role")
                    PartyRole.token
                    {
                        Before = change.Before |> Option.map _.Role
                        After = change.After |> Option.map _.Role
                    }
                difference
                    (prefix + "name")
                    PartyName.format
                    {
                        Before = change.Before |> Option.bind _.Name
                        After = change.After |> Option.bind _.Name
                    }
                difference
                    (prefix + "details")
                    PartyDetails.format
                    {
                        Before = change.Before |> Option.bind _.Details
                        After = change.After |> Option.bind _.Details
                    }
            ]
        | TimelineChange.Reminder change ->
            let prefix = "reminder." + ReminderId.format change.ReminderId + "."

            [
                yield!
                    change.Action
                    |> Option.toList
                    |> List.map (difference (prefix + "action") ReminderAction.format)
                yield!
                    change.Due
                    |> Option.toList
                    |> List.map (difference (prefix + "dueDate") CalendarDate.format)
                yield!
                    change.Kind
                    |> Option.toList
                    |> List.map (difference (prefix + "kind") ReminderKind.token)
                yield!
                    change.DoneOn
                    |> Option.toList
                    |> List.map (difference (prefix + "doneOn") CalendarDate.format)
                yield!
                    change.State
                    |> Option.toList
                    |> List.map (difference (prefix + "state") ReminderState.token)
            ]

    let private csv = Csv.row

    let csvHeader =
        csv
            [
                "caseReference"
                "operationId"
                "sequence"
                "effectIndex"
                "operationHash"
                "recordedAt"
                "recordedBy.actorId"
                "recordedBy.displayName.value"
                "recordedBy.displayName.kind"
                "act"
                "revision"
                "reminderId"
                "nextReminderId"
                "nextReminderRevision"
                "changes.kind"
                "field"
                "before"
                "after"
            ]

    let private rows reference (entry: CaseTimelineEntry) =
        let act = TaskId.token entry.Task
        let revision = Revision.format entry.Subject.Revision

        let reminder =
            if entry.Subject.PartKind = ClaimCore.Domain.Changes.PartKind.Reminder then
                entry.Subject.PartId.ToString("D")
            else
                ""

        let nextId, nextRevision = "", ""

        let actorName, actorKind =
            match entry.RecordedBy.DisplayName with
            | DisplayName.Named value -> PartyName.format value, "NAMED"
            | DisplayName.Erased -> "", "ERASED"

        let disclosure =
            match entry.Changes with
            | TimelineChanges.Disclosed _ -> "DISCLOSED"
            | TimelineChanges.Withheld -> "WITHHELD"
            | TimelineChanges.Erased -> "ERASED"

        let metadata =
            [
                CaseReference.format reference
                OperationId.format entry.OperationId
                Sequence.format (EffectPosition.sequence entry.Position)
                string (EffectPosition.index entry.Position)
                entry.OperationHash |> EntryHash.toBytes |> System.Convert.ToHexStringLower
                Csv.instant entry.RecordedAt
                ActorId.format entry.RecordedBy.ActorId
                actorName
                actorKind
                act
                revision
                reminder
                nextId
                nextRevision
                disclosure
            ]

        let differences =
            match entry.Changes with
            | TimelineChanges.Disclosed disclosure ->
                disclosure.Items |> List.collect scalarDifferences
            | _ -> []

        let differences = if differences.IsEmpty then [ "", "", "" ] else differences

        differences
        |> Seq.map (fun (key, before, after) -> csv (metadata @ [ key; before; after ]))

    let csvChunks (page: CaseTimelinePage) =
        page.Items |> Seq.collect (rows page.Reference)

    let csvRows page =
        csvChunks page |> Seq.toArray |> Array.concat
