namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Protocol.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal TextCaseReply =
    open TextReplyFormat

    let fieldLines language (fields: CaseFields) =
        let rows placement =
            CaseFields.declarations
            |> List.filter (fun row ->
                row.Group = placement && row.Section <> Some CaseSection.Work)
            |> List.map (fun row ->
                line
                    language
                    (FieldPresentation.labelId row.Field)
                    (fieldValue
                        language
                        row.Field
                        (if row.Field = BusinessField.Status then
                             Some(term language CaseStatus.vocabulary fields.Status)
                         else
                             CaseFields.value row.Field fields)))

        rows None
        @ (Vocabulary.all RecordGroup.vocabulary
           |> List.collect (fun group ->
               (label language (Vocabulary.messageKey RecordGroup.vocabulary group "label")
                + ":")
               :: rows (Some group)))

    let assigneePage language (page: AssigneePage) =
        (page.Items
         |> List.map (fun account ->
             ActorId.format account.ActorId
             + "  "
             + displayed language (Some(actorName language account))))
        @ nextLines language page.NextCursor

    let offerAct language act =
        term language CaseActKind.vocabulary act

    let caseLines language (value: CaseReadModel) =
        let sections =
            Descriptor.caseView value
            |> List.filter (fun section -> section.TitleId <> "label.recommendedNext")

        TextDescriptorValue.sections language sections
        @ (value.Advice.Recommended
           |> Option.toList
           |> List.map (fun act ->
               let operation = CatalogOperation.Act(ActKind.Case act)

               let command =
                   CommandGrammar.commandText operation
                   + " "
                   + CommandGrammar.quoteArgument (CaseReference.format value.Snapshot.Reference)
                   + " --expected-revision "
                   + Revision.format value.Snapshot.Revision
                   + " …"

               line
                   language
                   "label.recommendedNext"
                   (offerAct language act + " (" + TerminalText.data command + ")")))
        @ (if value.Advice.Maintenance.Active then
               [ label language "diagnostic.REGISTER_MAINTENANCE" ]
           else
               [])
        @ [
            line
                language
                (CliMessages.key CliMessage.AvailableActsLabel)
                (value.Advice.Acts |> List.map (offerAct language) |> String.concat ", ")
        ]
        @ (if value.Advice.Blocked.IsEmpty then
               []
           else
               label language "label.unavailableActions"
               :: (value.Advice.Blocked
                   |> List.collect (fun blocked ->
                       (offerAct language blocked.Act + ":")
                       :: TextDiagnosticReply.refusalLinesFor
                           language
                           (Some(CaseReference.format value.Snapshot.Reference))
                           blocked.Reason)))
        @ [ TextObservation.line language value.Work.Basis ]

    let historyLines language (page: HistoryResultPage) =
        (page.Entries
         |> List.collect (function
             | HistoryEntry.SummaryEntry change ->
                 [
                     String.concat
                         "  "
                         [
                             Revision.format change.Revision
                             term language CaseActKind.vocabulary change.Act
                             instant change.RecordedAt
                             OperationId.format change.OperationId
                         ]
                 ]
             | HistoryEntry.FullEntry full ->
                 let receipt = full.Receipt

                 let revision, act, fields =
                     match receipt.Subject with
                     | ReceiptSubject.Case value ->
                         let fields =
                             match value.Snapshot with
                             | SnapshotDisclosure.Disclosed live ->
                                 LiveCase.toState live
                                 |> CaseState.fields
                                 |> Option.map (fieldLines language)
                                 |> Option.defaultValue []
                             | SnapshotDisclosure.Withheld
                             | SnapshotDisclosure.Erased -> []

                         Revision.format value.Revision,
                         term language CaseActKind.vocabulary value.Act,
                         fields
                     | ReceiptSubject.Work value ->
                         Revision.format value.Revision,
                         term language WorkActKind.vocabulary value.Act,
                         []
                     | ReceiptSubject.Reminder value ->
                         "—", term language ReminderActKind.vocabulary value.Act, []
                     | ReceiptSubject.Authority value ->
                         "—", term language AuthorityKind.vocabulary value.Act, []

                 String.concat
                     "  "
                     [
                         revision
                         act
                         instant receipt.RecordedAt
                         OperationId.format receipt.OperationId
                     ]
                 :: (fields |> List.map ((+) "  "))))
        @ nextLines language page.NextCursor

    let casePage language (page: CaseSummaryPage) =
        CaseListRows.textHeader language :: CaseListRows.textRows language page.Items
        @ nextLines language page.NextCursor
