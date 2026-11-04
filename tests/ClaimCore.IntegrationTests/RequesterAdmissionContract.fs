module ClaimCore.TestSupport.RequesterAdmissionContract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None
let private missing = CaseSelector.Reference "ADMISSION-MISSING"
let private malformed = CaseSelector.Reference "bad reference"

let private principal subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.defaultWith (string >> failtest)

let private code (pending: Task<Outcome<'value>>) =
    task {
        let! reply = pending

        return
            match reply with
            | Outcome.Rejected reason -> DiagnosticCodecs.code reason
            | other -> failtestf "Expected a refusal, got %A" other
    }

let private reads core =
    [
        "case selector", (fun () -> code (Service.get core { Target = malformed } token))
        "case limit",
        (fun () ->
            code (Service.list core (Inputs.caseListPage { Limit = 0; AfterCursor = None }) token))
        "history selector",
        (fun () ->
            code (
                Service.history
                    core
                    {
                        Target = malformed
                        Limit = 10
                        AfterCursor = None
                        Detail = HistoryDetail.Summary
                    }
                    token
            ))
        "history limit",
        (fun () ->
            code (
                Service.history
                    core
                    {
                        Target = missing
                        Limit = 0
                        AfterCursor = None
                        Detail = HistoryDetail.Summary
                    }
                    token
            ))
        "assignee limit",
        (fun () ->
            code (
                Service.assignees
                    core
                    {
                        Target = missing
                        Limit = 0
                        AfterCursor = None
                    }
                    token
            ))
        "assignee selector",
        (fun () ->
            code (
                Service.assignees
                    core
                    {
                        Target = malformed
                        Limit = 10
                        AfterCursor = None
                    }
                    token
            ))
        "people limit",
        (fun () ->
            code (
                Service.actors
                    core
                    {
                        Text = None
                        Role = None
                        Limit = 0
                        AfterCursor = None
                    }
                    token
            ))
        "grants limit",
        (fun () ->
            code (
                Service.grants
                    core
                    {
                        ActorId = id 80001
                        Limit = 0
                        AfterCursor = None
                    }
                    token
            ))
        "authority history limit",
        (fun () ->
            code (
                Service.authorityHistory
                    core
                    {
                        TargetActorId = None
                        Limit = 0
                        AfterCursor = None
                    }
                    token
            ))
        "timeline selector",
        (fun () ->
            code (
                Service.timeline
                    core
                    {
                        Target = malformed
                        Filter = None
                        Limit = 10
                        AfterCursor = None
                    }
                    token
            ))
        "timeline limit",
        (fun () ->
            code (
                Service.timeline
                    core
                    {
                        Target = missing
                        Filter = None
                        Limit = 0
                        AfterCursor = None
                    }
                    token
            ))
        "reminder id",
        (fun () ->
            code (
                Service.reminder
                    core
                    {
                        Target = missing
                        ReminderId = Guid.Empty
                    }
                    token
            ))
        "reminder selector",
        (fun () ->
            code (
                Service.reminder
                    core
                    {
                        Target = malformed
                        ReminderId = id 80002
                    }
                    token
            ))
        "reminder limit",
        (fun () ->
            code (
                Service.reminders
                    core
                    {
                        Target = None
                        View = None
                        State = None
                        Due = None
                        Assignment = None
                        Text = None
                        Limit = 0
                        AfterCursor = None
                        IncludeInactive = None
                    }
                    token
            ))
        "reminder text",
        (fun () ->
            code (
                Service.reminders
                    core
                    {
                        Target = None
                        View = None
                        State = None
                        Due = None
                        Assignment = None
                        Text = Some(String('x', 201))
                        Limit = 10
                        AfterCursor = None
                        IncludeInactive = None
                    }
                    token
            ))
        "activity limit",
        (fun () ->
            code (
                Service.activity
                    core
                    {
                        Target = missing
                        Limit = 0
                        AfterCursor = None
                        Detail = HistoryDetail.Summary
                    }
                    token
            ))
        "observe id",
        (fun () ->
            task {
                let! reply = Service.observe core { OperationId = Guid.Empty } token in
                return! code (Task.FromResult reply.Outcome)
            })
        "status id",
        (fun () ->
            task {
                let! reply = Service.status core { OperationId = Guid.Empty } token in
                return! code (Task.FromResult reply.Outcome)
            })
        "resolve id",
        (fun () ->
            task {
                let! reply = Service.resolve core { OperationId = Guid.Empty } token in
                return! code (Task.FromResult reply.Outcome)
            })
    ]

let internal malformedWrites =
    [
        OperationInput.Authority(
            authorityRequest
                Guid.Empty
                (principal "new-person")
                (AuthorityAct.Register Inputs.actorProfile)
        )
        OperationInput.Authority(
            authorityRequest
                (id 80100)
                (principal "new-person")
                (AuthorityAct.Register { DisplayName = ""; Contact = None })
        )
        OperationInput.Work
            {
                OperationId = id 80101
                Target = missing
                ExpectedRevision = 0L
                Act =
                    WorkAct.UpdateWork
                        {
                            Responsible = WorkValue.Keep
                            Summary = WorkValue.Set ""
                        }
            }
        OperationInput.Reminder
            {
                OperationId = id 80102
                Target = missing
                Act = AuthoredReminderAct.Create { Action = ""; Due = "invalid" }
            }
        OperationInput.Reminder
            {
                OperationId = id 80103
                Target = missing
                Act = AuthoredReminderAct.Complete(Guid.Empty, -1L)
            }
    ]

let private mutationCode core input =
    task {
        let! reply = Service.execute core input token

        return
            match reply.Outcome with
            | ExecuteResult.Rejected reason -> DiagnosticCodecs.code reason
            | other -> failtestf "Expected refused write, got %A" other
    }

let internal lifecycle fixture =
    task {
        let! owner = caseWorker fixture
        let authored = List.item 1 malformedWrites
        let wire = Codec.encode Api.operationExecute.Input authored

        Expect.equal
            (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input wire)
            (Ok authored)
            "The malformed profile is valid transport syntax and reaches Application admission"

        let disabled = principal "disabled-input-caller"
        let roleless = principal "roleless-input-caller"

        for index, target in List.indexed [ disabled; roleless ] do
            let! registered =
                Service.execute
                    owner
                    (OperationInput.Authority(
                        authorityRequest
                            (id (80010 + index))
                            target
                            (AuthorityAct.Register Inputs.actorProfile)
                    ))
                    token

            acceptedAuthority registered

        let! turnedOff =
            Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 80012) disabled AuthorityAct.Disable))
                token

        acceptedAuthority turnedOff

        for caller in [ principal "unknown-input-caller"; disabled ] do
            let current = core fixture caller

            for label, query in reads current do
                let! actual = query ()

                Expect.equal
                    actual
                    "RESOURCE_UNAVAILABLE"
                    (label + " must hide business validation from unavailable callers")

            for input in malformedWrites do
                let! actual = mutationCode current input

                Expect.equal
                    actual
                    "RESOURCE_UNAVAILABLE"
                    "Unavailable execute caller receives no business input detail"

                match fixture.Store with
                | :? MemoryStore.Store as memory ->
                    Expect.equal
                        memory.Events
                        [ "lock"; "read" ]
                        "Refused malformed writes own the write lock but neither append nor allocate a payload nonce"
                | _ -> ()

                let! reply = Service.preview current input token
                let! actual = code (Task.FromResult reply.Outcome)

                Expect.equal
                    actual
                    "RESOURCE_UNAVAILABLE"
                    "Unavailable preview caller receives no business input detail"

            for operation in [ id 80200; Guid.Empty ] do
                let! reply = Service.status current { OperationId = operation } token
                let! actual = code (Task.FromResult reply.Outcome)

                Expect.equal
                    actual
                    "RESOURCE_UNAVAILABLE"
                    "Both absent valid and empty IDs retain requester privacy"

        for label, query in reads owner do
            let! actual = query ()

            Expect.isTrue
                (actual <> "RESOURCE_UNAVAILABLE")
                (label + " retains authorized input diagnostics")

        for input in malformedWrites do
            let! actual = mutationCode owner input

            Expect.equal
                actual
                "INVALID_INPUT"
                "Enabled authorized caller retains accumulated input issues"

            let! preview = Service.preview owner input token
            let! actual = code (Task.FromResult preview.Outcome)
            Expect.equal actual "INVALID_INPUT" "Enabled preview retains the same input diagnostics"

        let roleless = core fixture roleless
        let! empty = Service.status roleless { OperationId = Guid.Empty } token
        let! actual = code (Task.FromResult empty.Outcome)

        Expect.equal
            actual
            "INVALID_INPUT"
            "A valid enabled roleless caller retains own recovery validation"

        let! unresolved = Service.status roleless { OperationId = id 80201 } token

        Expect.equal
            unresolved.Outcome
            (Outcome.Succeeded OperationStatus.Unresolved)
            "Roleless own recovery is not a new capability restriction"

        let! resolved = Service.resolve roleless { OperationId = id 80201 } token

        Expect.equal
            resolved.Outcome
            (Outcome.Succeeded Resolved.NotAccepted)
            "Roleless caller retains its own recovery fence"
    }

let internal targetPrivacy fixture =
    task {
        let! owner = caseWorker fixture
        let reader = principal "scope-reader"

        let! registered =
            Service.execute
                owner
                (OperationInput.Authority(
                    authorityRequest (id 80300) reader (AuthorityAct.Register Inputs.actorProfile)
                ))
                token

        acceptedAuthority registered

        let! opened =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 80301 "ADMISSION-READABLE" 0L (AuthoredCaseAct.Open registration)
                ))
                token

        let parent = accepted opened |> caseReceipt

        let! other =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 80302 "ADMISSION-PRIVATE" 0L (AuthoredCaseAct.Open registration)
                ))
                token

        accepted other |> ignore

        let! granted =
            grant
                owner
                (id 80303)
                reader
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseId parent.CaseId
                }
                true

        acceptedAuthority granted
        let core = core fixture reader

        for number, target, expected in
            [
                80304, "ADMISSION-READABLE", "NOT_AUTHORIZED"
                80305, "ADMISSION-PRIVATE", "RESOURCE_UNAVAILABLE"
                80306, "ADMISSION-MISSING", "RESOURCE_UNAVAILABLE"
            ] do
            let! actual =
                mutationCode
                    core
                    (Inputs.caseOperation (
                        request number target 1L (AuthoredCaseAct.Bare BareAct.Close)
                    ))

            Expect.equal
                actual
                expected
                "Registered caller's existing target/capability distinctions remain unchanged"
    }
