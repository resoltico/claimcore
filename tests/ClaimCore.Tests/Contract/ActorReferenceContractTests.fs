module ClaimCore.Tests.ActorReferenceContractTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport

let private accepted result =
    result
    |> Result.defaultWith (fun issue -> failtestf "Unexpected issue: %A" issue)

let private actorId =
    CaseFixtures.actorId (Guid.Parse "12345678-1234-4234-8234-123456789abc")

[<Tests>]
let tests =
    testList
        "actor reference transport and presentation"
        [
            testCase "reference codec excludes profile contact and principal identity" (fun () ->
                let profile =
                    ActorProfile.parse
                        {
                            DisplayName = "Anna Ozola"
                            Contact = Some "private-contact@example.test"
                        }
                    |> accepted

                let reference =
                    ActorRef.create
                        actorId
                        PrincipalKind.Human
                        ActorAvailability.Available
                        (Some profile)

                let page: ClaimCore.Application.Contracts.AssigneePage =
                    {
                        CaseId =
                            CaseFixtures.caseId (
                                System.Guid.Parse "20000000-0000-4000-8000-000000000001"
                            )
                        WorkRevision = Revision.zero
                        Basis = DomainInputs.workBasis
                        Items = [ reference ]
                        NextCursor = None
                    }

                let bytes = ResponseCodecs.assigneePage.Write page |> WireWriter.bytes
                let text = Encoding.UTF8.GetString bytes

                Expect.equal
                    text
                    "{\"caseId\":\"20000000-0000-4000-8000-000000000001\",\"workRevision\":0,\"basis\":{\"observedAt\":\"2026-08-31T12:00:00.000000Z\",\"businessDate\":\"2026-08-31\",\"timeZone\":\"UTC\"},\"items\":[{\"actorId\":\"12345678-1234-4234-8234-123456789abc\",\"displayName\":{\"kind\":\"NAMED\",\"value\":\"Anna Ozola\"},\"principalKind\":\"HUMAN\",\"availability\":\"AVAILABLE\"}],\"nextCursor\":null}"
                    "Authored independent wire shape"

                Expect.equal
                    (Codec.decode 4096 ResponseCodecs.assigneePage bytes)
                    (Ok page)
                    "Typed roundtrip"

                Expect.isFalse (text.Contains("private-contact")) "Contact never enters references")
            testCase
                "erased actor naming is core-owned and uses only the short retained actor ID"
                (fun () ->
                    let reference =
                        ActorRef.create actorId PrincipalKind.Human ActorAvailability.Disabled None

                    let render key arguments =
                        Expect.equal key "label.formerAccount" "Core message"

                        Expect.equal
                            arguments
                            (Map.ofList [ "id", "12345678" ])
                            "No profile or principal data"

                        "Former account 12345678"

                    Expect.equal
                        (ActorPresentation.displayName render reference)
                        "Former account 12345678"
                        "Explicit erased state")
        ]
