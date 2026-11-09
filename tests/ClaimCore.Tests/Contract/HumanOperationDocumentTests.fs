module ClaimCore.Tests.HumanOperationDocumentTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private accepted value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private mutate name replacement =
    function
    | JsonValue.Object members ->
        JsonValue.Object(
            members
            |> List.map (fun (key, value) -> key, if key = name then replacement else value)
        )
    | _ -> failtest "Expected an authored object"

let private containsMember name value =
    let rec walk =
        function
        | JsonValue.Object members ->
            members |> List.exists (fun (key, value) -> key = name || walk value)
        | JsonValue.Array values -> List.exists walk values
        | _ -> false

    walk value

let private authorityReview () =
    let principal =
        PrincipalKey.human "https://issuer.example/" "review-subject" |> accepted

    let actor =
        Inputs.namedActorReference
            (CaseFixtures.actorId (Guid.Parse "78280331-c97d-4be4-8031-f5dbd4a65282"))
            "Reviewed actor🪙"

    let state =
        {
            AuthorityState.Profile = None
            Enabled = true
            Grants = []
            Access = []
        }

    let input =
        OperationInput.Authority
            {
                OperationId = OperationId.value ResponseExamples.receipt.OperationId
                Target =
                    {
                        Kind = PrincipalKind.Human
                        Issuer = "https://issuer.example/"
                        Subject = "review-subject"
                    }
                Act = AuthorityAct.EraseProfile
            }

    let review =
        OperationReview.Authority
            {
                Basis = Inputs.workBasis
                Effects = [ AuthorityReviewEffect.EraseProfile ]
                Target =
                    Some
                        {
                            Actor = actor
                            Principal = principal
                            Contact = None
                            State = AccountState.NoRoles
                            Roles = []
                            ResponsibleCases = None
                        }
                AccessScope = GrantScope.Installation
                Before = state
                After = state
            }

    input, review, actor

[<Tests>]
let tests =
    testList
        "human operation documents"
        [
            testCase
                "review carries core confirmation and bindings without full snapshots"
                (fun () ->
                    let input, review, actor = authorityReview ()
                    let result = HumanReviewDocument.ofCore input review

                    match result.Document.Context with
                    | InteractionContext.Review binding ->
                        Expect.equal
                            binding.Task
                            (TaskId.Operation(ActKind.Authority AuthorityKind.EraseProfile))
                            "Semantic task"

                        Expect.equal
                            binding.Consequence
                            Consequence.Irreversible
                            "Core consequence"

                        Expect.equal
                            binding.Target
                            (ReviewTarget.Authority(Some actor.ActorId, GrantScope.Installation))
                            "Resolved target"

                        Expect.equal
                            binding.Confirmation
                            (ConfirmationTarget.Operation(OperationInput.operationId input))
                            "Exact confirmation target supplied by core"

                        Expect.contains
                            binding.Inputs
                            (TaskInputId.Target InputTarget.PrincipalSubject)
                            "Semantic input binding"
                    | _ -> failtest "Review context required"

                    let wire = HumanDocumentCodecs.reviewDocument.Write result

                    for retired in [ "subject"; "proposed"; "snapshot" ] do
                        Expect.isFalse
                            (containsMember retired wire)
                            ("No duplicated raw review " + retired)

                    HumanDocumentCodecs.reviewDocument.Read "$" wire |> accepted |> ignore

                    let unrelated =
                        HumanDocument.empty HumanDisclosure.None
                        |> HumanDocumentCodecs.document.Write
                        |> fun document -> mutate "document" document wire

                    Expect.isError
                        (HumanDocumentCodecs.reviewDocument.Validate "$" unrelated)
                        "A structurally valid unrelated document cannot be a review")

            testCase
                "structural review binding refuses mismatched task target input and consequence"
                (fun () ->
                    let input, review, _ = authorityReview ()
                    let document = (HumanReviewDocument.ofCore input review).Document
                    let wire = HumanDocumentCodecs.document.Write document
                    HumanDocumentCodecs.document.Validate "$" wire |> accepted |> ignore
                    let context = JsonValue.tryMember "context" wire |> Option.get

                    for altered in
                        [
                            mutate "task" (JsonValue.String "query:context") context
                            mutate "inputs" (JsonValue.Array []) context
                            mutate "consequence" (JsonValue.String "ROUTINE") context
                            mutate
                                "target"
                                (JsonValue.Object
                                    [
                                        "kind", JsonValue.String "CASE"
                                        "caseId",
                                        JsonValue.String(
                                            CaseId.format ResponseExamples.snapshot.CaseId
                                        )
                                        "beforeRevision", JsonValue.Null
                                    ])
                                context
                            mutate
                                "confirmation"
                                (JsonValue.Object
                                    [
                                        "kind", JsonValue.String "CASE_REFERENCE"
                                        "reference", JsonValue.String "UNRELATED"
                                    ])
                                context
                        ] do
                        Expect.isError
                            (HumanDocumentCodecs.document.Validate
                                "$"
                                (mutate "context" altered wire))
                            "Cross references must fail before typed interpretation")

            testCase
                "authority receipt retains supplied outcome facts in its core Screen"
                (fun () ->
                    let _, _, actor = authorityReview ()

                    let receipt =
                        { ResponseExamples.authorityReceipt with
                            Subject =
                                ReceiptSubject.Authority
                                    {
                                        TargetActor = AuthorityTargetDisclosure.Disclosed actor
                                        Act = AuthorityKind.Disable
                                        Effects =
                                            [
                                                {
                                                    Act = AuthorityKind.Disable
                                                    Position = ResponseExamples.receipt.Position
                                                }
                                            ]
                                    }
                        }

                    let document = HumanReceiptDocument.ofCore receipt

                    let rendered =
                        ViewText.renderSelected true "en" document.Screen |> String.concat "\n"

                    Expect.stringContains
                        rendered
                        "Reviewed actor🪙"
                        "Authority facts cannot collapse to generic acceptance and UUID"

                    Expect.stringContains
                        rendered
                        (ActorId.format actor.ActorId)
                        "Technical target remains available in DETAILS"

                    let wire = HumanDocumentCodecs.document.Write document
                    Expect.isFalse (containsMember "snapshot" wire) "No raw receipt snapshot"
                    Expect.isFalse (containsMember "data" wire) "No raw DATA sibling"
                    HumanDocumentCodecs.document.Validate "$" wire |> accepted |> ignore)

            testCase
                "compound receipt preserves both handles and distinguishes partial disclosure"
                (fun () ->
                    let parent = ResponseExamples.snapshot.CaseId

                    let first =
                        (ReminderId.create >> Option.get) (
                            Guid.Parse "c999fa84-cd4e-4cfe-b8bb-87929a18ff10"
                        )

                    let second =
                        (ReminderId.create >> Option.get) (
                            Guid.Parse "519e39cd-070b-4ce5-b1e0-a2f391fd54a1"
                        )

                    let next =
                        Reminder.ofAccepted
                            second
                            parent
                            Revision.first
                            (ReminderAction.parse "Permitted next reminder" |> accepted)
                            (Inputs.date "2026-11-10")
                            ReminderState.Pending
                        |> accepted

                    let version id snapshot =
                        {
                            ReminderVersion.ReminderId = id
                            Revision = Revision.first
                            Snapshot = snapshot
                        }

                    let receipt =
                        { ResponseExamples.receipt with
                            Subject =
                                ReceiptSubject.Reminder
                                    {
                                        CaseId = parent
                                        Reference = ResponseExamples.snapshot.Reference
                                        Act = ReminderActKind.CompleteAndPlanNext
                                        Effects =
                                            ReminderReceiptEffects.CompletedAndPlanned(
                                                version first ReminderDisclosure.Erased,
                                                version second (ReminderDisclosure.Disclosed next)
                                            )
                                    }
                        }

                    let document = HumanReceiptDocument.ofCore receipt

                    Expect.equal
                        document.Disclosure
                        HumanDisclosure.Partial
                        "Mixed compartments are neither wholly disclosed nor wholly erased"

                    match document.Context with
                    | InteractionContext.Receipt {
                                                     Target = ReceiptTarget.Reminder(id, versions)
                                                 } ->
                        Expect.equal id parent "Parent handle"

                        Expect.equal
                            (versions |> List.map _.ReminderId)
                            [ first; second ]
                            "Both compound identities in effect order"
                    | _ -> failtest "Compound reminder handles required"

                    let wire = HumanDocumentCodecs.document.Write document
                    HumanDocumentCodecs.document.Validate "$" wire |> accepted |> ignore
                    let context = JsonValue.tryMember "context" wire |> Option.get
                    let target = JsonValue.tryMember "target" context |> Option.get

                    let duplicate =
                        JsonValue.Object
                            [
                                "reminderId", JsonValue.String(ReminderId.format first)
                                "revision", JsonValue.Integer 1L
                            ]

                    let altered =
                        mutate
                            "target"
                            (mutate "versions" (JsonValue.Array [ duplicate; duplicate ]) target)
                            context

                    Expect.isError
                        (HumanDocumentCodecs.document.Validate "$" (mutate "context" altered wire))
                        "Repeated effect IDs cannot masquerade as two handles")
        ]
