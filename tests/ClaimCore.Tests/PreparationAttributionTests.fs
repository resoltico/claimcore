module ClaimCore.Tests.PreparationAttributionTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private prepareAttribution () =
    let claims = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)
    let request = request 0L (Command.Open registration)

    let principal =
        PrincipalKey.human "https://issuer.example.test/realms/claimcore" "preparer-subject"
        |> Result.defaultWith (fun _ -> failtest "Synthetic actor must be valid")

    let actor: ActorBinding =
        {
            Principal = principal
            ActorId = Guid.Parse("10000000-0000-4000-8000-000000000021")
            GrantRevision = 7L
        }

    let caseId = Guid.Parse("10000000-0000-4000-8000-000000000022")
    let authority: CommandAuthority = { Actor = actor; CaseId = caseId }

    match
        TypedPreparation.prepare
            (claims :> IClaimStore)
            (recovery :> IRecoveryStore)
            (businessTime today)
            authority
            request
            CancellationToken.None
        |> fun pending -> pending.Result
    with
    | PrepareOutcome.Prepared _ -> ()
    | _ -> failtest "Actor-bound preparation must retain the synthetic OPEN"

    let readRetained () =
        match
            (recovery :> IRecoveryStore).Get(request.OperationId, CancellationToken.None).Result
        with
        | Ok(Some(RecoveryStoredOperation.Retained(value, _))) -> value
        | _ -> failtest "Retained preparation is required"

    let retained = readRetained ()
    Expect.equal retained.CaseId caseId "Reserved case ID is explicit and nonempty"
    Expect.equal retained.PreparerActorId actor.ActorId "Original preparer is explicit"
    Expect.equal retained.PreparerGrantRevision actor.GrantRevision "Grant revision is retained"
    Expect.equal retained.ImporterActorId None "Normal prepare is not an import"

    let anotherTentativeCase =
        { authority with
            CaseId = Guid.Parse("10000000-0000-4000-8000-000000000023")
        }

    TypedPreparation.prepare
        (claims :> IClaimStore)
        (recovery :> IRecoveryStore)
        (businessTime today)
        anotherTentativeCase
        request
        CancellationToken.None
    |> fun pending -> pending.Result |> ignore

    Expect.equal (readRetained ()).CaseId caseId "Exact replay preserves first retained case ID"

[<Tests>]
let tests =
    testList
        "preparation attribution"
        [
            testCase
                "[CC-REC-001] retained OPEN preserves case and preparer authority"
                prepareAttribution
        ]
