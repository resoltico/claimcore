module internal ClaimCore.TestSupport.MemoryStoreState

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Protocol
open ClaimCore.Domain


type internal State =
    {
        Head: LedgerPosition
        Entries: Map<Guid, StoredEntry>
        Cases: Map<Guid, StoredCase>
        Actors: Map<Guid, Actor>
        Grants: Set<Guid * Role * Guid option>
        Fences: Set<Guid * Guid>
    }

let internal compareId (left: Guid) (right: Guid) =
    StringComparer.Ordinal.Compare(left.ToString("N"), right.ToString("N"))

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
