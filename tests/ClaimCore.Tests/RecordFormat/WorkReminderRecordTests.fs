module ClaimCore.Tests.WorkReminderRecordTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private required =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

let private operation =
    Guid.Parse "96000000-0000-7000-8000-000000000001"
    |> OperationId.create
    |> Option.get

let private parent =
    Guid.Parse "96000000-0000-7000-8000-000000000002" |> CaseId.create |> Option.get

let private utf8 (value: string) = Encoding.UTF8.GetBytes value

let private exact expected actual =
    Expect.sequenceEqual actual (utf8 expected) "Independently authored canonical UTF8 bytes"

let private summary value = HandlingSummary.parse value |> required
let private action value = ReminderAction.parse value |> required
let private due = CalendarDate.parse "2026-11-10" |> required

let private workRequest text : WorkRequestRecord =
    {
        OperationId = operation
        CaseId = parent
        ExpectedRevision = Revision.zero
        Update =
            {
                Responsible = WorkValue.Keep
                Summary = WorkValue.Set(summary text)
            }
    }

let private reminderRequest text : ReminderRequestRecord =
    {
        OperationId = operation
        Command = ReminderCommand.Create(parent, { Action = action text; Due = due })
    }

let private reminder state revision text =
    Reminder.ofAccepted (ReminderId.ofOperationId operation) parent revision (action text) due state
    |> required

[<Tests>]
let tests =
    testList
        "work and reminder permanent records"
        [
            testCase
                "work request and empty root have independently authored literal bytes"
                (fun () ->
                    let request = workRequest "first\nsecond"

                    exact
                        """{"caseId":"96000000-0000-7000-8000-000000000002","expectedRevision":0,"format":1,"kind":"UPDATE_WORK","operationId":"96000000-0000-7000-8000-000000000001","values":{"handlingSummary":{"kind":"SET","value":"first\nsecond"},"responsibleActor":{"kind":"KEEP"}}}"""
                        (WorkRecord.encodeRequest request)

                    let root =
                        {
                            CaseWorkSnapshot.CaseId = parent
                            Revision = Revision.zero
                            Work = CaseWork.empty
                        }

                    exact
                        """{"caseId":"96000000-0000-7000-8000-000000000002","format":1,"revision":0,"work":{"handlingSummary":null,"responsibleActor":null}}"""
                        (WorkRecord.encodeSnapshot root)

                    Expect.equal
                        (WorkRecord.decodeRequest (WorkRecord.encodeRequest request))
                        (Ok request)
                        "Request roundtrip"

                    Expect.equal
                        (WorkRecord.decodeSnapshot (WorkRecord.encodeSnapshot root))
                        (Ok root)
                        "Zero root roundtrip")
            testCase "revision-zero work cannot contain authored state" (fun () ->
                let invalid =
                    """{"caseId":"96000000-0000-7000-8000-000000000002","format":1,"revision":0,"work":{"handlingSummary":"Already authored","responsibleActor":null}}"""

                Expect.isError
                    (WorkRecord.decodeSnapshot (utf8 invalid))
                    "OPEN creates exactly an empty zero root"

                let valid =
                    """{"caseId":"96000000-0000-7000-8000-000000000002","format":1,"revision":1,"work":{"handlingSummary":"Already authored","responsibleActor":null}}"""

                Expect.isOk
                    (WorkRecord.decodeSnapshot (utf8 valid))
                    "Same authored value at first edit is representable")
            testCase
                "reminder creation request has explicit format parent and operation identity"
                (fun () ->
                    let request = reminderRequest "Review evidence"

                    exact
                        """{"act":{"kind":"CREATE","plan":{"action":"Review evidence","due":"2026-11-10"}},"caseId":"96000000-0000-7000-8000-000000000002","format":1,"operationId":"96000000-0000-7000-8000-000000000001"}"""
                        (ReminderRecord.encodeRequest request)

                    Expect.equal
                        (ReminderRecord.decodeRequest (ReminderRecord.encodeRequest request))
                        (Ok request)
                        "Creation roundtrip"

                    let wrong =
                        """{"act":{"kind":"CREATE","plan":{"action":"Review evidence","due":"2026-11-10"}},"caseId":"96000000-0000-7000-8000-000000000002","format":2,"operationId":"96000000-0000-7000-8000-000000000001"}"""

                    Expect.isError
                        (ReminderRecord.decodeRequest (utf8 wrong))
                        "Unrecognized permanent format refuses")
            testCase "work clear keep and set remain distinct permanent meanings" (fun () ->
                let set = workRequest "Summary"

                let keep =
                    { set with
                        Update =
                            { set.Update with
                                Summary = WorkValue.Keep
                            }
                    }

                let clear =
                    { set with
                        Update =
                            { set.Update with
                                Summary = WorkValue.Clear
                            }
                    }

                for value in [ set; keep; clear ] do
                    Expect.equal
                        (WorkRecord.decodeRequest (WorkRecord.encodeRequest value))
                        (Ok value)
                        "Three typed choices survive"

                Expect.notEqual
                    (WorkRecord.encodeRequest keep)
                    (WorkRecord.encodeRequest clear)
                    "Omission/preservation is not clear"

                Expect.notEqual
                    (WorkRecord.encodeRequest set)
                    (WorkRecord.encodeRequest clear)
                    "Text is not clear")
            testCase
                "retained text decode does not reapply the current scalar admission limit"
                (fun () ->
                    let retained = String.replicate 601 "x"
                    let request = workRequest retained

                    Expect.equal
                        (WorkRecord.decodeRequest (WorkRecord.encodeRequest request))
                        (Ok request)
                        "Retained grammar permits former policy"

                    Expect.isError
                        (HandlingSummary.admit (summary retained))
                        "Current admission still refuses"

                    let oldAction = String.replicate 201 "x"
                    let oldReminder = reminder ReminderState.Completed Revision.first oldAction

                    Expect.equal
                        (ReminderRecord.decodeSnapshot (ReminderRecord.encodeSnapshot oldReminder))
                        (Ok oldReminder)
                        "Terminal retained action is decodable"

                    Expect.isError
                        (ReminderAction.admit (action oldAction))
                        "New assertion remains bounded")
            testCase
                "actual canonical combined payload remains bounded for worst admitted work and reminder text"
                (fun () ->
                    let combined request snapshot =
                        LedgerEntry.encodePayload
                            {
                                Request = request
                                Snapshot = snapshot
                            }

                    for text in
                        [
                            String.replicate 600 "😀"
                            String.replicate 600 "\""
                            String.replicate 600 "\\"
                            String.replicate 300 "a\n"
                        ] do
                        let work = summary text
                        Expect.isOk (HandlingSummary.admit work) "Valid admission control"

                        let snapshot =
                            {
                                CaseWorkSnapshot.CaseId = parent
                                Revision = Revision.first
                                Work =
                                    {
                                        Responsible = None
                                        Summary = Some work
                                    }
                            }

                        let encoded =
                            combined
                                (WorkRecord.encodeRequest (workRequest text))
                                (WorkRecord.encodeSnapshot snapshot)

                        Expect.isLessThanOrEqual
                            encoded.Length
                            32768
                            "Actual combined writer/framing, not raw text estimate"

                        Expect.isOk
                            (LedgerEntry.decodePayload 32768 encoded)
                            "Framing admits encoded payload"

                    for text in
                        [
                            String.replicate 200 "😀"
                            String.replicate 200 "\""
                            String.replicate 200 "\\"
                        ] do
                        Expect.isOk (ReminderAction.admit (action text)) "Action admission control"

                        let completed =
                            reminder
                                ReminderState.Completed
                                (Revision.create 2L |> Option.get)
                                text

                        let nextId =
                            Guid.Parse "96000000-0000-7000-8000-000000000003"
                            |> ReminderId.create
                            |> Option.get

                        let next =
                            Reminder.ofAccepted
                                nextId
                                parent
                                Revision.first
                                (action text)
                                due
                                ReminderState.Pending
                            |> required

                        let sourceId = Reminder.id completed

                        let request =
                            {
                                ReminderRequestRecord.OperationId =
                                    Guid.Parse "96000000-0000-7000-8000-000000000003"
                                    |> OperationId.create
                                    |> Option.get
                                Command =
                                    ReminderCommand.Change(
                                        parent,
                                        sourceId,
                                        Revision.first,
                                        ReminderChange.CompleteAndPlanNext
                                            { Action = action text; Due = due }
                                    )
                            }

                        let effects = ReminderEffects.CompletedAndPlanned(completed, next)

                        let encoded =
                            combined
                                (ReminderRecord.encodeRequest request)
                                (ReminderRecord.encodeEffects effects)

                        Expect.isLessThanOrEqual
                            encoded.Length
                            32768
                            "Bounded pair, never the reminder collection"

                        Expect.equal
                            (ReminderRecord.decodeEffects (ReminderRecord.encodeEffects effects))
                            (Ok effects)
                            "Both effects survive encoded bytes")
            testCase
                "actual maximum case description and required facts fit the combined permanent payload"
                (fun () ->
                    for text in
                        [
                            String.replicate 600 "😀"
                            String.replicate 600 "\""
                            String.replicate 600 "\\"
                            String.replicate 300 "a\n"
                        ] do
                        let facts =
                            RegistrationInput.create
                                [
                                    BusinessField.IncidentDate, "2026-11-02"
                                    BusinessField.IncidentNotificationDate, "2026-11-02"
                                    BusinessField.IncidentCountry, "ZZ"
                                    BusinessField.ClaimantName, String.replicate 200 "😀"
                                    BusinessField.InsurerName, String.replicate 200 "😀"
                                    BusinessField.ClaimedAmount, "999999999999999999.9999"
                                    BusinessField.ClaimedCurrency, "EUR"
                                    BusinessField.IncidentDescription, text
                                ]
                            |> required

                        let command =
                            Case.parse
                                {
                                    OperationId = OperationId.value operation
                                    CaseReference = String.replicate 80 "\\"
                                    ExpectedRevision = 0L
                                    Act = CaseAct.Open facts
                                }
                            |> required

                        let snapshot =
                            Case.decide (DateOnly(2026, 11, 2)) command None
                            |> required
                            |> Case.snapshot

                        let request = RequestRecord.encode command
                        let bytes = CaseRecord.encodeSnapshot snapshot

                        let combined =
                            LedgerEntry.encodePayload { Request = request; Snapshot = bytes }

                        Expect.isLessThanOrEqual
                            combined.Length
                            32768
                            "Canonical quotes, slashes and supplementary scalars including framing"

                        Expect.equal
                            (CaseRecord.decodeSnapshot bytes)
                            (Ok snapshot)
                            "Maximum snapshot retains exact description and facts")
        ]
