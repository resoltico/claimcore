module ClaimCore.Tests.ContractScenarioInputs

open ClaimCore.Cli

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

open System
open System.Text
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Tests.Fixtures


let internal bytes (value: string) = Encoding.UTF8.GetBytes value
let internal id = Guid.Parse "20000000-0000-7000-8000-000000000001"
let internal digest = String.replicate 64 "a"

let internal position: ClaimCore.Domain.LedgerPosition =
    {
        Sequence = CaseFixtures.sequence 1L
        EntryHash = (Convert.FromHexString digest |> EntryHash.ofBytes |> Option.get)
    }

let internal receipt: ClaimCore.Application.Contracts.OperationReceipt =
    {
        OperationId = (CaseFixtures.operationId (id))
        Position = position
        Subject =
            ClaimCore.Application.Contracts.ReceiptSubject.Case
                {
                    CaseId =
                        CaseFixtures.caseId (
                            System.Guid.Parse "20000000-0000-7000-8000-000000000001"
                        )
                    Reference = CaseFixtures.reference "UNIT-001"
                    Revision = CaseFixtures.revision 1L
                    Act = CaseActKind.Open
                    Snapshot =
                        ClaimCore.Application.Contracts.SnapshotDisclosure.Disclosed(
                            {
                                State =
                                    LiveCase.ofState (Case.snapshot (opened ())).State |> Option.get
                                Payment = None
                            }
                        )
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        Basis =
            {
                ObservedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
                BusinessDate = DateOnly(2026, 10, 13)
                TimeZoneId = "Etc/UTC"
            }
        RecordedBy = ClaimCore.TestSupport.Inputs.actorReference ((CaseFixtures.actorId (id)))
    }

let internal authority =
    let effects =
        [
            {
                AuthorityReceiptEffect.Act = AuthorityKind.Register
                Position = receipt.Position
            }
            {
                AuthorityReceiptEffect.Act = AuthorityKind.SetProfile
                Position =
                    LedgerPosition.create
                        (Sequence.create 2L |> Option.get)
                        receipt.Position.EntryHash
            }
        ]

    { receipt with
        Position = (List.last effects).Position
        Subject =
            ReceiptSubject.Authority
                {
                    TargetActor = AuthorityTargetDisclosure.Withheld(CaseFixtures.actorId id)
                    Act = AuthorityKind.Register
                    Effects = effects
                }
    }

let internal admitted codec value =
    let encoded = Codec.encode codec value

    Expect.equal
        (Codec.decode Limits.MaximumResponseBytes codec encoded)
        (Ok value)
        "Typed reply round trips"

    use document = JsonDocument.Parse(encoded.AsMemory())
    document.RootElement.Clone()

let internal caseRequest values : ClaimCore.Domain.UnvalidatedCaseRequest =
    {
        OperationId = id
        CaseReference = "UNIT-001"
        ExpectedRevision = 0L
        Act = ClaimCore.Domain.AuthoredCaseAct.Open values
    }

let internal artifact name =
    JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "contracts", name)).AsMemory()
    )
