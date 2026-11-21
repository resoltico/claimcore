module ClaimCore.Tests.DisclosureTrailTests

open System
open Expecto
open ClaimCore.Application.Contracts
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Presentation
open ClaimCore.TestSupport
open ClaimCore.Wire

let private id = "10000000-0000-7000-8000-000000000001"
let private caseId = "20000000-0000-7000-8000-000000000001"
let private attachmentId = "30000000-0000-7000-8000-000000000001"

let private accepted value =
    value |> Result.defaultWith (fun error -> failtestf "%A" error)

let private filters =
    [
        "disclosure"
        "trail"
        "--actor-id"
        id
        "--case-id"
        caseId
        "--kind"
        "attachment-preview"
        "--from"
        "2026-11-04T10:30:00.000000Z"
        "--until"
        "2026-11-04T11:30:00.000000Z"
        "--limit"
        "1"
        "--all"
    ]

let private request frame =
    Frames.value Api.disclosureTrail frame
    |> Option.defaultWith (fun () -> failtest "Expected trail invocation")

let private continuation () =
    let prepared = Grammar.prepare (fun () -> id) filters |> accepted
    Expect.isTrue prepared.All "The declared paged endpoint supports --all"

    let selected = request prepared.Invocation

    Expect.equal
        selected
        {
            ActorId = ActorId.create (Guid.Parse id)
            CaseId = CaseId.create (Guid.Parse caseId)
            Kind = Some DisclosureKind.AttachmentPreview
            From = Some(DateTimeOffset(2026, 11, 4, 10, 30, 0, TimeSpan.Zero))
            Until = Some(DateTimeOffset(2026, 11, 4, 11, 30, 0, TimeSpan.Zero))
            AfterCursor = None
            Limit = 1
        }
        "The command submits each typed disclosure filter"

    let next = Frames.withCursor (Some "older-events") prepared.Invocation |> accepted

    Expect.equal
        (request next)
        { selected with
            AfterCursor = Some "older-events"
        }
        "Continuation preserves all filters and only changes the cursor"

    Expect.isError
        (Grammar.prepare (fun () -> id) [ "disclosure"; "trail"; "--case-id"; "CASE-001" ])
        "The filter requires the case UUID rather than a human case reference"

let private humanTable () =
    let event =
        {
            ActorId = ActorId.create (Guid.Parse id) |> Option.get
            Kind = DisclosureKind.Export
            CaseId = None
            AttachmentId = None
            Outcome = DisclosureOutcome.Succeeded
            Export =
                Some
                    {
                        Query = "case.list"
                        Selection = Map.ofList [ "view", "NEEDS_ATTENTION" ]
                        RowCount = 12
                    }
        }

    let document =
        HumanReadDocuments.disclosureTrail
            {
                Basis = DomainInputs.workBasis
                Items =
                    [
                        {
                            Id = 1L
                            RecordedAt = DomainInputs.workBasis.ObservedAt
                            Event = event
                        }
                        {
                            Id = 2L
                            RecordedAt = DomainInputs.workBasis.ObservedAt
                            Event =
                                { event with
                                    Kind = DisclosureKind.AttachmentDownload
                                    CaseId = CaseId.create (Guid.Parse caseId)
                                    AttachmentId = AttachmentId.create (Guid.Parse attachmentId)
                                    Export = None
                                }
                        }
                    ]
                NextCursor = Some "older-events"
            }

    let output =
        ViewText.renderDocument true (RenderingContext.forLanguage "en") document
        |> String.concat "\n"

    for value in
        [
            id
            caseId
            attachmentId
            "Export"
            "case.list"
            "view=NEEDS_ATTENTION"
            "12"
        ] do
        Expect.stringContains output value "Exact disclosure data is present in --details output"

    Expect.equal
        document.NextCursor
        (Some "older-events")
        "The continuation is available to CLI paging"

[<Tests>]
let tests =
    testList
        "disclosure trail CLI"
        [
            testCase "typed filters survive paging" continuation
            testCase "human table shows recorded export details" humanTable
        ]
