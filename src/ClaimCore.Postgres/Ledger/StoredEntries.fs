namespace ClaimCore.Postgres

open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat

/// Binding database effect references always loads and validates their complete owning root.
module internal StoredEntries =
    let position sequence bytes =
        let sequence = DatabaseSession.admitted Sequence.create sequence

        EntryHash.ofBytes bytes
        |> Option.map (LedgerPosition.create sequence)
        |> Option.defaultWith (fun () ->
            raise (RecordCorruptException "Invalid stored operation position."))

    let operation connection installation id token =
        OperationStorage.find connection installation id token

    let bind connection installation (rows: obj array list) token =
        task {
            let mutable operations = Map.empty
            let effects = ResizeArray<StoredEffect>()

            for row in rows do
                let identity =
                    DatabaseSession.value<Guid> 0 row |> DatabaseSession.admitted OperationId.create

                let index = DatabaseSession.value<int> 1 row

                let! operation =
                    match Map.tryFind identity operations with
                    | Some value -> System.Threading.Tasks.Task.FromResult value
                    | None ->
                        task {
                            let! found =
                                OperationStorage.find connection installation identity token

                            let value =
                                found
                                |> Option.defaultWith (fun () ->
                                    raise (
                                        RecordCorruptException "Effect owner operation is absent."
                                    ))

                            operations <- Map.add identity value operations
                            return value
                        }

                let effect =
                    StoredEffect.find index operation
                    |> Option.defaultWith (fun () ->
                        raise (
                            RecordCorruptException "Stored effect index is absent from its root."
                        ))

                effects.Add effect

            return List.ofSeq effects
        }
