module ClaimCore.Tests.OperationalCommandTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private principal kind =
    PrincipalKey.ofKind kind "https://issuer.example/" "shared-subject"
    |> Result.toOption
    |> Option.get

let private requestPage key =
    HumanReadDocuments.requests
        {
            Basis = DomainInputs.workBasis
            NextCursor = None
            Items =
                [
                    {
                        Principal = key
                        FirstSeen = DomainInputs.workBasis.ObservedAt
                        LastSeen = DomainInputs.workBasis.ObservedAt
                        Claims = RequesterClaims.empty
                    }
                ]
        }

let private rawRequests key =
    RepresentationReply.Human
        {
            HumanReadReply.Outcome = HumanReadOutcome.Succeeded(requestPage key)
        }
    |> Codec.encode Api.peopleRequests.Output
    |> Encoding.UTF8.GetString

let private readRequests (text: string) =
    let raw =
        StrictJson.read Limits.MaximumResponseBytes (Encoding.UTF8.GetBytes text)
        |> Result.toOption
        |> Option.get

    (Api.forActor (ActorEndpoint.Query QueryTaskId.PeopleRequests)).ReadOutput raw

[<Tests>]
let tests =
    testList
        "typed operational command controls"
        [
            testCase
                "dismissal survives additional commands and round-trips exact principal kind"
                (fun () ->
                    for kind in [ PrincipalKind.Human; PrincipalKind.Service ] do
                        let key = principal kind

                        Expect.isOk
                            (readRequests (rawRequests key))
                            "Valid principal and complete task/control association"

                        let controls = requestPage key |> fun doc -> Screen.affordances doc.Screen

                        let dismissal =
                            controls
                            |> List.find (fun action ->
                                action.Task = TaskId.ofLiteral "people.requests.dismiss")

                        Expect.equal
                            dismissal.Control
                            (ActionControl.Command(CommandRequest.DismissAccessRequest key))
                            "Other commands cannot retarget dismissal"

                        Expect.isEmpty
                            dismissal.Seeds
                            "Commands do not reconstruct required principals from seeds")
            testCase
                "malformed dismissal principals return response admission errors without throwing"
                (fun () ->
                    let valid = rawRequests (principal PrincipalKind.Human)

                    for issuer in
                        [ ""; "https://user@issuer.example/"; "https://issuer.example/?query" ] do
                        Expect.isError
                            (readRequests (valid.Replace("https://issuer.example/", issuer)))
                            "Malformed principal is a fallible boundary error"

                    Expect.isError
                        (readRequests (valid.Replace("shared-subject", "")))
                        "Missing subject refuses")
            testCase
                "task and command association is checked structurally before serving"
                (fun () ->
                    let valid = rawRequests (principal PrincipalKind.Human)

                    let mismatch =
                        valid.Replace(
                            "\"task\":\"people.requests.dismiss\"",
                            "\"task\":\"session.terminateOwn\""
                        )

                    Expect.isError
                        (readRequests mismatch)
                        "Own-session task cannot carry dismissal principal binding"

                    let raw =
                        StrictJson.read
                            Limits.MaximumResponseBytes
                            (Encoding.UTF8.GetBytes mismatch)
                        |> Result.toOption
                        |> Option.get

                    Expect.isError
                        (Api.peopleRequests.Output.Validate "$" raw)
                        "Native structural validation rejects the association, independently of constructors")
            testCase "command declaration and endpoint sets are exact and nonledger" (fun () ->
                Expect.equal
                    (EndpointDeclarations.commands |> List.map _.Id |> Set.ofList)
                    (CommandTaskId.all |> List.map CommandTaskId.key |> Set.ofList)
                    "Every closed command has one declaration"

                for id in CommandTaskId.all do
                    let route = Api.forActor (ActorEndpoint.Command id)

                    Expect.equal
                        (Api.effect route)
                        EndpointEffect.OperationalWrite
                        "No command enters ledger replay"

                    Expect.isFalse
                        (Api.requiresInstallation route)
                        "Session/dismissal commands have no mandatory ledger installation binding")
            testCase
                "session command argv routes through declared grammar without an operation identity"
                (fun () ->
                    let own = [ "session"; "terminate-own"; "--dry-run" ]

                    match CliInvocations.admit own with
                    | Ok(CliInvocation.Verb words) ->
                        Expect.equal words own "Multiword task shares executable session family"
                    | _ -> failtest "Session command must be a declared human verb"

                    match CliInvocations.admit [ "session"; "terminate-own"; "--help" ] with
                    | Ok(CliInvocation.CommandHelp(words, _, false)) ->
                        Expect.equal
                            words
                            [ "session"; "terminate-own" ]
                            "Declared task help wins over builtin session help"
                    | _ -> failtest "Declared session task help required"

                    let prepared =
                        Grammar.prepare
                            (fun () ->
                                failtest "Operational command must not allocate a ledger ID")
                            own
                        |> Result.toOption
                        |> Option.get

                    Expect.equal
                        (Api.id (Frames.endpoint prepared.Invocation))
                        "session.terminateOwn"
                        "Own termination endpoint"

                    Expect.isNone
                        (Frames.operationId prepared.Invocation)
                        "No fake operation identity"

                    let person =
                        Grammar.prepare
                            (fun () ->
                                failtest "Operational command must not allocate a ledger ID")
                            [
                                "people"
                                "terminate-sessions"
                                "10000000-0000-4000-8000-000000000001"
                                "--dry-run"
                            ]
                        |> Result.toOption
                        |> Option.get

                    Expect.equal
                        (Frames.value Api.terminatePersonSessions person.Invocation
                         |> Option.map _.ActorId)
                        (Some(Guid.Parse "10000000-0000-4000-8000-000000000001"))
                        "Exact selected person")
            testCase "operational unknown has neither a count nor an operation proof" (fun () ->
                let raw =
                    SessionTerminationCodecs.data.Write SessionTerminationResult.OutcomeUnknown

                Expect.equal
                    raw
                    (JsonValue.Object
                        [
                            "outcome",
                            JsonValue.Object [ "kind", JsonValue.String "OUTCOME_UNKNOWN" ]
                        ])
                    "Explicit nonledger uncertainty"

                let document = HumanReadDocuments.terminationUnknown ()
                let text = ViewText.renderDocument false "en" document |> String.concat "\n"

                Expect.stringContains
                    text
                    "newer sessions"
                    "Repeating is not falsely described as exact idempotent recovery")
        ]
