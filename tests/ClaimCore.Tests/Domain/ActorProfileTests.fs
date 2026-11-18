module ClaimCore.Tests.ActorProfileTests

open Expecto
open ClaimCore.Domain

let private accepted result =
    result
    |> Result.defaultWith (fun issue -> failtestf "Unexpected issue: %A" issue)

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
                            DigestEmail = None
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
                        DigestEmail = None
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
                            DigestEmail = None
                            Contact = Some " retained contact "
                        }

                    Expect.isError
                        (ActorProfile.parse retained)
                        "Current authoring does not accept surrounding spaces"

                    Expect.equal
                        (ActorProfile.restore retained |> accepted |> ActorProfile.authored)
                        retained
                        "Permanent meaning is preserved")
        ]
