module ClaimCore.Tests.CaseSnapshotChronologyTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCase "permanent snapshot decoding rejects reversed registration chronology" (fun () ->
        let reference = CaseReference.parse "CHRONOLOGY" |> Result.toOption |> Option.get

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
                            Status = CaseStatus.Opened
                            Registration = Inputs.admittedRegistration Inputs.registration
                            Finance = FinancialPosition.undecided
                        }
            }

        let bytes = CaseRecord.encodeSnapshot snapshot
        Expect.isOk (CaseRecord.decodeSnapshot bytes) "Valid permanent snapshot control"

        let reversed =
            Encoding.UTF8.GetString(bytes).Replace("2026-08-01", "2026-08-04")
            |> Encoding.UTF8.GetBytes

        Expect.isError (CaseRecord.decodeSnapshot reversed) "Permanent decode enforces chronology")
