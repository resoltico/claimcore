module ClaimCore.Tests.CliReplyFixtures

open ClaimCore.TestSupport

open System
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

let id = Guid.Parse "10000000-0000-7000-8000-000000000001"

let peopleRequest =
    Submissions.create
        "people.register"
        id
        (Binding.PeopleNew Revision.zero)
        (Submissions.principal (
            PrincipalKey.human "https://issuer.example/" "synthetic"
            |> Result.toOption
            |> Option.get
         )
         |> Map.fold
             (fun values key value -> Map.add key value values)
             (Submissions.profile DomainInputs.actorProfile))

let snapshot =
    let fields: CaseFields =
        {
            CaseReference = CaseFixtures.reference "x"
            Handling = Handling.Open
            Registration =
                CaseFixtures.registration (
                    ClaimCore.TestSupport.DomainInputs.registrationWith
                        [
                            BusinessField.PolicyNumber, ("x")
                            BusinessField.InsurerClaimReference, ("x")
                            BusinessField.ClaimedAmount, ("0")
                        ]
                        ((DomainInputs.registrationOn "2026-01-01" "2026-01-01"))
                )
            Finance = FinancialPosition.undecided
        }

    {
        CaseId = CaseFixtures.caseId id
        Reference = CaseFixtures.reference "x"
        Revision = CaseFixtures.revision 1L
        State = CaseState.Active(fields)
    }

let receipt: OperationReceipt =
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
                    CaseId = CaseId.ofOperationId (CaseFixtures.operationId id)
                    Reference = CaseFixtures.reference "x"
                    Revision = CaseFixtures.revision 1L
                    Act = (TaskId.ofLiteral "case.open")
                    Snapshot = SnapshotDisclosure.Withheld
                }
        RecordedAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        Basis =
            {
                ObservedAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                BusinessDate = DateOnly(2026, 1, 1)
                TimeZoneId = "Etc/UTC"
            }
        RecordedBy =
            AuthorityRef.Actor(
                ClaimCore.TestSupport.DomainInputs.actorReference ((CaseFixtures.actorId (id)))
            )
    }
