module internal ClaimCore.TestSupport.MemoryStoreState

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Domain


type internal State =
    {
        Maintenance: RegisterMaintenance
        Head: LedgerPosition
        Entries: Map<OperationId, StoredEntry>
        Controls: Map<Sequence, RetainedControl>
        Cases: Map<CaseId, StoredCase>
        Actors: Map<ActorId, Actor>
        Grants: Set<ActorId * Role * CaseId option>
        Fences: Map<ActorId * OperationId, DateTimeOffset>
    }

let internal compareId format left right =
    StringComparer.Ordinal.Compare(format left, format right)

let internal page limit position items =
    let visible = List.truncate limit items

    {
        Items = visible
        Next =
            if List.length items > limit then
                List.tryLast visible |> Option.map position
            else
                None
    }

let internal copyEntry (entry: StoredEntry) =
    { entry with
        EntryHash = Array.copy entry.EntryHash
        PayloadCommitment = Array.copy entry.PayloadCommitment
        Payload = entry.Payload |> Option.map Array.copy
        PayloadNonce = entry.PayloadNonce |> Option.map Array.copy
    }

exception CorruptProjection

let internal readCase (entries: Map<OperationId, StoredEntry>) (record: StoredCase) =
    let identity =
        entries
        |> Map.tryFind (OperationId.create (CaseId.value record.CaseId) |> Option.get)
        |> Option.exists (fun entry ->
            entry.CreationReference = Some record.Snapshot.Reference
            && entry.Subject
               |> function
                   | EntryTarget.Case(id, _, CaseActKind.Open, _) -> id = record.CaseId
                   | _ -> false)

    let valid =
        match record.Snapshot.State with
        | CaseState.Erased -> Array.isEmpty record.SnapshotBytes
        | CaseState.Active _
        | CaseState.Voided _ ->
            match CaseRecord.decodeSnapshot record.SnapshotBytes with
            | Ok snapshot -> snapshot = record.Snapshot
            | Error _ -> false

    if not identity || record.Snapshot.CaseId <> record.CaseId || not valid then
        raise CorruptProjection

    { record with
        SnapshotBytes = Array.copy record.SnapshotBytes
    }
