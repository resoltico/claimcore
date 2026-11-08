module ClaimCore.Tests.OwnerInputTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Database
open ClaimCore.Postgres
open ClaimCore.Presentation

let private flags =
    [
        "initialize"
        "Etc/UTC"
        "--owner-issuer"
        "https://issuer.example/"
        "--owner-subject"
        "human-owner"
        "--owner-name"
        "Authored owner"
    ]

let private issue args =
    match Inputs.command args with
    | Error value -> value.Issue
    | Ok _ -> failtest "Expected invalid initialization arguments"

[<Tests>]
let tests =
    testList
        "initial owner flags"
        [
            testCase
                "initialization accepts a human profile and allowed installation roles"
                (fun () ->
                    match
                        Inputs.command (
                            flags
                            @ [
                                "--owner-contact"
                                "owner@example.test"
                                "--owner-role"
                                "CASE_HANDLER"
                                "--owner-role"
                                "PAYMENT_RECORDER"
                            ]
                        )
                    with
                    | Ok(Initialize(zone, owner)) ->
                        Expect.equal zone "Etc/UTC" "Explicit business zone"

                        Expect.equal
                            owner.Principal
                            (PrincipalKey.human "https://issuer.example/" "human-owner"
                             |> Result.defaultWith (string >> invalidOp))
                            "Human identity"

                        Expect.equal
                            owner.Profile
                            {
                                DisplayName = "Authored owner"
                                Contact = Some "owner@example.test"
                            }
                            "Authored profile"

                        Expect.equal
                            owner.Roles
                            [ Role.CaseHandler; Role.PaymentRecorder ]
                            "Only additional roles are authored"
                    | other -> failtestf "Expected initialization: %A" other)
            testCase
                "owner flags are required without an operation identity or private principal file"
                (fun () ->
                    Expect.equal
                        (issue [ "initialize"; "Etc/UTC" ])
                        (UsageError.MissingOption "--owner-issuer")
                        "Missing owner identity"

                    Expect.equal
                        (issue (flags @ [ "--operation-id"; Guid.NewGuid().ToString("D") ]))
                        (UsageError.UnknownOption("--operation-id", None))
                        "Identity is minted inside initialization"

                    Expect.isTrue
                        (match Inputs.command [ "provision-owner" ] with
                         | Error _ -> true
                         | _ -> false)
                        "Retired command refuses")
            testCase "initial roles reject unsupported and duplicate grants" (fun () ->
                for role in [ "OWNER"; "CASE_READER"; "CASE_REGISTRAR"; "case-handler" ] do
                    Expect.isTrue
                        (match issue (flags @ [ "--owner-role"; role ]) with
                         | UsageError.InvalidChoice _ -> true
                         | _ -> false)
                        "Only the declared bootstrap role subset is allowed"

                Expect.isTrue
                    (match
                        issue (
                            flags
                            @ [ "--owner-role"; "CASE_HANDLER"; "--owner-role"; "CASE_HANDLER" ]
                        )
                     with
                     | UsageError.InvalidChoice _ -> true
                     | _ -> false)
                    "Duplicate role is not silently discarded")
            testCase "profile validation identifies the actual owner flag" (fun () ->
                let invalidName =
                    flags
                    |> List.map (fun value -> if value = "Authored owner" then " " else value)

                Expect.equal
                    (issue invalidName)
                    (UsageError.InvalidValue("--owner-name", " "))
                    "Name admission is shared with actor profiles"

                Expect.equal
                    (issue (flags @ [ "--owner-contact"; "\n" ]))
                    (UsageError.InvalidValue("--owner-contact", "\n"))
                    "Contact admission is shared"

                Expect.equal
                    (issue (flags @ [ "--owner-name"; "Second" ]))
                    (UsageError.DuplicateOption "--owner-name")
                    "A scalar flag has one value")
            testCase
                "maintenance is idempotent on/off syntax and retired generation flags refuse"
                (fun () ->
                    Expect.equal
                        (Inputs.command [ "maintenance"; "on" ])
                        (Ok(Maintenance ClaimCore.Application.Contracts.MaintenanceChange.On))
                        "On"

                    Expect.equal
                        (Inputs.command [ "maintenance"; "off" ])
                        (Ok(Maintenance ClaimCore.Application.Contracts.MaintenanceChange.Off))
                        "Off"

                    Expect.isTrue
                        (match Inputs.command [ "maintenance"; "resume"; "--generation"; "1" ] with
                         | Error _ -> true
                         | _ -> false)
                        "No generation contract remains")
        ]
