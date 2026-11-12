module ClaimCore.Tests.ReminderControlDocumentTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private item =
    let id =
        ReminderId.create (Guid.Parse "00000000-0000-4000-8000-000000000002")
        |> Option.get

    let parent = ResponseExamples.caseRead.Snapshot.CaseId

    let reminder =
        Reminder.ofAccepted
            id
            parent
            Revision.first
            (ReminderAction.parse "Editable action seed"
             |> Result.defaultWith (string >> failtest))
            (CalendarDate.ofDate Inputs.workBasis.BusinessDate)
            ReminderState.Pending
        |> Result.defaultWith failtest

    {
        ReminderReadModel.Reminder = reminder
        Reference = ResponseExamples.caseRead.Snapshot.Reference
        ClaimantName = Inputs.name "Read-only claimant"
        Responsible = None
        RecordedAt = Inputs.workBasis.ObservedAt
        RecordedBy = ResponseExamples.caseRead.RecordedBy
        Acts = [ ReminderActKind.Update; ReminderActKind.Complete ]
        Active = true
        Timing = Some ReminderDue.Today
    }

[<Tests>]
let tests =
    testCase "reminder list and detail share versioned task controls and permitted seeds" (fun () ->
        let document =
            HumanReadDocuments.reminders
                {
                    Basis = Inputs.workBasis
                    Items = [ item ]
                    NextCursor = None
                }

        let listControl =
            match document.Context with
            | InteractionContext.Reminders page -> List.exactlyOne page.Reminders
            | _ -> failtest "Reminder list controls required"

        Expect.equal listControl (ReminderInteraction.ofCore item) "Same authorized projection"
        Expect.equal listControl.Revision Revision.first "Actual reminder version"

        Expect.equal
            listControl.Tasks
            [
                TaskId.Operation(ActKind.Reminder ReminderActKind.Update)
                TaskId.Operation(ActKind.Reminder ReminderActKind.Complete)
            ]
            "Literal permitted task set"

        Expect.equal
            listControl.ActionSeed
            (Some "Editable action seed")
            "Only offered update seeds action"

        let readOnly =
            ReminderInteraction.ofCore
                { item with
                    Acts = [ ReminderActKind.Complete ]
                }

        Expect.isNone readOnly.ActionSeed "Completion does not disclose an editable action seed"
        Expect.isNone readOnly.DueDateSeed "Completion does not offer a due input"

        Expect.isOk
            (Codec.encode HumanDocumentCodecs.document document
             |> Codec.decode 65536 HumanDocumentCodecs.document)
            "Actual native structural boundary")
