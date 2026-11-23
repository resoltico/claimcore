module ClaimCore.Tests.ReceiptProofDocumentTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.TestSupport

let private hash = EntryHash.ofBytes (Array.create 32 0xabuy) |> Option.get

let private entries (screen: Screen) =
    screen.Regions
    |> List.collect (fun region ->
        match region.Content with
        | RegionContent.Entries values -> values |> List.map (fun value -> region.Role, value)
        | _ -> [])

let private identifier value =
    match value with
    | PresentedValue.Identifier value -> Some value
    | _ -> None

[<Tests>]
let tests =
    testList
        "human operation proofs"
        [
            testCase
                "compound people receipt keeps stable unique provenance and hash in details"
                (fun () ->
                    let template = ResponseExamples.peopleReceipt

                    let actor =
                        DomainInputs.namedActorReference
                            (CaseFixtures.actorId (
                                Guid.Parse "10000000-0000-4000-8000-000000000009"
                            ))
                            "private-target-caption"

                    let effects =
                        [
                            (TaskId.ofLiteral "people.register")
                            (TaskId.ofLiteral "people.setProfile")
                            (TaskId.ofLiteral "people.grant")
                        ]
                        |> List.mapi (fun index act ->
                            {
                                PeopleReceiptEffect.Act = act
                                Id = EffectId.create template.OperationId index |> Option.get
                                Position =
                                    EffectPosition.create template.Position.Sequence index
                                    |> Option.get
                            })

                    let receipt =
                        { template with
                            Position =
                                { template.Position with
                                    EntryHash = hash
                                }
                            Subject =
                                ReceiptSubject.People
                                    {
                                        TargetActor = PeopleTargetDisclosure.Disclosed actor
                                        Act = (TaskId.ofLiteral "people.admit")
                                        Effects = effects
                                    }
                        }

                    let document = HumanReceiptDocument.ofCore receipt

                    Expect.equal
                        (document.Screen.Regions |> List.map _.Role)
                        [
                            RegionRole.Notice
                            RegionRole.Identity
                            RegionRole.Facts
                            RegionRole.Facts
                            RegionRole.Facts
                            RegionRole.Facts
                            RegionRole.Details
                        ]
                        "Authored receipt anatomy and declared effects"

                    let all = entries document.Screen

                    Expect.isEmpty
                        (all
                         |> List.filter (fun (role, entry) ->
                             role <> RegionRole.Details && identifier entry.Value |> Option.isSome))
                        "Technical identities do not become saved primary content"

                    let details =
                        all |> List.filter (fst >> (=) RegionRole.Details) |> List.map snd

                    Expect.equal
                        (details |> List.map _.Key |> List.distinct |> List.length)
                        details.Length
                        "Flattened detail keys remain unambiguous across effects"

                    let identifiers = details |> List.choose (_.Value >> identifier)

                    for expected in
                        [
                            String.replicate 32 "ab"
                            "1/0"
                            "1/1"
                            "1/2"
                            OperationId.format receipt.OperationId + "/0"
                            OperationId.format receipt.OperationId + "/1"
                            OperationId.format receipt.OperationId + "/2"
                        ] do
                        Expect.contains
                            identifiers
                            expected
                            "Independent literal provenance is retained"

                    let withheld =
                        { receipt with
                            Subject =
                                ReceiptSubject.People
                                    {
                                        TargetActor = PeopleTargetDisclosure.Withheld actor.ActorId
                                        Act = (TaskId.ofLiteral "people.admit")
                                        Effects = effects
                                    }
                        }

                    let hidden = HumanReceiptDocument.ofCore withheld

                    Expect.equal
                        hidden.Disclosure
                        HumanDisclosure.Withheld
                        "Current target disclosure controls accepted document"

                    Expect.isFalse
                        (entries hidden.Screen
                         |> List.exists (fun (_, entry) ->
                             entry.Value = PresentedValue.Actor actor))
                        "Withheld target caption is absent"

                    Expect.contains
                        (entries hidden.Screen |> List.choose (snd >> _.Value >> identifier))
                        (String.replicate 32 "ab")
                        "Owner-visible acceptance proof survives target withholding")
            testCase
                "timeline and people history retain root proofs under disclosure masking"
                (fun () ->
                    let receipt = ResponseExamples.receipt
                    let position = EffectPosition.create receipt.Position.Sequence 2 |> Option.get

                    let timeline: CaseTimelineEntry =
                        {
                            OperationId = receipt.OperationId
                            Position = position
                            OperationHash = hash
                            Subject =
                                {
                                    PartKind = PartKind.Case
                                    PartId = CaseId.value ResponseExamples.snapshot.CaseId
                                    Revision = Revision.first
                                }
                            Task = TaskId.ofLiteral "case.close"
                            RecordedAt = receipt.RecordedAt
                            RecordedBy = receipt.RecordedBy |> AuthorityRef.actor |> Option.get
                            Changes = TimelineChanges.Withheld
                        }

                    let item = TimelineScreen.item timeline

                    Expect.isNone item.Target "A case-scoped timeline omits its own subject"
                    Expect.isNone item.Link "The case-scoped timeline does not link to itself"

                    let detailValues =
                        item.Details
                        |> List.collect _.Entries
                        |> List.choose (_.Value >> identifier)

                    Expect.contains
                        detailValues
                        (String.replicate 32 "ab")
                        "Actual timeline detail path includes operation hash"

                    Expect.contains
                        detailValues
                        "1/2"
                        "Actual timeline detail path includes effect index"

                    let history: PeopleHistoryEntry =
                        {
                            OperationId = receipt.OperationId
                            Position = position
                            OperationHash = hash
                            RecordedAt = receipt.RecordedAt
                            RecordedBy = receipt.RecordedBy
                            Target = receipt.RecordedBy |> AuthorityRef.actor |> Option.get
                            Task = TaskId.ofLiteral "people.enable"
                            Changes =
                                {
                                    PersonPartDescription.Profiles =
                                        { Before = None; After = None }
                                    Principal = None
                                    Enabled =
                                        Some
                                            {
                                                Before = Some false
                                                After = Some true
                                            }
                                    ProfilePresent = None
                                    DisplayName = None
                                    DigestEmail = None
                                    Contact = None
                                    Grants = []
                                }
                            PrivateAvailable = true
                            References = { Actors = []; Cases = [] }
                        }

                    let screen = AccessHistoryScreen.rows [ history ]

                    let details =
                        screen.Regions
                        |> List.collect (fun region ->
                            match region.Content with
                            | RegionContent.Timeline entries -> entries |> List.collect _.Details
                            | _ -> [])
                        |> List.collect _.Entries
                        |> List.choose (_.Value >> identifier)

                    Expect.contains
                        details
                        (String.replicate 32 "ab")
                        "People proof is available in technical details"

                    Expect.contains
                        details
                        (OperationId.format receipt.OperationId)
                        "People operation identity is retained")
        ]
