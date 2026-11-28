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
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Tests.CaseBuilders


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
                    Reference = CaseFixtures.reference "CC-ZQ16-FT6R-54T8-E39K-67"
                    Revision = CaseFixtures.revision 1L
                    Act = (TaskId.ofLiteral "case.open")
                    Snapshot =
                        ClaimCore.Application.Contracts.SnapshotDisclosure.Disclosed(
                            {
                                State =
                                    LiveCase.ofState (familySnapshot (opened ())).State
                                    |> Option.get
                                Disposition = None
                                ReopenReason = None
                                RecordedFields = BusinessField.ofGroup RecordGroup.Registration
                                Payments = []
                                Balances = []
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
        RecordedBy =
            AuthorityRef.Actor(
                ClaimCore.TestSupport.DomainInputs.actorReference ((CaseFixtures.actorId (id)))
            )
    }

let internal people =
    let effects =
        [
            {
                PeopleReceiptEffect.Act = (TaskId.ofLiteral "people.register")
                Id = EffectId.create receipt.OperationId 0 |> Option.get
                Position = EffectPosition.create receipt.Position.Sequence 0 |> Option.get
            }
            {
                PeopleReceiptEffect.Act = (TaskId.ofLiteral "people.setProfile")
                Id = EffectId.create receipt.OperationId 1 |> Option.get
                Position = EffectPosition.create receipt.Position.Sequence 1 |> Option.get
            }
        ]

    { receipt with
        Subject =
            ReceiptSubject.People
                {
                    TargetActor = PeopleTargetDisclosure.Withheld(CaseFixtures.actorId id)
                    Act = (TaskId.ofLiteral "people.register")
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

let internal caseRequest values =
    Submissions.create
        "case.open"
        id
        (Binding.CaseNew Revision.zero)
        (Submissions.fields (RegistrationInput.values values))

let internal artifact name =
    JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "contracts", name)).AsMemory()
    )
