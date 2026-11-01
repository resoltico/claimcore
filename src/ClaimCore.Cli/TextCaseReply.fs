namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal TextCaseReply =
    open TextReplyFormat

    let fieldLines language (fields: CaseFields) =
        let rows placement =
            CaseFields.declarations
            |> List.filter (fun row -> row.Group = placement)
            |> List.map (fun row ->
                line
                    language
                    (FieldPresentation.labelId row.Field)
                    (displayed
                        language
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
             + displayed language (Some account.Subject)))
        @ nextLines language page.NextCursor

    let private workAccount language (work: CaseWorkView) =
        work.Responsible
        |> Option.map (fun assigned ->
            line
                language
                (FieldPresentation.labelId BusinessField.ResponsibleActor)
                (displayed language (Some assigned.Account.Subject)
                 + " ["
                 + ActorId.format assigned.Account.ActorId
                 + "] — "
                 + label
                     language
                     (if assigned.Available then
                          "label.assigneeAvailable"
                      else
                          "label.assigneeUnavailable")))
        |> Option.toList

    let offerAct language act =
        term language CaseActKind.vocabulary act

    let caseLines language (value: CaseReadModel) =
        [
            line
                language
                (FieldPresentation.labelId BusinessField.CaseReference)
                (TerminalText.data (CaseReference.format value.Snapshot.Reference))
            line
                language
                (CliMessages.key CliMessage.RevisionLabel)
                (Revision.format value.Snapshot.Revision)
            line
                language
                (CliMessages.key CliMessage.StateLabel)
                (term language Lifecycle.vocabulary (CaseState.lifecycle value.Snapshot.State))
        ]
        @ (if value.Advice.Maintenance.Active then
               [ label language "diagnostic.REGISTER_MAINTENANCE" ]
           else
               [])
        @ workAccount language value.Work
        @ (CaseState.fields value.Snapshot.State
           |> Option.map (fieldLines language)
           |> Option.defaultValue [])
        @ [
            line
                language
                (CliMessages.key CliMessage.AvailableActsLabel)
                (value.Advice.Acts |> List.map (offerAct language) |> String.concat ", ")
        ]

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
