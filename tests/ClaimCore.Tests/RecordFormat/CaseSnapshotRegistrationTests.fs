module ClaimCore.Tests.CaseSnapshotRegistrationTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCase "registration decoding preserves independently authored dates" (fun () ->
        let reference =
            ClaimCore.TestSupport.DomainInputs.reference "CC-1466-7QJR-WW86-MP91-73"

        let id =
            OperationId.create (Guid.Parse "10000000-0000-7000-8000-000000000001")
            |> Option.get

        let snapshot =
            {
                CaseSnapshot.CaseId = CaseId.ofOperationId id
                Reference = reference
                Revision = Revision.first
                State =
                    CaseState.Active
                        {
                            CaseReference = reference
                            Handling = Handling.Open
                            Registration =
                                DomainInputs.admittedRegistration DomainInputs.registration
                            Finance = FinancialPosition.undecided
                        }
            }

        let bytes = CaseRecord.encodeSnapshot snapshot
        Expect.isOk (CaseRecord.decodeSnapshot bytes) "Valid permanent snapshot control"

        let reversed =
            Encoding.UTF8.GetString(bytes).Replace("2026-08-01", "2026-08-04")
            |> Encoding.UTF8.GetBytes

        Expect.isOk
            (CaseRecord.decodeSnapshot reversed)
            "The two dates remain independently representable")
