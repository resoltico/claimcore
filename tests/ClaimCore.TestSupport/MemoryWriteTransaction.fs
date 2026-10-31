module internal ClaimCore.TestSupport.MemoryWriteTransaction

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Protocol
open ClaimCore.Domain

open ClaimCore.TestSupport.MemoryStoreState
open ClaimCore.TestSupport.MemoryReadTransaction

[<NoEquality; NoComparison>]
type internal WriteInputs =
    {
        Get: unit -> State
        Set: State -> unit
        InstallationId: Guid
        Observed: DateTimeOffset
        Head: LedgerPosition
        Nonce: byte array
        Read: unit -> unit
        FactRead: AuthorityFact -> unit
    }

type internal Writer(inputs: WriteInputs) =
    inherit
        Reader(
            inputs.Get,
            inputs.InstallationId,
            inputs.Observed,
            inputs.Read,
            factRead = inputs.FactRead
        )

    let get = inputs.Get
    let set = inputs.Set
    let head = inputs.Head
    let nonce = inputs.Nonce
    let read = inputs.Read

    interface IWriteTx with
        member _.Head = head
        member _.Nonce = nonce

        member _.IsFenced
            ({
                 FenceKey.ActorId = actor
                 OperationId = operation
             })
            =
            read ()
            Task.FromResult(Set.contains (actor, operation) (get ()).Fences)

        member _.Append draft =
            let state = get ()

            let control =
                match draft.Subject with
                | EntryTarget.NonAcceptance _ -> true
                | _ -> false

            if
                draft.Sequence <> Sequence.successor state.Head.Sequence
                || draft.PreviousHash <> EntryHash.toBytes state.Head.EntryHash
                || (not control && Map.containsKey draft.OperationId state.Entries)
            then
                invalidOp "An append must extend the owned head with a fresh operation."

            set
                { state with
                    Head =
                        {
                            Sequence = draft.Sequence
                            EntryHash = EntryHash.ofBytes draft.EntryHash |> Option.get
                        }
                    Controls =
                        if control then
                            Map.add
                                draft.Sequence
                                (LedgerEntries.stored draft |> copyEntry)
                                state.Controls
                        else
                            state.Controls
                    Entries =
                        if control then
                            state.Entries
                        else
                            Map.add
                                draft.OperationId
                                (LedgerEntries.stored draft |> copyEntry)
                                state.Entries
                }

            Task.FromResult(())

        member _.ProjectCase projection =
            let state = get ()

            let cases =
                match projection with
                | CaseProjection.Insert(id, snapshot, index)
                | CaseProjection.Update(id, snapshot, index) ->
                    Map.add
                        id
                        {
                            CaseId = id
                            Snapshot = snapshot
                            Index = index
                            SnapshotBytes = CaseRecord.encodeSnapshot snapshot
                        }
                        state.Cases
                | CaseProjection.Erase(id, revision, changedAt) ->
                    Map.add
                        id
                        {
                            CaseId = id
                            SnapshotBytes = Array.empty
                            Index =
                                {
                                    Status = None
                                    Progress = None
                                    SearchText = None
                                    NotifiedOn = None
                                    Claimed = None
                                    Payable = None
                                    ChangedAt = changedAt
                                }
                            Snapshot =
                                { state.Cases[id].Snapshot with
                                    Revision = revision
                                    State = CaseState.Erased
                                }
                        }
                        state.Cases

            let entries =
                match projection with
                | CaseProjection.Erase(id, _, _) ->
                    state.Entries
                    |> Map.map (fun _ entry ->
                        match entry.Subject with
                        | EntryTarget.Case(caseId, _, _, _) when caseId = id ->
                            { entry with
                                Payload = None
                                PayloadNonce = None
                            }
                        | _ -> entry)
                | _ -> state.Entries

            set
                { state with
                    Cases = cases
                    Entries = entries
                }

            Task.FromResult(())

        member _.ProjectAuthority projection =
            let state = get ()

            let changed =
                match projection with
                | AuthorityProjection.Register(id, principal) ->
                    { state with
                        Actors =
                            Map.add
                                id
                                {
                                    ActorId = id
                                    Principal = principal
                                    Enabled = true
                                }
                                state.Actors
                    }
                | AuthorityProjection.ProvisionOwner(id, principal) ->
                    { state with
                        Actors =
                            Map.add
                                id
                                {
                                    ActorId = id
                                    Principal = principal
                                    Enabled = true
                                }
                                state.Actors
                        Grants = Set.add (id, Role.Owner, None) state.Grants
                    }
                | AuthorityProjection.SetEnabled(id, enabled) ->
                    { state with
                        Actors =
                            Map.add
                                id
                                { state.Actors[id] with
                                    Enabled = enabled
                                }
                                state.Actors
                    }
                | AuthorityProjection.AddGrant(id, role, scope) ->
                    { state with
                        Grants = Set.add (id, role, scope) state.Grants
                    }
                | AuthorityProjection.RemoveGrant(id, role, scope) ->
                    { state with
                        Grants = Set.remove (id, role, scope) state.Grants
                    }

            set changed
            Task.FromResult(())

        member _.Fence
            ({
                 FenceKey.ActorId = actor
                 OperationId = operation
             })
            =
            set
                { get () with
                    Fences = Set.add (actor, operation) (get ()).Fences
                }

            Task.FromResult(())

/// One transactional double for Application tests; refusals and replay discard pending state.
