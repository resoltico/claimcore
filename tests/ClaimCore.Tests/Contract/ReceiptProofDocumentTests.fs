module ClaimCore.Tests.ReceiptProofDocumentTests

open System
open Expecto
open ClaimCore.Domain
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
                "compound authority receipt keeps stable unique provenance and hash in details"
                (fun () ->
                    let template = ResponseExamples.authorityReceipt

                    let actor =
                        Inputs.namedActorReference
                            (CaseFixtures.actorId (
                                Guid.Parse "10000000-0000-4000-8000-000000000009"
                            ))
                            "private-target-caption"

                    let effects =
                        [ AuthorityKind.Register; AuthorityKind.SetProfile; AuthorityKind.Grant ]
                        |> List.mapi (fun index act ->
                            {
                                AuthorityReceiptEffect.Act = act
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
                                ReceiptSubject.Authority
                                    {
                                        TargetActor = AuthorityTargetDisclosure.Disclosed actor
                                        Act = AuthorityKind.Admit
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
                                ReceiptSubject.Authority
                                    {
                                        TargetActor =
                                            AuthorityTargetDisclosure.Withheld actor.ActorId
                                        Act = AuthorityKind.Admit
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
                "timeline and authority history retain root proofs under disclosure masking"
                (fun () ->
                    let receipt = ResponseExamples.receipt
                    let position = EffectPosition.create receipt.Position.Sequence 2 |> Option.get

                    let timeline: CaseTimelineEntry =
                        {
                            OperationId = receipt.OperationId
                            Position = position
                            OperationHash = hash
                            Subject = TimelineSubject.CaseFact(Revision.first, CaseActKind.Close)
                            RecordedAt = receipt.RecordedAt
                            RecordedBy = receipt.RecordedBy
                            Changes = TimelineChanges.Withheld
                        }

                    let item = TimelineScreen.item timeline

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

                    let history: AuthorityHistoryEntry =
                        {
                            OperationId = receipt.OperationId
                            Position = position
                            OperationHash = hash
                            RecordedAt = receipt.RecordedAt
                            RecordedBy = receipt.RecordedBy
                            Target = receipt.RecordedBy
                            Facts = AuthorityHistoryFacts.Enabled
                        }

                    let screen = AccessHistoryScreen.rows [ history ]

                    let details =
                        entries screen
                        |> List.filter (fst >> (=) RegionRole.Details)
                        |> List.choose (snd >> _.Value >> identifier)

                    Expect.contains
                        details
                        (String.replicate 32 "ab")
                        "Authority proof is available in technical details"

                    Expect.contains
                        details
                        (OperationId.format receipt.OperationId)
                        "Authority operation identity is retained")
        ]
