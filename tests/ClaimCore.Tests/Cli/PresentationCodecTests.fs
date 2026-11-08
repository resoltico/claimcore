module ClaimCore.Tests.PresentationCodecTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
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
                let value = read provided |> Fixtures.accepted

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
                    (TextDescriptorValue.render "en" value)
                    "1.0000 EUR"
                    "The human consumer uses supplied scale"

                let lower = read (money "1.2345" (JsonValue.Integer 0L)) |> Fixtures.accepted

                Expect.equal
                    (TextDescriptorValue.render "en" lower)
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
                let before = PartyName.parse "Authoritative᠎Before" |> Fixtures.accepted
                let after = PartyName.parse "Authoritative👩‍💻After" |> Fixtures.accepted

                let review =
                    OperationReview.Case
                        {
                            Before = Some CliReplyFixtures.snapshot
                            Proposed = CliReplyFixtures.snapshot
                            Context =
                                {
                                    ProductVersion = "fixture"
                                    Basis = Inputs.workBasis
                                }
                            Changes =
                                {
                                    Lifecycle = None
                                    Fields =
                                        [
                                            {
                                                FieldName = BusinessField.ClaimantName
                                                Before =
                                                    Some(
                                                        CaseFieldValue.Business(
                                                            Admitted.Name before
                                                        )
                                                    )
                                                After =
                                                    Some(
                                                        CaseFieldValue.Business(
                                                            Admitted.Name after
                                                        )
                                                    )
                                            }
                                        ]
                                }
                        }

                let supplied = PresentedReview.ofCore None review

                let change =
                    supplied.Presentation.Changes
                    |> List.find (fun change ->
                        change.Key = BusinessField.key BusinessField.ClaimantName)

                Expect.equal
                    change.Before
                    (PresentedValue.Party before)
                    "Snapshot text does not replace the supplied core change"

                Expect.equal
                    change.After
                    (PresentedValue.Party after)
                    "Each side retains its own exact value"

                Expect.isTrue
                    (TextDescriptorValue.render "en" change.Before
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
                    Basis = Inputs.workBasis
                }

            let encode review =
                let reply =
                    PresentedPreviewReply.ofCore
                        None
                        {
                            PreviewReply.OperationId = CliReplyFixtures.id
                            Outcome = Outcome.Succeeded(Previewed.Reviewed review)
                        }

                Codec.encode Api.operationPreview.Output reply

            let profile =
                ActorProfile.parse
                    {
                        DisplayName = String.replicate PartyName.MaximumCharacters "😀"
                        Contact = Some(String.replicate PartyName.MaximumCharacters "😀")
                    }
                |> Fixtures.accepted

            let principal =
                PrincipalKey.human "https://budget.example/" "new-person" |> Fixtures.accepted

            let grants =
                [
                    for number in 1 .. AuthorityAdmission.MaximumGrants ->
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

            let admission =
                OperationReview.Authority
                    {
                        Basis = Inputs.workBasis
                        Effects =
                            [
                                AuthorityReviewEffect.Register principal
                                AuthorityReviewEffect.SetProfile profile
                            ]
                            @ (grants |> List.map AuthorityReviewEffect.Grant)
                        Target = None
                        AccessScope = GrantScope.Installation
                        Before =
                            {
                                Profile = None
                                Enabled = false
                                Grants = []
                                Access = []
                            }
                        After =
                            {
                                Profile = Some profile
                                Enabled = true
                                Grants = grants
                                Access = []
                            }
                    }

            let beforeWork =
                {
                    CaseWorkReadModel.CaseId = CliReplyFixtures.snapshot.CaseId
                    Revision = Revision.first
                    Responsible = None
                    Summary =
                        HandlingSummary.parse (String('"', HandlingSummary.MaximumBytes))
                        |> Result.toOption
                }

            let work =
                OperationReview.Work
                    {
                        Parent = CliReplyFixtures.snapshot
                        Reference = CliReplyFixtures.snapshot.Reference
                        Before = beforeWork
                        Proposed =
                            { beforeWork with
                                Summary =
                                    HandlingSummary.parse (String.replicate 600 "😀")
                                    |> Result.toOption
                            }
                        Context = context
                    }

            let reminder id revision action state =
                Reminder.ofAccepted
                    (ReminderId.create id |> Option.get)
                    CliReplyFixtures.snapshot.CaseId
                    (CaseFixtures.revision revision)
                    (ReminderAction.parse action |> Fixtures.accepted)
                    (Inputs.date "2026-11-04")
                    state
                |> Fixtures.accepted

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

            let completeNext =
                OperationReview.Reminder
                    {
                        Parent = CliReplyFixtures.snapshot
                        Reference = CliReplyFixtures.snapshot.Reference
                        CaseId = CliReplyFixtures.snapshot.CaseId
                        Before = Some old
                        Effects = ReminderEffects.CompletedAndPlanned(completed, next)
                        Context = context
                    }

            let suppliedReminder = PresentedReview.ofCore None completeNext

            let identityValues =
                suppliedReminder.View.Regions
                |> List.collect (fun region ->
                    match region.Role, region.Content with
                    | RegionRole.Identity, RegionContent.Entries entries ->
                        entries |> List.map _.Value
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
let primaryAuthorityChanges =
    testCase
        "overlapping and disabled authority reviews keep authored changes without ambiguous access rows"
        (fun () ->
            let reference = CaseFixtures.reference "OVERLAP"

            let scoped =
                {
                    ScopedGrant.Role = Role.CaseReader
                    Scope = GrantScope.Case(CliReplyFixtures.snapshot.CaseId, reference)
                }

            let installation =
                {
                    ScopedGrant.Role = Role.CaseReader
                    Scope = GrantScope.Installation
                }

            let access enabled roles =
                [
                    for capability in [ Capability.ReadCase; Capability.ReadHistory ] ->
                        {
                            CapabilityAccess.Capability = capability
                            Effective = enabled
                            DirectRoles = roles
                            InstallationRoles = [ Role.Owner ]
                        }
                ]

            for enabled in [ true; false ] do
                let before =
                    {
                        AuthorityState.Profile = None
                        Enabled = enabled
                        Grants = [ scoped ]
                        Access = access enabled []
                    }

                let after =
                    { before with
                        Grants = [ scoped; installation ]
                        Access = access enabled [ Role.CaseReader ]
                    }

                let value =
                    {
                        AuthorityReview.Basis = Inputs.workBasis
                        Target = None
                        AccessScope = GrantScope.Installation
                        Before = before
                        After = after
                        Effects = [ AuthorityReviewEffect.Grant installation ]
                    }

                let result = PresentedReview.ofCore None (OperationReview.Authority value)

                let keys =
                    (result.Presentation.Changes |> List.map _.Key)
                    @ (result.Presentation.Unchanged |> List.map _.Key)

                Expect.isFalse
                    (keys
                     |> List.exists (fun key ->
                         key.StartsWith("READ_CASE.", StringComparison.Ordinal)
                         || key.StartsWith("READ_HISTORY.", StringComparison.Ordinal)))
                    "Primary presentation never loses capability identity in repeated access-source rows"

                Expect.equal
                    (result.Presentation.Changes |> List.map _.Key)
                    [ "CASE_READER.installation.role"; "CASE_READER.installation.scope" ]
                    "Authored role and scope remain explicit"

                match result.Subject with
                | PresentedReviewSubject.Authority provided ->
                    Expect.equal
                        provided.Before.Access
                        before.Access
                        "The full correctly grouped before-access contract remains available"

                    Expect.equal
                        provided.After.Access
                        after.Access
                        "The full correctly grouped after-access contract remains available"

                    Expect.equal
                        provided.Effects
                        [ AuthorityReviewEffect.Grant installation ]
                        "No authored effect is removed"
                | _ -> failtest "Expected authority")
