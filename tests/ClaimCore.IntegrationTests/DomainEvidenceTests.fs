module ClaimCore.IntegrationTests.DomainEvidenceTests

open System
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.IntegrationTests.Fixtures

let private storedRule reference =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT rule_revision FROM claimcore.case_changes WHERE case_reference=@reference ORDER BY revision DESC LIMIT 1",
            connection
        )

    command.Parameters.AddWithValue("reference", reference) |> ignore
    command.ExecuteScalar() :?> int16

let private rejectUpdate sql reference =
    try
        runSql (adminConnection ()) sql reference
        failtest "The fresh baseline must reject unsupported accepted evidence."
    with :? PostgresException as error ->
        Expect.equal error.SqlState PostgresErrorCodes.CheckViolation "Structural refusal"

let private rollbackClock =
    { new IBusinessTime with
        member _.Capture() =
            {
                ObservedUtcInstant = DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero)
                EffectiveBusinessDate = DateOnly(2026, 7, 31)
                TimeZoneId = "Etc/UTC"
            }
    }

let private amendment =
    testCase
        "[CC-DOM-002] clock rollback amendments persist and retain the current rule revision"
        (fun () ->
            use database = store ()
            let service = database :> IClaimStore
            let initial = newRequest ()
            Service.executeAsync service clock initial |> await |> accepted |> ignore

            let changed =
                Command.AmendRegistration
                    { registration with
                        ClaimantName = "Synthetic rollback party"
                    }

            let amended = next initial 1L changed

            let receipt =
                Service.executeAsync service rollbackClock amended |> await |> accepted

            Expect.equal (Claim.view receipt.Case).Version 2L "Accepted revision"

            Expect.equal
                (storedRule initial.CaseReference)
                (int16 DomainRules.version)
                "Actual SQL evidence matches executable rules"

            let replay = Service.executeAsync service rollbackClock amended |> await |> accepted
            Expect.isTrue replay.Replayed "Exact replay retains acceptance after rollback"

            Expect.isTrue
                (Claim.view replay.Case = Claim.view receipt.Case)
                "Same historical receipt")

let private constraints =
    testCase
        "[CC-DOM-001] fresh SQL refuses terminal business revisions and unsupported rule evidence"
        (fun () ->
            use database = store ()
            let service = database :> IClaimStore
            let initial = newRequest ()
            Service.executeAsync service clock initial |> await |> accepted |> ignore

            for table in [ "cases"; "case_changes" ] do
                rejectUpdate
                    ($"UPDATE claimcore.{table} SET revision=9223372036854775807 WHERE case_reference=@reference")
                    initial.CaseReference

            rejectUpdate
                "UPDATE claimcore.case_changes SET rule_revision=1 WHERE case_reference=@reference"
                initial.CaseReference

            let replay = Service.executeAsync service clock initial |> await |> accepted
            Expect.isTrue replay.Replayed "Rejected SQL did not alter accepted evidence"
            Expect.equal (Claim.view replay.Case).Version 1L "Original revision")

let tests = testList "domain durable evidence" [ amendment; constraints ]
