module ClaimCore.Tests.ReminderSelectionAdapterTests

open System
open System.IO
open System.Text
open Microsoft.VisualBasic.FileIO
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Wire.Export
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private required value =
    Result.defaultWith (string >> failtest) value

let private id number =
    Guid.Parse(sprintf "10000000-0000-4000-8000-%012d" number)

let private query arguments =
    let invocation =
        Grammar.prepare (fun () -> id 99 |> string) ("reminder" :: "list" :: arguments)
        |> required
        |> _.Invocation

    Codec.decode Limits.MaximumRequestBytes Api.reminders.Input (Frames.inputBytes invocation)
    |> required

let private encoded request =
    Codec.encode Api.reminders.Input request |> Encoding.UTF8.GetString

let private page =
    {
        ReminderPage.Basis = DomainInputs.workBasis
        NextCursor = None
        Items =
            [
                1, ReminderState.Pending
                2, ReminderState.Completed
                3, ReminderState.Cancelled
            ]
            |> List.map (fun (number, state) ->
                {
                    ReminderReadModel.Reminder =
                        Reminder.decode
                            (ReminderId.create (id number) |> Option.get)
                            (CaseId.create (id 10) |> Option.get)
                            Revision.first
                            (ReminderAction.parse (sprintf "Row %d" number) |> required)
                            (CalendarDate.parse "2026-01-01" |> required)
                            ReminderKind.FollowUp
                            state
                            (if state = ReminderState.Completed then
                                 Some(CalendarDate.ofDate (System.DateOnly(2026, 11, 18)))
                             else
                                 None)
                        |> required
                    Reference = CaseReference.parse "STATE-OPEN" |> required
                    Claimants = [ (PartyName.parse "Synthetic claimant" |> required) ]
                    Responsible = None
                    RecordedAt = DomainInputs.workBasis.ObservedAt
                    RecordedBy = DomainInputs.actorReference (CaseFixtures.actorId (id 20))
                    Offers = []
                    Active = true
                    Timing =
                        if state = ReminderState.Pending then
                            Some ReminderDue.Overdue
                        else
                            None
                })
    }

let private csvRecords bytes =
    use text = new StringReader(Encoding.UTF8.GetString(bytes: byte array))
    use reader = new TextFieldParser(text)
    reader.SetDelimiters [| "," |]
    reader.HasFieldsEnclosedInQuotes <- true
    let header = reader.ReadFields() |> nonNull |> Array.toList
    let rows = ResizeArray<_>()

    while not reader.EndOfData do
        rows.Add(List.zip header (reader.ReadFields() |> nonNull |> Array.toList) |> Map.ofList)

    List.ofSeq rows

[<Tests>]
let tests =
    testList
        "reminder selection adapter results"
        [
            testCase
                "actual CLI ANY and explicit false survive request Wire while absence remains distinct"
                (fun () ->
                    let selected =
                        query
                            [
                                "--state"
                                "ANY"
                                "--view"
                                "COMPLETED"
                                "--include-inactive=false"
                                "--case-reference"
                                "STATE-OPEN"
                            ]

                    Expect.equal
                        selected.IncludeInactive
                        (Some false)
                        "False is an explicit query restriction"

                    let raw = encoded selected

                    Expect.stringContains
                        raw
                        "\"state\":\"ANY\""
                        "CLI selected explicit ANY, not omitted state"

                    Expect.stringContains
                        raw
                        "\"includeInactive\":false"
                        "The Wire producer preserves false"

                    let roundtrip =
                        Codec.decode
                            Limits.MaximumRequestBytes
                            Api.reminders.Input
                            (Encoding.UTF8.GetBytes raw)
                        |> required

                    Expect.equal
                        roundtrip
                        selected
                        "The actual input codec preserves the full selection"

                    let omitted = query [ "--view"; "COMPLETED" ]

                    Expect.isNone
                        omitted.State
                        "Omitted selector remains absent for the owner's view default"

                    Expect.isNone
                        omitted.IncludeInactive
                        "Omitted inactive permission remains distinct from false"

                    Expect.isFalse
                        ((encoded omitted).Contains "\"ANY\"")
                        "The adapter does not fabricate an explicit selector")
            testCase "Core case link Wire retains explicit ANY and inactive selection" (fun () ->
                let selected =
                    match Link.remindersForCase (CaseSelector.Reference "STATE-OPEN") with
                    | Link.ReminderList value -> value
                    | _ -> failtest "Reminder link required"

                let raw = encoded selected

                Expect.stringContains
                    raw
                    "\"state\":\"ANY\""
                    "The actual producer declares all reminder states"

                Expect.equal
                    selected.IncludeInactive
                    (Some true)
                    "The case link intentionally includes inactive work")
            testCase
                "actual DATA and HUMAN CLI result adapters preserve all three stored states and identities"
                (fun () ->
                    let result = Outcome.Succeeded page
                    let raw = Codec.encode Api.reminders.Output (RepresentationReply.Data result)

                    Expect.equal
                        (Codec.decode Limits.MaximumResponseBytes Api.reminders.Output raw
                         |> required)
                        (RepresentationReply.Data result)
                        "All typed selected rows survive DATA"

                    let text = Encoding.UTF8.GetString raw

                    for state in [ "PENDING"; "COMPLETED"; "CANCELLED" ] do
                        Expect.stringContains
                            text
                            ("\"state\":\"" + state + "\"")
                            "Stored states stay unchanged"

                    Expect.isError
                        (Codec.decode
                            Limits.MaximumResponseBytes
                            Api.reminders.Output
                            (Encoding.UTF8.GetBytes(
                                text.Replace("\"state\":\"PENDING\"", "\"state\":\"ANY\"")
                            )))
                        "ANY is a query selector, never a stored reminder state"

                    let human =
                        CliReplies.ofValue
                            Api.reminders
                            (RepresentationReply.Human(
                                HumanReplies.read
                                    (ReminderListScreen.document (query [ "--state"; "ANY" ]))
                                    result
                            ))

                    let output =
                        ReplyText.resultLinesWithDetails
                            true
                            (RenderingContext.forLanguage "en")
                            None
                            (FrameResult.Reply human)
                        |> String.concat "\n"

                    for number in [ 1; 2; 3 ] do
                        Expect.stringContains
                            output
                            (sprintf "Row %d" number)
                            "No state row is silently dropped by HUMAN/CLI"

                        Expect.stringContains
                            output
                            (string (id number))
                            "Supplied reminder identity remains in the CLI read control"

                    for state in [ "Overdue: 1"; "Completed: 1"; "Cancelled: 1" ] do
                        Expect.stringContains
                            output
                            state
                            "Shared stored-state vocabulary is rendered")
            testCase "CSV writes literal state identities instead of the ANY query token" (fun () ->
                let bytes =
                    Array.append
                        ReminderCsv.csvHeader
                        (ReminderCsv.csvRows (fun key _ -> key) page.Items)

                let rows = csvRecords bytes

                Expect.equal
                    (rows |> List.map (fun row -> row["reminderId"], row["state"]))
                    [
                        string (id 1), "PENDING"
                        string (id 2), "COMPLETED"
                        string (id 3), "CANCELLED"
                    ]
                    "Literal expected stored-state rows remain ordered and complete")
        ]
