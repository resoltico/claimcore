module ClaimCore.WebTests.FacadeStub

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Domain

let operationId = Guid.Parse "40000000-0000-4000-8000-000000000001"

let position =
    {
        Sequence = 1L
        EntryHash = String.replicate 64 "a"
    }

let view reference =
    let request =
        {
            OperationId = operationId
            CaseReference = reference
            ExpectedVersion = 0L
            Command =
                Command.Open
                    {
                        IncidentDate = "2026-08-01"
                        IncidentNotificationDate = "2026-08-03"
                        IncidentCountry = "Latvia"
                        ClaimantName = "Synthetic claimant"
                        InsurerName = "Synthetic insurer"
                        ClaimedAmount = "1.00"
                        ClaimedCurrency = "EUR"
                    }
        }

    Claim.decide (DateOnly(2026, 10, 13)) request None
    |> Result.map Claim.view
    |> Result.defaultWith (fun _ -> invalidOp "Synthetic case failed admission.")

let receipt id reference =
    {
        OperationId = id
        Position = position
        CaseReference = reference
        Revision = 1L
        Act = AcceptedAct.Command CommandKind.Open
        Disposition = Some CaseDisposition.Active
        Snapshot = Some(view reference)
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = operationId
    }

let description =
    {
        Contract = SemanticContract.current
        SemanticFingerprint = SemanticContract.fingerprint SemanticContract.current
        Runtime =
            {
                ProductVersion = BuildIdentity.current.Version
                EffectiveBusinessDate = DateOnly(2026, 10, 13)
                TimeZoneId = "Etc/UTC"
            }
    }

type Facade() =
    let calls = ResizeArray<string>()
    member _.Calls = List.ofSeq calls
    member val Error: exn option = None with get, set

    member val Execute: CommandRequest * CancellationToken -> Task<ExecuteOutcome> =
        (fun (request, _) ->
            Task.FromResult(
                ExecuteOutcome.Accepted(receipt request.OperationId request.CaseReference)
            )) with get, set

    member val Principal: PrincipalKey option = None with get, set

    member this.Invoke name result =
        calls.Add name

        match this.Error with
        | Some error -> raise error
        | None -> Task.FromResult result

    member this.ActorCore = this :> IActorClaimsCore

    interface IActorClaimsCore with
        member this.Definition _ =
            this.Invoke "definition" (QueryOutcome.Succeeded description)

        member this.Preview(request, _) =
            this.Invoke
                "command.preview"
                (PreviewOutcome.AlreadyAccepted(receipt request.OperationId request.CaseReference))

        member this.Execute(request, token) =
            calls.Add "command.execute"

            match this.Error with
            | Some error -> raise error
            | None -> this.Execute(request, token)

        member this.Steward(request, _) =
            this.Invoke
                "case.steward"
                (ExecuteOutcome.Accepted(receipt request.OperationId request.CaseReference))

        member this.Get(reference, _) =
            this.Invoke
                "case.get"
                (QueryOutcome.Succeeded(
                    Lookup.Found(CaseRecord.Live(view reference, CaseDisposition.Active, []))
                ))

        member this.List(_, _) =
            this.Invoke
                "case.list"
                (QueryOutcome.Succeeded
                    {
                        Items = []
                        NextCursor = None
                        AvailableCommands = []
                    })

        member this.History(_, _) =
            this.Invoke
                "case.history"
                (QueryOutcome.Succeeded(Lookup.Found { Entries = []; NextCursor = None }))

        member this.ObserveOperation(id, _) =
            this.Invoke
                "operation.observe"
                (QueryOutcome.Succeeded(Lookup.Found(receipt id "WEB-001")))

        member this.Authority =
            { new IAuthorityManagement with
                member _.RegisterActor(id, _, _) =
                    this.Invoke
                        "authority.register"
                        (AuthorityOutcome.Applied(id, position, operationId))

                member _.SetGrant(id, _, _, _, _, _) =
                    this.Invoke
                        "authority.setGrant"
                        (AuthorityOutcome.Applied(id, position, operationId))

                member _.SetEnabled(id, _, _, _) =
                    this.Invoke
                        "authority.setEnabled"
                        (AuthorityOutcome.Applied(id, position, operationId))

                member _.Observe(id, _) =
                    this.Invoke
                        "authority.observe"
                        (AuthorityOutcome.AlreadyApplied(id, position, operationId))
            }
