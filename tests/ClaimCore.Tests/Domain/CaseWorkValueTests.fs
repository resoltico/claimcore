module ClaimCore.Tests.CaseWorkValueTests

open System
open Expecto
open ClaimCore.Domain

let private required =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

let private date value = CalendarDate.parse value |> required

let private id number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)

let private today = DateOnly(2026, 11, 1)

[<Tests>]
let tests =
    testList
        "work values and access"
        [
            testCase
                "work summary parsing and application preserve independent optional values"
                (fun () ->
                    let input: CaseWorkInput =
                        {
                            Responsible = WorkValue.Keep
                            Summary = WorkValue.Set "Current\nhandling context"
                        }

                    let update = CaseWorkInput.parse input |> required
                    let changed = CaseWork.apply update CaseWork.empty
                    Expect.equal changed.Responsible None "Summary does not assign"

                    Expect.equal
                        (changed.Summary |> Option.map HandlingSummary.format)
                        (Some "Current\nhandling context")
                        "Paragraph summary"

                    let clear: CaseWorkInput =
                        {
                            Responsible = WorkValue.Keep
                            Summary = WorkValue.Clear
                        }

                    Expect.equal
                        (CaseWork.apply (CaseWorkInput.parse clear |> required) changed)
                        CaseWork.empty
                        "Explicit clear"

                    Expect.isError
                        (CaseWorkInput.parse
                            {
                                Responsible = WorkValue.Keep
                                Summary = WorkValue.Keep
                            })
                        "No empty work input"

                    Expect.equal
                        (CaseDue.at
                            today
                            Lifecycle.Active
                            (Some CaseStatus.Closed)
                            (Some(date "2026-10-01")))
                        CaseDue.Inactive
                        "Inactive parent classification")
            testCase "human capability does not invalidate service CASE_HANDLER" (fun () ->
                let principal = PrincipalKey.service "https://issuer.example/" "worker" |> required

                let view =
                    { AccessView.empty with
                        Actor =
                            Some
                                {
                                    ActorId = ActorId.create (id 10) |> Option.get
                                    Principal = principal
                                    Enabled = true
                                    Profile = None
                                }
                        Installation = [ Role.CaseHandler ]
                    }

                Expect.isTrue (Access.validActor principal view) "Role remains valid for services"

                for capability in
                    [
                        Capability.ReadCase
                        Capability.Register
                        Capability.Decide
                        Capability.Close
                    ] do
                    Expect.isTrue
                        (Access.can principal view capability Scope.Installation)
                        "Existing service right retained"

                Expect.isFalse
                    (Access.can principal view Capability.ManageCaseWork Scope.Installation)
                    "No human-only capability")


        ]
