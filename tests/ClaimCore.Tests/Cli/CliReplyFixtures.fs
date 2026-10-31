module ClaimCore.Tests.CliReplyFixtures

open ClaimCore.TestSupport

open System
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

let id = Guid.Parse "10000000-0000-4000-8000-000000000001"

let authorityRequest =
    {
        OperationId = id
        Target =
            {
                Kind = PrincipalKind.Human
                Issuer = "https://issuer.example/"
                Subject = "synthetic"
            }
        Act = AuthorityAct.Register
    }

let snapshot =
    let fields: CaseFields =
        {
            CaseReference = CaseFixtures.reference "x"
            Status = CaseStatus.Opened
            Registration =
                CaseFixtures.registration (
                    ClaimCore.TestSupport.Inputs.registrationWith
                        [
                            BusinessField.ClaimantName, ("x")
                            BusinessField.InsurerName, ("x")
                            BusinessField.ClaimedAmount, ("0")
                        ]
                        ((Inputs.registrationOn "2026-01-01" "2026-01-01"))
                )
            Progress = ClaimCore.TestSupport.CaseFixtures.progress (None) (None)
        }

    {
        Reference = CaseFixtures.reference "x"
        Revision = CaseFixtures.revision 1L
        State = CaseState.Active(fields)
    }

let receipt =
    {
        OperationId = (CaseFixtures.operationId (id))
        Position =
            {
                Sequence = CaseFixtures.sequence 1L
                EntryHash =
                    (String.replicate 64 "0"
                     |> System.Convert.FromHexString
                     |> EntryHash.ofBytes
                     |> Option.get)
            }
        Subject =
            ReceiptSubject.Case
                {
                    Reference = CaseFixtures.reference "x"
                    Revision = CaseFixtures.revision 1L
                    Act = CaseActKind.Open
                    Snapshot = SnapshotDisclosure.Withheld
                }
        RecordedAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = (CaseFixtures.actorId (id))
    }
