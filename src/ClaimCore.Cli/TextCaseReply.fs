namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation
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
        let header, rows =
            TextTable.page language (TextTable.environment ()) Descriptor.assigneeColumns page.Items

        (if header = "" then [] else [ header ])
        @ rows page.Items
        @ TextObservation.footer language page.Items.Length page.NextCursor page.Basis false

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

    let casePage language (page: CaseSummaryPage) =
        let header = CaseListRows.textHeader language

        (if header = "" then [] else [ header ])
        @ CaseListRows.textRows language page.Items
        @ TextObservation.footer language page.Items.Length page.NextCursor page.Basis true
