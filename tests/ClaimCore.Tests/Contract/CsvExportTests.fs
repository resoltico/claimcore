module ClaimCore.Tests.CsvExportTests

open ClaimCore.Wire.Export

open System
open System.IO
open System.Text
open Microsoft.VisualBasic.FileIO
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private parse bytes =
    use source = new StringReader(Encoding.UTF8.GetString(bytes: byte array))
    use reader = new TextFieldParser(source)
    reader.SetDelimiters [| "," |]
    reader.HasFieldsEnclosedInQuotes <- true
    let header = reader.ReadFields() |> nonNull
    let row = reader.ReadFields() |> nonNull
    Expect.equal row.Length header.Length "Every field has one header"
    List.zip (List.ofArray header) (List.ofArray row) |> Map.ofList

let private parseRows (bytes: byte array) =
    use input = new StringReader(Encoding.UTF8.GetString bytes)
    use reader = new TextFieldParser(input)
    reader.SetDelimiters [| "," |]
    reader.HasFieldsEnclosedInQuotes <- true
    reader.TrimWhiteSpace <- false
    let rows = ResizeArray<string list>()

    while not reader.EndOfData do
        rows.Add(reader.ReadFields() |> nonNull |> Array.toList)

    List.ofSeq rows

[<Tests>]
let tests =
    testCase "CSV exports preserve operation proof and distinct effect provenance" (fun () ->
        let raw = Guid.Parse "019a6b00-0000-7000-8000-000000000001"
        let operation = CaseFixtures.operationId raw
        let actor = DomainInputs.actorReference (CaseFixtures.actorId raw)
        let hash = EntryHash.ofBytes (Array.create 32 0xabuy) |> Option.get
        let position = EffectPosition.create (CaseFixtures.sequence 7L) 3 |> Option.get

        let entry: PeopleHistoryEntry =
            {
                OperationId = operation
                Position = position
                OperationHash = hash
                RecordedAt = DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
                RecordedBy = AuthorityRef.Actor(actor)
                Target = actor
                Task = TaskId.ofLiteral "people.enable"
                Changes =
                    {
                        PersonPartDescription.Profiles = { Before = None; After = None }
                        Principal = None
                        Enabled =
                            Some
                                {
                                    Before = Some false
                                    After = Some true
                                }
                        ProfilePresent = None
                        DisplayName = None
                        Contact = None
                        Grants = []
                    }
                PrivateAvailable = true
                References = { Actors = []; Cases = [] }
            }

        let people =
            Array.append
                PeopleHistoryCsv.csvHeader
                (PeopleHistoryCsv.csvRows
                    (MessageCatalogs.render LocalizationPolicy.defaultLanguage)
                    [ entry ])
            |> parse

        let timeline: CaseTimelineEntry =
            {
                OperationId = operation
                Position = position
                OperationHash = hash
                Subject =
                    {
                        PartKind = PartKind.Case
                        PartId = raw
                        Revision = Revision.first
                    }
                Task = TaskId.ofLiteral "case.open"
                RecordedAt = entry.RecordedAt
                RecordedBy = actor
                Changes = TimelineChanges.Withheld
            }

        let activity =
            Array.append
                TimelineCsv.csvHeader
                (TimelineCsv.csvRows
                    {
                        Disclosure = HistoryDisclosure.Full
                        Basis = DomainInputs.workBasis
                        CaseId = CaseFixtures.caseId raw
                        Reference = DomainInputs.reference "CASE-PROOF"
                        Items = [ timeline ]
                        NextCursor = None
                    })
            |> parse

        for row in [ people; activity ] do
            Expect.equal row["operationId"] (raw.ToString "D") "Stable operation identity"
            Expect.equal row["sequence"] "7" "Sequence is the operation position"
            Expect.equal row["effectIndex"] "3" "Effect ordinal is retained independently"

            Expect.equal
                row["operationHash"]
                (String.replicate 32 "ab")
                "Hash belongs to complete root")

[<Tests>]
let formulaGuard =
    testCase "CSV neutralizes every formula trigger including tabs after leading spaces" (fun () ->
        for prefix in [ ""; "  " ] do
            for trigger in [ '='; '+'; '-'; '@'; '\t'; '\r' ] do
                let authored = prefix + string trigger + "value"

                use source =
                    new StringReader(Encoding.UTF8.GetString(Csv.row [ authored; "sentinel" ]))

                use reader = new TextFieldParser(source)
                reader.SetDelimiters [| "," |]
                reader.HasFieldsEnclosedInQuotes <- true

                Expect.equal
                    (reader.ReadFields() |> nonNull |> List.ofArray)
                    [ "'" + authored; "sentinel" ]
                    "Independent CSV parsing preserves protected content"

        for ordinary in [ ""; "plain"; "  plain"; "a=1" ] do
            Expect.equal (CsvCell.neutralize ordinary) ordinary "Ordinary cells remain exact")

[<Tests>]
let cellEncoding =
    testCase "CSV cell protection handles whitespace and leaves ordinary cells exact" (fun () ->
        for authored, expected in
            [
                "", ""
                "ordinary", "ordinary"
                "a=1+1", "a=1+1"
                "  ordinary", "  ordinary"
                "  =1+1", "'  =1+1"
                " \t+1", "' \t+1"
                "  -1", "'  -1"
                " \t@call", "' \t@call"
                "\tordinary", "'\tordinary"
                "\rordinary", "'\rordinary"
                "  \rordinary", "'  \rordinary"
                "'=1+1", "'=1+1"
                "@call,\"quoted\"\r\nnext", "'@call,\"quoted\"\r\nnext"
            ] do
            let bytes = CsvCell.format authored + ",sentinel\r\n" |> Encoding.UTF8.GetBytes

            Expect.equal (parseRows bytes) [ [ expected; "sentinel" ] ] "Independent CSV readback")

[<Tests>]
let completeByteGoldens =
    testCase
        "four CSV formats retain literal UTF-8 bytes CRLF canonical scalars and protected formulas"
        (fun () ->
            let raw = Guid.Parse "019aa6a0-0000-7000-8000-000000000001"
            let actorRaw = Guid.Parse "019aa6a0-0000-7000-8000-000000000003"
            let targetRaw = Guid.Parse "019aa6a0-0000-7000-8000-000000000004"
            let reminderRaw = Guid.Parse "019aa6a0-0000-7000-8000-000000000002"
            let operation = CaseFixtures.operationId raw

            let actor =
                DomainInputs.namedActorReference (CaseFixtures.actorId actorRaw) "+Recorder, α"

            let target =
                DomainInputs.namedActorReference (CaseFixtures.actorId targetRaw) "=Target α"

            let reference = DomainInputs.reference "CSV-GOLDEN"
            let changed = DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)

            let basis =
                {
                    WorkDateBasis.ObservedAt = changed
                    BusinessDate = DateOnly(2026, 11, 9)
                    TimeZoneId = "Etc/UTC"
                }

            let policyNumber = "=CSV, α \"quoted\""

            let registration =
                DomainInputs.registrationFrom
                    [
                        BusinessField.PolicyNumber, policyNumber
                        BusinessField.InsurerClaimReference, "Insurer"
                        BusinessField.ClaimedAmount, "1.2345"
                        BusinessField.ClaimedCurrency, "EUR"
                    ]
                |> DomainInputs.admittedRegistration

            let fields =
                {
                    CaseFields.CaseReference = reference
                    Handling = Handling.Open
                    Registration = registration
                    Finance = FinancialPosition.undecided
                }

            let caseRow: CaseSummary =
                {
                    CaseId = CaseFixtures.caseId raw
                    Reference = reference
                    Revision = CaseFixtures.revision 7L
                    WorkRevision = CaseFixtures.revision 3L
                    CanAssign = true
                    ChangedAt = changed
                    State =
                        CaseState.Active
                            {
                                Fields =
                                    CaseSummaryFields.values CaseSummaryFields.declarations fields
                                Parties = Map.empty
                            }
                    Headline = CaseHeadline.Undecided
                    Attention = [ CaseAttention.NoNextAction; CaseAttention.Unassigned ]
                    Stages = None
                    NextAction = None
                    Responsible = None
                    PendingReminders = 0L
                    NetPaid = None
                    PaymentCount = 0L
                    LastPaymentDate = None
                }

            let expected header cells =
                Encoding.UTF8.GetBytes(header + "\r\n" + String.concat "," cells + "\r\n")

            let caseBytes = Array.append CaseCsv.csvHeader (CaseCsv.csvRows basis [ caseRow ])

            Expect.equal
                caseBytes
                (expected
                    "caseReference,revision,workRevision,state.kind,headline,attention,state.fields.policyNumber,state.fields.insurerClaimReference,state.fields.claimedAmount,state.fields.claimedCurrency,state.fields.payableAmount,state.fields.payableCurrency,paidAmount,paymentCount,lastPaymentDate,responsible.actorId,responsible.displayName.value,responsible.displayName.kind,responsible.availability,nextAction.reminderId,nextAction.action,nextAction.due,nextAction.dueState,pendingReminders,changedAt,basis.businessDate,basis.timeZone"
                    [
                        "CSV-GOLDEN"
                        "7"
                        "3"
                        "ACTIVE"
                        "UNDECIDED"
                        "NO_NEXT_ACTION;UNASSIGNED"
                        "\"'=CSV, α \"\"quoted\"\"\""
                        "Insurer"
                        "1.2345"
                        "EUR"
                        ""
                        ""
                        ""
                        "0"
                        ""
                        ""
                        ""
                        ""
                        ""
                        ""
                        ""
                        ""
                        ""
                        "0"
                        "2026-11-09T12:00:00.000000Z"
                        "2026-11-09"
                        "Etc/UTC"
                    ])
                "Independent case byte vector"

            let due =
                CalendarDate.parse "2026-11-20" |> Result.defaultWith (fun _ -> failtest "Date")

            let reminder =
                Reminder.ofAccepted
                    (ReminderId.create reminderRaw |> Option.get)
                    (CaseFixtures.caseId raw)
                    (CaseFixtures.revision 3L)
                    (ReminderAction.parse "+CSV, α \"quoted\""
                     |> Result.defaultWith (fun _ -> failtest "Action"))
                    due
                    ReminderState.Pending
                |> Result.defaultWith failtest

            let row: ReminderReadModel =
                {
                    Reminder = reminder
                    Reference = reference
                    Claimants =
                        [
                            PartyName.parse policyNumber
                            |> Result.defaultWith (fun _ -> failtest "Claimant")
                        ]
                    Responsible = None
                    RecordedAt = changed
                    RecordedBy = actor
                    Offers = []
                    Active = true
                    Timing = Some ReminderDue.Upcoming
                }

            let render = MessageCatalogs.render LocalizationPolicy.defaultLanguage

            let reminderBytes =
                Array.append ReminderCsv.csvHeader (ReminderCsv.csvRows render [ row ])

            Expect.equal
                reminderBytes
                (expected
                    "caseId,caseReference,reminderId,revision,action,dueDate,state,recordedAt,recordedBy.actorId,recordedBy.displayName,active"
                    [
                        "019aa6a0-0000-7000-8000-000000000001"
                        "CSV-GOLDEN"
                        "019aa6a0-0000-7000-8000-000000000002"
                        "3"
                        "\"'+CSV, α \"\"quoted\"\"\""
                        "2026-11-20"
                        "PENDING"
                        "2026-11-09T12:00:00.000000Z"
                        "019aa6a0-0000-7000-8000-000000000003"
                        "\"'+Recorder, α\""
                        "true"
                    ])
                "Independent reminder byte vector"

            let hash = EntryHash.ofBytes (Array.create 32 0xabuy) |> Option.get
            let position = EffectPosition.create (CaseFixtures.sequence 7L) 3 |> Option.get

            let person: PeopleHistoryEntry =
                {
                    OperationId = operation
                    Position = position
                    OperationHash = hash
                    RecordedAt = changed
                    RecordedBy = AuthorityRef.Actor(actor)
                    Target = target
                    Task = TaskId.ofLiteral "people.enable"
                    Changes =
                        {
                            PersonPartDescription.Profiles = { Before = None; After = None }
                            Principal = None
                            Enabled =
                                Some
                                    {
                                        Before = Some false
                                        After = Some true
                                    }
                            ProfilePresent = None
                            DisplayName = None
                            Contact = None
                            Grants = []
                        }
                    PrivateAvailable = true
                    References = { Actors = []; Cases = [] }
                }

            let peopleBytes =
                Array.append
                    PeopleHistoryCsv.csvHeader
                    (PeopleHistoryCsv.csvRows render [ person ])

            Expect.equal
                peopleBytes
                (expected
                    "operationId,sequence,effectIndex,operationHash,recordedAt,recordedBy.actorId,recordedBy.displayName,target.actorId,target.displayName,task,privateAvailable,field,before,after"
                    [
                        "019aa6a0-0000-7000-8000-000000000001"
                        "7"
                        "3"
                        String.replicate 32 "ab"
                        "2026-11-09T12:00:00.000000Z"
                        "019aa6a0-0000-7000-8000-000000000003"
                        "\"'+Recorder, α\""
                        "019aa6a0-0000-7000-8000-000000000004"
                        "'=Target α"
                        "people.enable"
                        "true"
                        "enabled"
                        "false"
                        "true"
                    ])
                "Independent people-history byte vector"

            let timeline: CaseTimelineEntry =
                {
                    OperationId = operation
                    Position = position
                    OperationHash = hash
                    Subject =
                        {
                            PartKind = PartKind.Case
                            PartId = raw
                            Revision = Revision.first
                        }
                    Task = TaskId.ofLiteral "case.open"
                    RecordedAt = changed
                    RecordedBy = actor
                    Changes = TimelineChanges.Withheld
                }

            let timelineBytes =
                Array.append
                    TimelineCsv.csvHeader
                    (TimelineCsv.csvRows
                        {
                            Disclosure = HistoryDisclosure.Full
                            Basis = basis
                            CaseId = CaseFixtures.caseId raw
                            Reference = reference
                            Items = [ timeline ]
                            NextCursor = None
                        })

            Expect.equal
                timelineBytes
                (expected
                    "caseReference,operationId,sequence,effectIndex,operationHash,recordedAt,recordedBy.actorId,recordedBy.displayName.value,recordedBy.displayName.kind,act,revision,reminderId,nextReminderId,nextReminderRevision,changes.kind,field,before,after"
                    [
                        "CSV-GOLDEN"
                        "019aa6a0-0000-7000-8000-000000000001"
                        "7"
                        "3"
                        String.replicate 32 "ab"
                        "2026-11-09T12:00:00.000000Z"
                        "019aa6a0-0000-7000-8000-000000000003"
                        "\"'+Recorder, α\""
                        "NAMED"
                        "case.open"
                        "1"
                        ""
                        ""
                        ""
                        "WITHHELD"
                        ""
                        ""
                        ""
                    ])
                "Independent timeline byte vector")
