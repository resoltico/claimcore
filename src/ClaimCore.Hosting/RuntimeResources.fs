namespace ClaimCore.Hosting

open System
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Postgres

/// One runtime owns the verified primary pool, witness and owner-private key custody.
type internal RuntimeResources(primaryConnection: string, artifactKeyRingPath: string) =
    let dataSource = RuntimeDataSource.create primaryConnection
    let cursorKey = RandomNumberGenerator.GetBytes 32
    let cursorProtection = new CaseListCursorProtection(cursorKey)
    do CryptographicOperations.ZeroMemory cursorKey
    let mutable witness: WitnessProtocol option = None
    let mutable clock: IBusinessTime option = None
    let mutable suppression: (IDisposable * ISuppressionCommitments) option = None
    member _.DataSource = dataSource
    member _.CursorProtection = cursorProtection :> ICaseListCursorProtection
    member _.ArtifactKeyRingPath = artifactKeyRingPath
    member _.Attach(value: WitnessProtocol) = witness <- Some value
    member _.AttachClock(value: IBusinessTime) = clock <- Some value

    member _.AttachSuppression(disposable, commitments) =
        suppression <- Some(disposable, commitments)

    member _.Witness =
        witness |> Option.defaultWith (fun () -> invalidOp "Witness is not attached.")

    member _.Clock =
        clock
        |> Option.defaultWith (fun () -> invalidOp "Business clock is not attached.")

    member _.Suppression =
        suppression
        |> Option.map snd
        |> Option.defaultWith (fun () -> invalidOp "Suppression key is not attached.")

    interface IDisposable with
        member _.Dispose() =
            witness |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            suppression |> Option.iter (fun (disposable, _) -> disposable.Dispose())
            (cursorProtection :> IDisposable).Dispose()
            dataSource.Dispose()
