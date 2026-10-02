namespace ClaimCore.Hosting

open System

/// Holds acquired children until their completed parent takes ownership.
type internal RuntimeConstruction() =
    let owned = ResizeArray<IDisposable>()
    let mutable closed = false

    member _.Own<'resource when 'resource :> IDisposable>(resource: 'resource) =
        if closed then
            raise (ObjectDisposedException("Runtime construction"))

        owned.Add(resource)
        resource

    member _.Transfer(value: 'value) =
        if closed then
            raise (ObjectDisposedException("Runtime construction"))

        closed <- true
        owned.Clear()
        value

    interface IDisposable with
        member _.Dispose() =
            if not closed then
                closed <- true
                let remaining = owned |> Seq.rev |> Seq.toArray
                owned.Clear()
                RuntimeResourceCleanup.disposeAll remaining
