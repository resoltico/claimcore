module ClaimCore.Tests.ServiceBoundaryTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.TestSupport

let private id n =
    OperationIds.numberedAt (DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)) n

let private installation = id 500

let private owner =
    PrincipalKey.human "https://issuer.example/" "owner"
    |> Result.toOption
    |> Option.get

let private other =
    PrincipalKey.human "https://issuer.example/" "other"
    |> Result.toOption
    |> Option.get

let private calendar =
    { new IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"
        member _.DateAt value = DateOnly.FromDateTime value.UtcDateTime
    }

/// A port failure has no transaction state and never invokes application callbacks.
type private FailingStore() =
    let mutable calls = 0
    member _.Calls = calls
    member val WriteFailure = StoreFailure.Unavailable with get, set

    interface IStore with
        member _.Context _ =
            calls <- calls + 1
            Task.FromResult(Error StoreFailure.Unavailable)

        member _.Read _ =
            calls <- calls + 1
            Task.FromResult(Error StoreFailure.Unavailable)

        member this.RecordDisclosure(_, _) =
            calls <- calls + 1
            Task.FromResult(Error this.WriteFailure)

        member this.Write _ =
            calls <- calls + 1
            Task.FromResult(Error this.WriteFailure)

let private unavailable () = FailingStore()

let private opening operation values =
    Submissions.create "case.open" operation (Binding.CaseNew Revision.zero) values

let private registration =
    DomainInputs.registration
    |> RegistrationInput.values
    |> Submissions.fields
    |> Map.add (Submissions.field BusinessField.CaseReference) (SubmissionValue.Text "BOUNDARY")

let private inputs () =
    [
        opening (id 470) registration
        Submissions.create
            "people.register"
            (id 471)
            (Binding.PeopleNew Revision.zero)
            (Map.fold
                (fun result key value -> Map.add key value result)
                (Submissions.principal other)
                (Submissions.profile DomainInputs.actorProfile))
    ]

let private malformed () =
    async {
        let probe = unavailable ()

        let core =
            Service.create
                BuildIdentity.current.Version
                installation
                (probe :> IStore)
                calendar
                owner

        let invalid =
            [
                opening Guid.Empty registration
                opening
                    (id 471)
                    (Map.add
                        (Submissions.field BusinessField.PolicyNumber)
                        (SubmissionValue.Text " Padded ")
                        registration)
                Submissions.create
                    "people.register"
                    Guid.Empty
                    (Binding.PeopleNew Revision.zero)
                    (Map.fold
                        (fun result key value -> Map.add key value result)
                        (Submissions.principal other)
                        (Submissions.profile DomainInputs.actorProfile))
            ]

        for input in invalid do
            let! execute =
                (Service.executeSubmission core (input) (CancellationToken.None))
                |> Async.AwaitTask

            let! preview =
                (Service.previewSubmission core (input) (CancellationToken.None))
                |> Async.AwaitTask

            Expect.equal
                execute.Outcome
                (ExecuteResult.Failed CoreFault.StoreUnavailable)
                "D2 requires current caller state before any Application business refusal"

            Expect.equal
                preview.Outcome
                (Outcome.Failed CoreFault.StoreUnavailable)
                "Preview cannot reveal business validation without its requester snapshot"

        Expect.equal
            probe.Calls
            6
            "Every malformed execute and preview consults exactly one snapshot"

    }

let private failures () =
    async {
        for failure, expected in
            [
                StoreFailure.Unavailable, ExecuteResult.Failed CoreFault.StoreUnavailable
                StoreFailure.Cancelled, ExecuteResult.Failed CoreFault.StoreUnavailable
                StoreFailure.Internal, ExecuteResult.Failed CoreFault.InternalError
                StoreFailure.CommitOutcomeUnknown, ExecuteResult.OutcomeUnknown
            ] do
            let probe = unavailable ()
            probe.WriteFailure <- failure

            let core =
                Service.create
                    BuildIdentity.current.Version
                    installation
                    (probe :> IStore)
                    calendar
                    owner

            for input in inputs () do
                let! reply =
                    (Service.executeSubmission core (input) (CancellationToken.None))
                    |> Async.AwaitTask

                Expect.equal reply.OperationId input.OperationId "Failure retains identity"

                Expect.equal reply.Outcome expected "Only dispatched commit uncertainty is unknown"

            Expect.equal probe.Calls 2 "Each write dispatched once"
    }

let private cancelled () =
    async {
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        let probe = unavailable ()

        let core =
            Service.create
                BuildIdentity.current.Version
                installation
                (probe :> IStore)
                calendar
                owner

        for input in inputs () do
            let! execute =
                (Service.executeSubmission core (input) (cancellation.Token)) |> Async.AwaitTask

            let! preview =
                (Service.previewSubmission core (input) (cancellation.Token)) |> Async.AwaitTask

            Expect.equal execute.OperationId input.OperationId "Cancelled write identity"

            Expect.equal
                execute.Outcome
                (ExecuteResult.Failed CoreFault.StoreUnavailable)
                "Definite predispatch failure"

            Expect.equal
                preview.Outcome
                (Outcome.Failed CoreFault.StoreUnavailable)
                "Cancelled preview"

        Expect.equal probe.Calls 0 "No writes or previews dispatched"

        let! context = (Service.context core () cancellation.Token) |> Async.AwaitTask

        let! current =
            (Service.get
                core
                ({
                    Target = CaseSelector.Reference("BOUNDARY")
                })
                (cancellation.Token))
            |> Async.AwaitTask

        let! observed =
            (Service.operationGet core ({ OperationId = id 470 }) (cancellation.Token))
            |> Async.AwaitTask

        Expect.equal context (Outcome.Failed CoreFault.StoreUnavailable) "Cancelled context"
        Expect.equal current (Outcome.Failed CoreFault.StoreUnavailable) "Cancelled case"

        Expect.equal
            observed.Outcome
            (Outcome.Failed CoreFault.StoreUnavailable)
            "Cancelled observation"
    }

[<Tests>]
let tests =
    testList
        "Core port boundaries"
        [
            testCaseAsync
                "business input refusals require current requester snapshots"
                (malformed ())
            testCaseAsync "write failures preserve identity and dispatch uncertainty" (failures ())
            testCaseAsync
                "predispatch cancellation and cancelled reads remain definite"
                (cancelled ())
        ]
