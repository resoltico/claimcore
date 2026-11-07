module internal ClaimCore.IntegrationTests.ObservedTransactions

open System
open ClaimCore.Application

/// Record the real provider instant without replacing transaction behavior or results.
type Store(inner: IStore) =
    let mutable observed: DateTimeOffset option = None

    member _.ObservedAt =
        observed
        |> Option.defaultWith (fun () -> invalidOp "No transaction was observed.")

    interface IStore with
        member _.Context query =
            task {
                let! result = inner.Context query

                match result with
                | Ok facts -> observed <- Some facts.ObservedAt
                | Error _ -> ()

                return result
            }

        member _.Read operation =
            inner.Read
                { operation with
                    Work =
                        fun tx ->
                            observed <- Some tx.ObservedAt
                            operation.Work tx
                }

        member _.Write operation =
            inner.Write
                { operation with
                    Work =
                        fun tx ->
                            observed <- Some tx.ObservedAt
                            operation.Work tx
                }
