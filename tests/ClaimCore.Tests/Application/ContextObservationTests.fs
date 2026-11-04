module ClaimCore.Tests.ContextObservationTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport

let private id number =
    Guid.Parse(sprintf "e927ae30-f9cd-49a4-8117-%012d" number)

let private get (task: System.Threading.Tasks.Task<'value>) = task.GetAwaiter().GetResult()

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

[<Tests>]
let tests =
    testCase
        "active context exposes actual snapshot basis while limited requester states stay limited"
        (fun () ->
            let first = DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
            use store = new MemoryStore.Store(id 1, first)

            let calendar =
                { new IBusinessCalendar with
                    member _.TimeZoneId = "Etc/UTC"

                    member _.DateAt instant =
                        DateOnly.FromDateTime instant.UtcDateTime
                }

            let principal subject =
                PrincipalKey.human "https://observation.example/" subject |> accepted

            let actor number subject enabled =
                {
                    Actor.ActorId = CaseFixtures.actorId (id number)
                    Principal = principal subject
                    Enabled = enabled
                    Profile =
                        ActorProfile.parse
                            {
                                DisplayName = subject
                                Contact = None
                            }
                        |> accepted
                        |> Some
                }

            let active = actor 2 "active" true
            let noRoles = actor 3 "no-roles" true
            let disabled = actor 4 "disabled" false
            store.SeedActor active [ Role.CaseReader, None ]
            store.SeedActor noRoles []
            store.SeedActor disabled [ Role.CaseReader, None ]

            let read principal =
                Service.context
                    (Service.create "fixture-version" (id 1) (store :> IStore) calendar principal)
                    ()
                    CancellationToken.None
                |> get
                |> function
                    | Outcome.Succeeded reply -> reply
                    | other -> failtestf "%A" other

            let initial = read active.Principal

            let current =
                match initial.Requester with
                | RequesterState.Active context -> context
                | _ -> failtest "Active context required"

            Expect.equal
                current.Roles
                [
                    {
                        Role = Role.CaseReader
                        Installation = true
                        CaseCount = 0L
                    }
                ]
                "Own summaries expose actual raw assignment labels without deriving roles from capabilities"

            Expect.equal
                current.Runtime.Basis
                {
                    ObservedAt = first
                    BusinessDate = DateOnly(2026, 11, 4)
                    TimeZoneId = "Etc/UTC"
                }
                "The context is derived from the actual store snapshot and installation calendar"

            let requester =
                ResponseCodecs.contextReply.Write initial
                |> JsonValue.tryMember "requester"
                |> Option.get

            Expect.equal
                (JsonValue.tryMember "observedAt" requester)
                (Some(JsonValue.String "2026-11-04T12:00:00.000000Z"))
                "The observation is explicit in the existing flat active wire shape"

            Expect.equal
                (JsonValue.tryMember "businessDate" requester)
                (Some(JsonValue.String "2026-11-04"))
                "Business date remains flat"

            Expect.equal
                (JsonValue.tryMember "basis" requester)
                None
                "Internal structure does not invent an additional active-context wire wrapper"

            store.ObservedAt <- first.AddDays 1.

            match (read active.Principal).Requester with
            | RequesterState.Active context ->
                Expect.equal
                    context.Runtime.Basis.ObservedAt
                    (first.AddDays 1.)
                    "A later read uses its own snapshot"

                Expect.equal
                    context.Runtime.Basis.BusinessDate
                    (DateOnly(2026, 11, 5))
                    "No cached business date survives"
            | _ -> failtest "Active context required"

            for principal in [ noRoles.Principal; disabled.Principal; principal "not-registered" ] do
                let reply = read principal

                let wire =
                    ResponseCodecs.contextReply.Write reply
                    |> JsonValue.tryMember "requester"
                    |> Option.get

                Expect.equal
                    (JsonValue.tryMember "observedAt" wire)
                    None
                    "C2's limited states do not gain unsupported observation metadata"

                Expect.equal
                    (JsonValue.tryMember "productVersion" wire)
                    None
                    "Active runtime details stay within the active state")


[<Tests>]
let authorityPageBasis =
    testCase
        "authority pages carry the authorized transaction basis without a client clock"
        (fun () ->
            let first = DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
            use store = new MemoryStore.Store(id 10, first)

            let calendar =
                { new IBusinessCalendar with
                    member _.TimeZoneId = "Etc/UTC"

                    member _.DateAt instant =
                        DateOnly.FromDateTime instant.UtcDateTime
                }

            let principal =
                PrincipalKey.human "https://observation.example/" "owner" |> accepted

            let owner =
                {
                    Actor.ActorId = CaseFixtures.actorId (id 11)
                    Principal = principal
                    Enabled = true
                    Profile = None
                }

            store.SeedActor owner [ Role.Owner, None ]
            let services = Service.create "fixture" (id 10) (store :> IStore) calendar principal

            let succeeded =
                function
                | Outcome.Succeeded value -> value
                | other -> failtestf "%A" other

            for observed in [ first; first.AddDays 1. ] do
                store.ObservedAt <- observed

                let expected =
                    {
                        WorkDateBasis.ObservedAt = observed
                        BusinessDate = DateOnly.FromDateTime observed.UtcDateTime
                        TimeZoneId = "Etc/UTC"
                    }

                let actors =
                    Service.actors
                        services
                        {
                            Role = None
                            Text = None
                            AfterCursor = None
                            Limit = 50
                        }
                        CancellationToken.None
                    |> get
                    |> succeeded

                let grants =
                    Service.grants
                        services
                        {
                            ActorId = id 11
                            AfterCursor = None
                            Limit = 50
                        }
                        CancellationToken.None
                    |> get
                    |> succeeded

                let history =
                    Service.authorityHistory
                        services
                        {
                            TargetActorId = None
                            AfterCursor = None
                            Limit = 50
                        }
                        CancellationToken.None
                    |> get
                    |> succeeded

                let requests =
                    Service.accessRequests
                        services
                        { AfterCursor = None; Limit = 50 }
                        CancellationToken.None
                    |> get
                    |> succeeded

                for actual in [ actors.Basis; grants.Basis; history.Basis; requests.Basis ] do
                    Expect.equal
                        actual
                        expected
                        "Every reply basis comes from its own read snapshot"

                let preview =
                    Service.preview
                        services
                        (OperationInput.Authority
                            {
                                OperationId = id 12
                                Target =
                                    {
                                        Kind = PrincipalKind.Human
                                        Issuer = "https://observation.example/"
                                        Subject = "owner"
                                    }
                                Act =
                                    AuthorityAct.SetProfile
                                        {
                                            DisplayName = "Basis profile"
                                            Contact = None
                                        }
                            })
                        CancellationToken.None
                    |> get

                match preview.Outcome with
                | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Authority review)) ->
                    Expect.equal
                        review.Basis
                        expected
                        "Authority preview carries the same authorized read snapshot"
                | other -> failtestf "Expected an authority preview: %A" other

                let wire =
                    ResponseCodecs.actorsPage.Write actors
                    |> JsonValue.tryMember "basis"
                    |> Option.get

                Expect.equal
                    (JsonValue.tryMember "observedAt" wire)
                    (Some(JsonValue.String(observed.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"))))
                    "The supplied instant survives encoding"

                Expect.equal
                    (JsonValue.tryMember "timeZone" wire)
                    (Some(JsonValue.String "Etc/UTC"))
                    "Wire basis includes installation zone")
