module ClaimCore.Tests.PresentationCodecTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private read value = PresentationCodecs.value.Read "$" value

let private money amount scale =
    JsonValue.Object
        [
            "kind", JsonValue.String "MONEY"
            "amount", JsonValue.String amount
            "currency", JsonValue.String "EUR"
            "displayScale", scale
        ]

[<Tests>]
let tests =
    testList
        "review presentation transport"
        [
            testCase "provided scale survives admission reencoding and human rendering" (fun () ->
                let provided = money "1" (JsonValue.Integer 4L)
                let value = read provided |> CaseBuilders.accepted

                match value with
                | PresentedValue.Money value ->
                    Expect.equal
                        value.DisplayScale
                        4
                        "The supplied scale is retained, not recomputed to EUR minimum two"
                | _ -> failtest "Expected money"

                Expect.equal
                    (PresentationCodecs.value.Write value)
                    provided
                    "No facet metadata is masked during decode/write"

                Expect.equal
                    (TextDescriptorValue.render (RenderingContext.forLanguage "en") value)
                    "1.0000 EUR"
                    "The human consumer uses supplied scale"

                let lower = read (money "1.2345" (JsonValue.Integer 0L)) |> CaseBuilders.accepted

                Expect.equal
                    (TextDescriptorValue.render (RenderingContext.forLanguage "en") lower)
                    "1.2345 EUR"
                    "Formatting never removes meaningful exact digits")
            testCase
                "missing wrong typed and out of range scales are structural refusals"
                (fun () ->
                    for scale in
                        [
                            JsonValue.String "4"
                            JsonValue.Integer -1L
                            JsonValue.Integer 5L
                            JsonValue.Null
                        ] do
                        Expect.isError
                            (read (money "1" scale))
                            "Native scale grammar matches generated integer range"

                    Expect.isError
                        (read (
                            JsonValue.Object
                                [
                                    "kind", JsonValue.String "MONEY"
                                    "amount", JsonValue.String "1"
                                    "currency", JsonValue.String "EUR"
                                ]
                        ))
                        "Scale is required"

                    Expect.isError
                        (read (
                            JsonValue.Object
                                [
                                    "kind", JsonValue.String "UNKNOWN"
                                    "value", JsonValue.String "x"
                                ]
                        ))
                        "Unknown semantic tags refuse"

                    Expect.isError
                        (PresentationCodecs.review.Read
                            "$"
                            (JsonValue.Object [ "changes", JsonValue.Array [] ]))
                        "Unchanged identity list is required")
            testCase "server builder retains the authoritative typed case change values" (fun () ->
                let before = Identifier.parse "Authoritative᠎Before" |> CaseBuilders.accepted
                let after = Identifier.parse "Authoritative👩‍💻After" |> CaseBuilders.accepted

                let original =
                    Reviews.caseReview
                        (TaskId.ofLiteral "case.void")
                        (Binding.Case(
                            CaseSelector.Id CliReplyFixtures.snapshot.CaseId,
                            CliReplyFixtures.snapshot.Revision
                        ))
                        (Some CliReplyFixtures.snapshot)
                        CliReplyFixtures.snapshot
                        {
                            ProductVersion = "fixture"
                            Basis = DomainInputs.workBasis
                        }

                let changes =
                    original.Changes
                    |> List.map (fun part ->
                        match part.Description with
                        | PartDescription.Case value ->
                            { part with
                                Description =
                                    PartDescription.Case
                                        { value with
                                            Fields =
                                                [
                                                    {
                                                        FieldName = BusinessField.PolicyNumber
                                                        Before =
                                                            Some(
                                                                CaseFieldValue.Business(
                                                                    Admitted.Identifier before
                                                                )
                                                            )
                                                        After =
                                                            Some(
                                                                CaseFieldValue.Business(
                                                                    Admitted.Identifier after
                                                                )
                                                            )
                                                    }
                                                ]
                                        }
                            }
                        | _ -> failtest "Case part required")

                let review = { original with Changes = changes }

                let supplied =
                    HumanReviewDocument.ofSubmission ResponseExamples.previewInput review

                let change =
                    supplied.Presentation.Changes
                    |> List.find (fun change ->
                        change.Key.EndsWith(
                            "." + BusinessField.key BusinessField.PolicyNumber,
                            StringComparison.Ordinal
                        ))

                Expect.equal
                    change.Before
                    (PresentedValue.Text(Identifier.format before))
                    "Snapshot text does not replace the supplied core change"

                Expect.equal
                    change.After
                    (PresentedValue.Text(Identifier.format after))
                    "Each side retains its own exact value"

                Expect.isTrue
                    (TextDescriptorValue.render (RenderingContext.forLanguage "en") change.Before
                     |> fun value -> value.Contains "U+180E")
                    "Character-warning protection survives typed changes")
        ]

[<Tests>]
let producerBudgets =
    testCase
        "bounded ADMIT work narrative and complete-next review replies fit the unchanged response budget"
        (fun () ->
            let context =
                {
                    ProductVersion = "fixture"
                    Basis = DomainInputs.workBasis
                }

            let encode review =
                let reply =

                    {
                        PreviewReply.OperationId = CliReplyFixtures.id
                        Outcome = Outcome.Succeeded(Previewed.Reviewed review)
                    }

                CliReplies.encodeData Api.operationPreview reply

            let profile =
                ActorProfile.parse
                    {
                        DisplayName = String.replicate PartyName.MaximumCharacters "😀"
                        DigestEmail = None
                        Contact = Some(String.replicate PartyName.MaximumCharacters "😀")
                    }
                |> CaseBuilders.accepted

            let principal =
                PrincipalKey.human "https://budget.example/" "new-person"
                |> CaseBuilders.accepted

            let grants =
                [
                    for number in 1 .. PeopleAdmission.MaximumGrants ->
                        {
                            ScopedGrant.Role = Role.CaseReader
                            Scope =
                                GrantScope.Case(
                                    CaseFixtures.caseId (
                                        Guid.Parse(sprintf "0f82efaf-3882-7f58-b639-%012d" number)
                                    ),
                                    CaseFixtures.reference (
                                        number.ToString("D2")
                                        + String('"', CaseReference.MaximumCharacters - 2)
                                    )
                                )
                        }
                ]

            let grantFacts =
                grants
                |> List.map (fun grant ->
                    let scope =
                        match grant.Scope with
                        | GrantScope.Installation -> Scope.Installation
                        | GrantScope.Case(id, _) -> Scope.Case id

                    {
                        GrantAssignmentChange.Grant = { Role = grant.Role; Scope = scope }
                        Before = false
                        After = true
                    })

            let references =
                {
                    ChangeReferences.Actors = []
                    Cases =
                        grants
                        |> List.choose (fun grant ->
                            match grant.Scope with
                            | GrantScope.Case(id, reference) ->
                                Some
                                    {
                                        CaseReferenceItem.CaseId = id
                                        Reference = reference
                                    }
                            | _ -> None)
                }

            let description =
                {
                    PersonPartDescription.Profiles = { Before = None; After = Some profile }
                    Principal =
                        Some
                            {
                                Before = None
                                After = Some principal
                            }
                    Enabled = Some { Before = None; After = Some true }
                    ProfilePresent = Some { Before = None; After = Some true }
                    DisplayName =
                        Some
                            {
                                Before = None
                                After = Some profile.DisplayName
                            }
                    Contact =
                        Some
                            {
                                Before = None
                                After = profile.Contact
                            }
                    DigestEmail = None
                    Grants = grantFacts
                }

            let admission =
                Reviews.personReview
                    (TaskId.ofLiteral "people.admit")
                    (Binding.PeopleNew Revision.zero)
                    None
                    CliReplyFixtures.id
                    Revision.first
                    description
                    references
                    context

            let beforeWork =
                {
                    CaseWorkReadModel.CaseId = CliReplyFixtures.snapshot.CaseId
                    Revision = Revision.first
                    Responsible = None
                    Summary =
                        HandlingSummary.parse (String('"', HandlingSummary.MaximumBytes))
                        |> Result.toOption
                }

            let afterWork =
                { beforeWork with
                    Revision = CaseFixtures.revision 2L
                    Summary = HandlingSummary.parse (String.replicate 600 "😀") |> Result.toOption
                }

            let work =
                Reviews.workReview
                    (TaskId.ofLiteral "work.update")
                    (Binding.Work(CaseSelector.Id beforeWork.CaseId, beforeWork.Revision))
                    CliReplyFixtures.snapshot
                    beforeWork
                    afterWork
                    context

            let reminder id revision action state =
                Reminder.decode
                    (ReminderId.create id |> Option.get)
                    CliReplyFixtures.snapshot.CaseId
                    (CaseFixtures.revision revision)
                    (ReminderAction.parse action |> CaseBuilders.accepted)
                    (DomainInputs.date "2026-11-04")
                    ReminderKind.FollowUp
                    state
                    (if state = ReminderState.Completed then
                         Some(CalendarDate.ofDate (System.DateOnly(2026, 11, 18)))
                     else
                         None)
                |> CaseBuilders.accepted

            let old =
                reminder CliReplyFixtures.id 1L (String.replicate 200 "😀") ReminderState.Pending

            let completed =
                reminder CliReplyFixtures.id 2L (String.replicate 200 "😀") ReminderState.Completed

            let next =
                reminder
                    (Guid.Parse "0f82efaf-3882-7f58-b639-999999999999")
                    1L
                    (String.replicate 200 "😀")
                    ReminderState.Pending

            let binding =
                Binding.Reminder(
                    CaseSelector.Id CliReplyFixtures.snapshot.CaseId,
                    Reminder.id old,
                    Reminder.revision old
                )

            let completeNext =
                Reviews.reminderReview
                    (TaskId.ofLiteral "reminder.completeAndPlanNext")
                    binding
                    CliReplyFixtures.snapshot
                    (Some old)
                    [ completed; next ]
                    context

            let suppliedReminder =
                HumanReviewDocument.ofSubmission
                    (Submissions.create
                        "reminder.completeAndPlanNext"
                        CliReplyFixtures.id
                        binding
                        (Map.ofList
                            [
                                Submissions.input InputTarget.ReminderAction,
                                SubmissionValue.Text(ReminderAction.format (Reminder.action next))
                                Submissions.input InputTarget.ReminderDueDate,
                                SubmissionValue.Text(CalendarDate.format (Reminder.due next))
                            ]))
                    completeNext

            let identityValues =
                suppliedReminder.Document.Screen.Regions
                |> List.collect (fun region ->
                    match region.Role, region.Content with
                    | _, RegionContent.Entries entries -> entries |> List.map _.Value
                    | _ -> [])

            Expect.contains
                identityValues
                (PresentedValue.Text(ReminderAction.format (Reminder.action old)))
                "The review identifies the reminder being completed without relying on the client's typed snapshot"

            for name, review in
                [
                    "48-grant ADMIT", admission
                    "historical escaped work before and admitted after", work
                    "complete and plan next", completeNext
                ] do
                let bytes = encode review

                Expect.isLessThan
                    bytes.Length
                    Limits.MaximumResponseBytes
                    (name + " fits the real unchanged four MiB frame")

                Expect.isOk
                    (Codec.decode Limits.MaximumResponseBytes Api.operationPreview.Output bytes)
                    (name + " decodes through the actual endpoint codec")

            Expect.equal Limits.MaximumResponseBytes 4194304 "No response gate was raised")


[<Tests>]
let primaryPeopleChanges =
    testCase
        "overlapping and disabled accounts retain explicit assignment changes without capability rows"
        (fun () ->
            for enabled in [ true; false ] do
                let actor = ActorId.create CliReplyFixtures.id |> Option.get

                let description =
                    {
                        PersonPartDescription.Profiles = { Before = None; After = None }
                        Principal = None
                        Enabled = None
                        ProfilePresent = None
                        DisplayName = None
                        DigestEmail = None
                        Contact = None
                        Grants =
                            [
                                {
                                    Grant =
                                        {
                                            Role = Role.CaseReader
                                            Scope = Scope.Installation
                                        }
                                    Before = false
                                    After = true
                                }
                            ]
                    }

                let value =
                    Reviews.personReview
                        (TaskId.ofLiteral "people.grant")
                        (Binding.People(actor, Revision.first))
                        (Some
                            { DomainInputs.actorReference actor with
                                Availability =
                                    if enabled then
                                        ActorAvailability.Available
                                    else
                                        ActorAvailability.Disabled
                            })
                        CliReplyFixtures.id
                        (CaseFixtures.revision 2L)
                        description
                        { Actors = []; Cases = [] }
                        {
                            ProductVersion = "fixture"
                            Basis = DomainInputs.workBasis
                        }

                let input =
                    Submissions.create
                        "people.grant"
                        CliReplyFixtures.id
                        value.Binding
                        (Submissions.grant
                            {
                                Role = Role.CaseReader
                                Scope = GrantTarget.Installation
                            })

                let result = HumanReviewDocument.ofSubmission input value

                let keys =
                    (result.Presentation.Changes |> List.map _.Key)
                    @ (result.Presentation.Unchanged |> List.map _.Key)

                Expect.isFalse
                    (keys
                     |> List.exists (fun key ->
                         key.StartsWith("READ_CASE.", StringComparison.Ordinal)
                         || key.StartsWith("READ_HISTORY.", StringComparison.Ordinal)))
                    "Primary assignment does not invent capability rows"

                Expect.equal
                    (result.Presentation.Changes |> List.map _.Key)
                    [ CliReplyFixtures.id.ToString("D") + ".role.0" ]
                    "One authoritative assignment"

                let provided =
                    ReplyCodecs.review.Read "$" (ReplyCodecs.review.Write value)
                    |> CaseBuilders.accepted

                match provided.Changes.Head.Description with
                | PartDescription.Person provided ->
                    Expect.equal
                        provided.Grants
                        description.Grants
                        "Explicit assignment survives DATA"

                    Expect.isNone
                        provided.Enabled
                        "Granting a role does not invent an enabled-state change"
                | _ -> failtest "Person part required")
