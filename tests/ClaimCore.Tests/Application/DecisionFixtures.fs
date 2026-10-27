module internal ClaimCore.Tests.DecisionFixtures

open ClaimCore.TestSupport

open System
open Expecto
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application
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
        HasAssignedCases = not cases.IsEmpty
    }

let worker =
    actor owner (id 1) [ Role.Owner; Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward ] []

let head =
    {
        Sequence = 3L
        EntryHash = Array.zeroCreate<byte> 32
    }

let nonce = Array.init 32 (fun index -> byte (index + 1))

let opening n reference =
    request n reference 0L (UnvalidatedCaseAct.Open registration)

type LockedFacts<'Facts> =
    {
        Head: LedgerHead
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
                ObservedAt = instant
                Actor = authority
                ExistingOperation = existing
                Case = current
            }
    }

let decide request view =
    CaseExecution.execute
        installation
        calendar
        owner
        (ClaimCore.TestSupport.CaseFixtures.command request)
        view.Head
        view.IsFenced
        view.PayloadNonce
        view.Read

let executeAuthority installation principal request (view: LockedFacts<AuthorityFacts>) =
    AuthorityExecution.execute
        installation
        principal
        request
        view.Head
        view.IsFenced
        view.PayloadNonce
        view.Read

let reviewAuthority installation principal (request: ClaimCore.Domain.AuthorityRequest) view =
    match AuthorityRequests.admit request with
    | Ok command -> AuthorityExecution.review installation principal command view
    | Error refusal -> Verdict.Refuse refusal

let provisionAuthority installation operation principal (view: LockedFacts<AuthorityFacts>) =
    AuthorityExecution.provision
        installation
        operation
        principal
        view.Head
        view.IsFenced
        view.PayloadNonce
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
        | CaseProjection.Insert(id, snapshot) -> { CaseId = id; Snapshot = snapshot }
        | _ -> failtest "Opening inserts a case"

    request, LedgerEntries.stored entry, current, receipt

let authorityView caller target existing : LockedFacts<AuthorityFacts> =
    {
        Head = head
        PayloadNonce = nonce
        IsFenced = false
        Read =
            {
                ObservedAt = instant
                Actor = caller
                ExistingOperation = existing
                Target = target
                TargetCase = None
                EnabledHumanOwners = 2
                ActorCount = 2
            }
    }

let admitted n target act : AdmittedAuthorityRequest =
    {
        OperationId = id n
        Target = target
        Act = act
    }
