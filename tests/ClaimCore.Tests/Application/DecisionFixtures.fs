module internal ClaimCore.Tests.DecisionFixtures

open ClaimCore.TestSupport

open System
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures

let instant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
let installation = id 500

let owner =
    PrincipalKey.human "https://issuer.example/" "owner"
    |> Result.defaultWith (string >> failtest)

let other =
    PrincipalKey.human "https://issuer.example/" "other"
    |> Result.defaultWith (string >> failtest)

let service =
    PrincipalKey.service "https://issuer.example/" "service"
    |> Result.defaultWith (string >> failtest)

let calendar =
    { new IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"
        member _.DateAt value = DateOnly.FromDateTime value.UtcDateTime
    }

let private decisionStore =
    { new IStore with
        member _.Read _ =
            failtest "Pure decisions must not read storage"

        member _.Write _ =
            failtest "Pure decisions must not write storage"
    }

let services installation calendar principal =
    Service.create "decision-tests" installation decisionStore calendar principal

let actor principal actorId roles cases : AuthorityView =
    {
        Actor =
            Some
                {
                    ActorId = actorId
                    Principal = principal
                    Enabled = true
                }
        Installation = roles
        Cases = Map.ofList cases
        HasScopedCases = not cases.IsEmpty
    }

let worker =
    actor
        owner
        ((CaseFixtures.actorId (id 1)))
        [ Role.Owner; Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward ]
        []

let head =
    {
        Sequence = CaseFixtures.sequence 3L
        EntryHash = EntryHash.zero
    }

let nonce = Array.init 32 (fun index -> byte (index + 1))

let opening n reference =
    request n reference 0L (AuthoredCaseAct.Open registration)

type LockedFacts<'Facts> =
    {
        Head: LedgerPosition
        PayloadNonce: byte array
        IsFenced: bool
        Read: 'Facts
    }

let locked authority current existing : LockedFacts<CaseFacts> =
    {
        Head = head
        PayloadNonce = nonce
        IsFenced = false
        Read =
            {
                Assignee = None
                Maintenance = RegisterMaintenance.initial
                ObservedAt = instant
                Actor = authority
                ExistingOperation = existing
                Case = current
            }
    }

let decide request view =
    CaseExecution.execute
        (services installation calendar owner)
        (ClaimCore.TestSupport.CaseFixtures.command request)
        {
            Head = view.Head
            Fenced = view.IsFenced
            Nonce = view.PayloadNonce
        }
        view.Read

let executeAuthority installation principal request (view: LockedFacts<AuthorityExecutionFacts>) =
    AuthorityExecution.execute
        (services installation calendar principal)
        request
        {
            Head = view.Head
            Fenced = view.IsFenced
            Nonce = view.PayloadNonce
        }
        view.Read

let reviewAuthority installation principal (request: ClaimCore.Domain.AuthorityRequest) view =
    match Authority.parse request with
    | Ok command ->
        AuthorityExecution.review (services installation calendar principal) command view
    | Error refusal -> Verdict.Refuse refusal

let provisionAuthority
    installation
    operation
    principal
    (view: LockedFacts<AuthorityExecutionFacts>)
    =
    AuthorityExecution.provision
        (services installation calendar principal)
        operation
        {
            Head = view.Head
            Fenced = view.IsFenced
            Nonce = view.PayloadNonce
        }
        view.Read

let appended =
    function
    | Verdict.Proceed(entry, projection, receipt) -> entry, projection, receipt
    | _ -> failtest "Expected a pure append decision"

let created n reference =
    let request = opening n reference

    let entry, projection, receipt =
        decide request (locked worker None None) |> appended

    let current =
        match projection with
        | CaseProjection.Insert(id, snapshot, index) ->
            {
                CaseId = id
                Snapshot = snapshot
                Index = index
                SnapshotBytes = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
            }
        | _ -> failtest "Opening inserts a case"

    request, LedgerEntries.stored entry, current, receipt

let authorityView caller target existing : LockedFacts<AuthorityExecutionFacts> =
    {
        Head = head
        PayloadNonce = nonce
        IsFenced = false
        Read =
            {
                Maintenance = RegisterMaintenance.initial
                ObservedAt = instant
                Actor = caller
                ExistingOperation = existing
                Target = target
                TargetCase = None
                EnabledHumanOwners = Some 2
                ActorCount = Some 2
            }
    }

let admitted n target act : AuthorityCommand =
    AuthorityRequests.input (id n) target act
    |> Result.bind Authority.parse
    |> Result.defaultWith (fun refusal -> failtestf "Invalid authority test command: %A" refusal)
