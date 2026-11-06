module ClaimCore.Tests.ActorProfileTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
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
        "actor profiles and references"
        [
            testCase "independent profile issues accumulate in field order" (fun () ->
                let result =
                    ActorProfile.parse
                        {
                            DisplayName = " "
                            Contact = Some "\n"
                        }

                match result with
                | Error issues ->
                    Expect.equal
                        (List.map fst issues)
                        [ ActorProfileField.DisplayName; ActorProfileField.Contact ]
                        "Both invalid authored fields"
                | Ok _ -> failtest "Invalid profile admitted")
            testCase "contact admission counts Unicode scalars and rejects line breaks" (fun () ->
                let input (contact: string) : ActorProfileInput =
                    {
                        DisplayName = "Anna Ozola"
                        Contact = Some contact
                    }

                Expect.isOk (ActorProfile.parse (input (String.replicate 200 "😀"))) "200 scalars"

                Expect.isError
                    (ActorProfile.parse (input (String.replicate 201 "😀")))
                    "201 scalars"

                Expect.isError (ActorProfile.parse (input "one\ntwo")) "Single line"
                Expect.isError (ActorProfile.parse (input "")) "Omit absent contact explicitly")
            testCase
                "retained restoration preserves authored text without readmitting current policy"
                (fun () ->
                    let retained: ActorProfileInput =
                        {
                            DisplayName = " retained name "
                            Contact = Some " retained contact "
                        }

                    Expect.isError
                        (ActorProfile.parse retained)
                        "Current authoring does not accept surrounding spaces"

                    Expect.equal
                        (ActorProfile.restore retained |> accepted |> ActorProfile.authored)
                        retained
                        "Permanent meaning is preserved")
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
                        Basis = Inputs.workBasis
                        Items = [ reference ]
                        NextCursor = None
                    }

                let bytes = ResponseCodecs.assigneePage.Write page |> WireWriter.bytes
                let text = Encoding.UTF8.GetString bytes

                Expect.equal
                    text
                    "{\"basis\":{\"observedAt\":\"2026-08-31T12:00:00.000000Z\",\"businessDate\":\"2026-08-31\",\"timeZone\":\"UTC\"},\"items\":[{\"actorId\":\"12345678-1234-4234-8234-123456789abc\",\"displayName\":{\"kind\":\"NAMED\",\"value\":\"Anna Ozola\"},\"principalKind\":\"HUMAN\",\"availability\":\"AVAILABLE\"}],\"nextCursor\":null}"
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
