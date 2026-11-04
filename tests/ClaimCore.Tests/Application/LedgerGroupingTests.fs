module ClaimCore.Tests.LedgerGroupingTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let private installation = Guid.Parse "90000000-0000-4000-8000-000000000099"

let private actor =
    CaseFixtures.actorId (Guid.Parse "a0000000-0000-4000-8000-000000000099")

let private operation =
    CaseFixtures.operationId (Guid.Parse "b0000000-0000-4000-8000-000000000099")

let private instant = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)

let private accepted result =
    Result.defaultWith (fun fault -> failtestf "Unexpected fault: %A" fault) result

// These are structural storage witnesses, not authority admission histories.
let private drafts nonce =
    LedgerEntries.createAuthorityOperation
        AuthorityKind.Register
        nonce
        {
            InstallationId = installation
            Head = LedgerPosition.create Sequence.zero EntryHash.zero
            Instant = instant
            ActorId = actor
            OperationId = operation
            Effects =
                [ AuthorityKind.Register; AuthorityKind.SetProfile ]
                |> List.map (fun kind ->
                    {
                        Subject = EntryTarget.Authority(actor, kind)
                        Request = [| byte (if kind = AuthorityKind.Register then 1 else 2) |]
                        Snapshot = Array.empty
                    })
        }
    |> accepted

let private freshDrafts () =
    let mutable next = 0uy

    drafts (fun () ->
        next <- next + 1uy
        Array.create 32 next)

let private metadataControls () =
    let rows = freshDrafts () |> List.map LedgerEntries.stored
    let root, child = rows[0], rows[1]

    let rejected entries =
        Expect.isError (StoredOperation.create entries) "Incomplete or incoherent operation"

    Expect.equal
        (StoredOperation.create rows |> accepted).Position.Sequence
        (CaseFixtures.sequence 2L)
        "Tail owns proof"

    for entries in [ []; [ root ]; [ child ]; [ child; root ]; [ root; root ]; rows @ [ child ] ] do
        rejected entries

    let different =
        CaseFixtures.actorId (Guid.Parse "a0000000-0000-4000-8000-000000000098")

    Expect.isNone
        (LedgerEffects.create (Some AuthorityKind.Register) 3 1)
        "Invalid group counts cannot enter the metadata type"

    for changed in
        [
            { child with
                Effects = LedgerEffects.authority AuthorityKind.ProvisionOwner 2 1
            }
            { child with ActorId = different }
            { child with
                OperationId = CaseFixtures.operationId (Guid.NewGuid())
            }
            { child with
                RecordedAt = instant.AddSeconds 1
            }
            { child with
                Subject = EntryTarget.Authority(different, AuthorityKind.Grant)
            }
            { child with
                Sequence = CaseFixtures.sequence 3L
            }
            { child with EntryHash = Array.empty }
            { child with
                PayloadNonce = root.PayloadNonce
            }
        ] do
        rejected [ root; changed ]

    let erased =
        rows
        |> List.map (fun row ->
            { row with
                Payload = None
                PayloadNonce = None
            })

    Expect.isOk
        (StoredOperation.create erased)
        "Accepted metadata survives authorized payload erasure"

let private nonceAndChain () =
    let entries = freshDrafts ()
    Expect.equal entries[0].PayloadNonce (Array.create 32 1uy) "First provider result"
    Expect.equal entries[1].PayloadNonce (Array.create 32 2uy) "Independent second provider call"
    Expect.equal entries[1].PreviousHash entries[0].EntryHash "Each effect extends its predecessor"

    Expect.isError
        (LedgerEntries.createAuthorityOperation
            AuthorityKind.Register
            (fun () -> Array.zeroCreate 32)
            {
                InstallationId = installation
                Head = LedgerPosition.create Sequence.zero EntryHash.zero
                Instant = instant
                ActorId = actor
                OperationId = operation
                Effects =
                    entries
                    |> List.map (fun entry ->
                        {
                            Subject = entry.Subject
                            Request = [| 1uy |]
                            Snapshot = Array.empty
                        })
            })
        "A provider reusing its nonce cannot create an operation"

    let identity requests =
        LedgerEntries.authorityIdentityBytes AuthorityKind.Register requests

    Expect.equal (identity [ [| 1uy |] ]) [| 1uy |] "Singleton identity stays its canonical request"

    Expect.notEqual
        (identity [ [| 1uy |]; [| 2uy |] ])
        (identity [ [| 1uy |]; [| 3uy |] ])
        "Every child affects exact replay"

    Expect.notEqual
        (identity [ [| 1uy |]; [| 2uy; 3uy |] ])
        (identity [ [| 1uy; 2uy |]; [| 3uy |] ])
        "Length framing is unambiguous"

let private rollback () =
    async {
        use memory = new MemoryStore.Store(installation, instant)
        let store = memory :> IStore
        let entries = freshDrafts ()

        let write action =
            store.Write
                {
                    Work = action
                    CancellationToken = CancellationToken.None
                }
            |> Async.AwaitTask

        let read () =
            store.Read
                {
                    Work = (fun tx -> tx.FindOperation operation)
                    CancellationToken = CancellationToken.None
                }
            |> Async.AwaitTask

        for stopAfter in [ 1; 2 ] do
            let! result =
                write (fun tx ->
                    task {
                        for entry in entries |> List.take stopAfter do
                            do! tx.Append entry

                        return failwith "injected after physical effect"
                    })

            Expect.equal
                result
                (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
                "Injected failure rolls back"

            let! stored = read ()
            Expect.equal stored (Ok None) "No accepted prefix survives rollback"

        let! truncated =
            write (fun tx ->
                task {
                    do! tx.Append entries[0]
                    return Verdict.Proceed()
                })

        Expect.equal truncated (Error StoreFailure.Integrity) "Commit rejects a truncated operation"

        let! complete =
            write (fun tx ->
                task {
                    for entry in entries do
                        do! tx.Append entry

                    return Verdict.Proceed()
                })

        Expect.equal complete (Ok(Verdict.Proceed())) "Whole group commits once"
        let! stored = read ()

        Expect.equal
            ((stored |> accepted |> Option.get).Entries.Length)
            2
            "Lookup returns all physical effects"
    }

let private foldCompletion () =
    let target = ActorId.ofOperationId operation

    let principal =
        PrincipalKey.human "https://issuer.example/" "synthetic-group-owner" |> accepted

    let mutable nonce = 0uy

    let entries =
        LedgerEntries.createAuthorityOperation
            AuthorityKind.ProvisionOwner
            (fun () ->
                nonce <- nonce + 1uy
                Array.create 32 nonce)
            {
                InstallationId = installation
                Head = LedgerPosition.genesis
                Instant = instant
                ActorId = target
                OperationId = operation
                Effects =
                    [
                        AuthorityEffect.ProvisionOwner(PrincipalKey.toInput principal)
                        AuthorityEffect.SetProfile(
                            ActorProfile.parse Inputs.ownerProfile |> accepted
                        )
                    ]
                    |> List.map (fun act ->
                        {
                            Subject = EntryTarget.Authority(target, AuthorityEffect.kind act)
                            Request =
                                AuthorityRecord.encode
                                    {
                                        OperationId = OperationId.value operation
                                        TargetActorId = target
                                        Effect = act
                                    }
                            Snapshot = Array.empty
                        })
            }
        |> accepted

    let metadata (entry: EntryDraft) : LedgerMetadata =
        let kind =
            match entry.Subject with
            | EntryTarget.Authority(_, kind) -> kind
            | _ -> failtest "Authority witness"

        {
            Effects = entry.Effects
            InstallationId = installation
            Sequence = entry.Sequence
            PreviousHash = entry.PreviousHash
            OperationId = entry.OperationId
            ActorId = entry.ActorId
            RecordedAt = entry.RecordedAt
            CreationReference = None
            PayloadCommitment = entry.PayloadCommitment
            Subject = RecordedTarget.Authority { TargetActorId = target; Act = kind }
        }

    let append expected state entry =
        LedgerVerification.append
            installation
            expected
            state
            (LedgerEntries.stored entry)
            (metadata entry)

    let rootPosition = LedgerEntries.position entries[0].Sequence entries[0].EntryHash

    let root =
        append (Some rootPosition) (LedgerVerification.initial (Some rootPosition)) entries[0]
        |> accepted

    Expect.isEmpty root.Accepted "An intermediate effect is not an accepted operation"
    Expect.isError (LedgerVerification.complete None root) "A truncated fold cannot qualify"

    for forged in
        [
            { entries[1] with
                Effects = LedgerEffects.authority AuthorityKind.ProvisionOwner 2 0
            }
            { entries[1] with
                PayloadNonce = entries[0].PayloadNonce
            }
            { entries[1] with
                OperationId = CaseFixtures.operationId (Guid.NewGuid())
            }
        ] do
        match append None root forged with
        | Error divergence ->
            Expect.equal
                divergence.Finding
                VerificationFinding.OperationEffectGroup
                "Group boundary rejection precedes payload interpretation"
        | Ok _ -> failtest "Forged effect was accepted"

    let complete = append (Some rootPosition) root entries[1] |> accepted

    Expect.equal
        (Map.tryFind operation complete.Accepted)
        (Some target)
        "Only a complete group becomes accepted"

    Expect.isError
        (LedgerVerification.complete (Some rootPosition) complete)
        "Intermediate checkpoints cannot prove an operation"

    let tailPosition = LedgerEntries.position entries[1].Sequence entries[1].EntryHash

    Expect.equal
        (LedgerVerification.complete None complete |> accepted)
        tailPosition
        "Fold completes at final effect"

    let stored =
        entries |> List.map LedgerEntries.stored |> StoredOperation.create |> accepted

    let validated = LedgerEntries.validateOperation installation stored |> accepted
    let authority = complete.Actors[target]

    let view = AuthorityView.ofActor (Some authority)

    let receipt =
        LedgerEntries.receiptOperation
            (Inputs.workBasis)
            principal
            view
            (Some(Actor.currentReference authority.Actor))
            (ReceiptCurrent.Authority(Some(Actor.currentReference authority.Actor)))
            validated
        |> accepted

    Expect.equal
        receipt.Position
        tailPosition
        "Observation/replay receipt has the same complete proof position"

    Expect.isSome
        (LedgerEntries.identityBytesOperation validated)
        "Every live effect participates in exact replay"

[<Tests>]
let tests =
    testList
        "ledger operation grouping"
        [
            testCase "complete metadata and erasure controls" metadataControls
            testCase
                "fold acceptance checkpoints and receipts require the complete group"
                foldCompletion
            testCase "independent nonce calls chain and transient identity framing" nonceAndChain
            testCaseAsync "atomic memory append rollback and completeness" (rollback ())
        ]
