module ClaimCore.Tests.ReceiptObservationTests

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
    Guid.Parse(sprintf "e996c112-2ef5-478a-870a-%012d" number)

let private get (task: System.Threading.Tasks.Task<'value>) = task.GetAwaiter().GetResult()

let private accepted =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

[<Tests>]
let tests =
    testCase "receipt observations advance independently of the accepted event time" (fun () ->
        let first = DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
        use store = new MemoryStore.Store(id 1, first)
        let principal = PrincipalKey.human "https://receipt.example/" "author" |> accepted

        let actor =
            {
                Actor.ActorId = CaseFixtures.actorId (id 2)
                Principal = principal
                Enabled = true
                Profile =
                    ActorProfile.parse
                        {
                            DisplayName = "Receipt author"
                            Contact = None
                        }
                    |> accepted
                    |> Some
            }

        store.SeedActor actor [ Role.CaseHandler, None ]

        let calendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = "Etc/UTC"

                member _.DateAt instant =
                    DateOnly.FromDateTime instant.UtcDateTime
            }

        let core =
            Service.create "fixture-version" (id 1) (store :> IStore) calendar principal

        let input =
            Inputs.caseOperation
                {
                    OperationId = id 3
                    CaseReference = "OBS-RECEIPT"
                    ExpectedRevision = 0L
                    Act = CaseAct.Open Inputs.registration
                }

        let initial =
            (Service.execute core input CancellationToken.None |> get).Outcome
            |> function
                | ExecuteResult.Accepted receipt -> receipt
                | other -> failtestf "%A" other

        Expect.equal initial.RecordedAt first "The event keeps its accepting transaction time"

        Expect.equal
            initial.Basis.ObservedAt
            first
            "The initial receipt uses its actual accepting snapshot"

        store.ObservedAt <- first.AddDays 1.

        let replay =
            (Service.execute core input CancellationToken.None |> get).Outcome
            |> function
                | ExecuteResult.AlreadyAccepted receipt -> receipt
                | other -> failtestf "%A" other

        let lookup: OperationLookup = { OperationId = id 3 }

        let observe =
            (Service.observe core lookup CancellationToken.None |> get).Outcome
            |> function
                | Outcome.Succeeded receipt -> receipt
                | other -> failtestf "%A" other

        let status =
            (Service.status core lookup CancellationToken.None |> get).Outcome
            |> function
                | Outcome.Succeeded(OperationStatus.Accepted receipt) -> receipt
                | other -> failtestf "%A" other

        let resolved =
            (Service.resolve core lookup CancellationToken.None |> get).Outcome
            |> function
                | Outcome.Succeeded(Resolved.Accepted receipt) -> receipt
                | other -> failtestf "%A" other

        for receipt in [ replay; observe; status; resolved ] do
            Expect.equal
                receipt.RecordedAt
                first
                "Reading or replaying never rewrites the historical event time"

            Expect.equal
                receipt.Basis
                {
                    ObservedAt = first.AddDays 1.
                    BusinessDate = DateOnly(2026, 11, 5)
                    TimeZoneId = "Etc/UTC"
                }
                "Each authorized path uses its actual current snapshot and calendar"

            let wire = ReceiptCodecs.receipt.Write receipt

            Expect.equal
                (JsonValue.tryMember "basis" wire
                 |> Option.bind (JsonValue.tryMember "observedAt"))
                (Some(JsonValue.String "2026-11-05T12:00:00.000000Z"))
                "The observation is an explicit typed receipt member"

            Expect.equal
                (JsonValue.tryMember "recordedAt" wire)
                (Some(JsonValue.String "2026-11-04T12:00:00.000000Z"))
                "Canonical event time remains separately visible")
