module ClaimCore.IntegrationTests.StorageDateTests

open System
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.IntegrationTests.Fixtures

let private persistDateExtrema (value: string) =
    use database = store ()
    let service = database :> IClaimStore

    let businessClock =
        { new IBusinessDate with
            member _.Today() = DateOnly.MaxValue
        }

    let request =
        { newRequest () with
            Command =
                Command.Open
                    { registration with
                        IncidentDate = value
                        IncidentNotificationDate = value
                    }
        }

    Service.executeAsync service businessClock request
    |> await
    |> accepted
    |> ignore

    let decision =
        Command.Decide
            {
                PaymentDecisionDate = value
                PayableAmount = "1.2345"
                PayableCurrency = "USD"
            }

    Service.executeAsync service businessClock (next request 1L decision)
    |> await
    |> accepted
    |> ignore

    Service.executeAsync service businessClock (next request 2L (Command.RecordPayment value))
    |> await
    |> accepted
    |> ignore

    let fields =
        service.Get(request.CaseReference)
        |> await
        |> accepted
        |> Option.defaultWith (fun () -> failtest "Expected the stored case.")
        |> Claim.view
        |> fun view -> view.Fields

    request.CaseReference, fields

let private expectFiniteDateStorage reference =
    use connection = new NpgsqlConnection(appConnection ())
    connection.Open()

    use verify =
        new NpgsqlCommand(
            "SELECT isfinite(incident_date) AND isfinite(incident_notification_date) AND isfinite(payment_decision_date) AND isfinite(payment_date) FROM claimcore.cases WHERE case_reference = @reference",
            connection
        )

    verify.Parameters.AddWithValue("reference", reference) |> ignore
    Expect.equal (verify.ExecuteScalar() :?> bool) true "No infinity sentinel was persisted"

let private dateExtrema (value: string) =
    let reference, fields = persistDateExtrema value
    Expect.isTrue (fields.IncidentDate = value) "Incident remains finite"
    Expect.isTrue (fields.IncidentNotificationDate = value) "FNOL remains finite"
    Expect.isTrue (fields.PaymentDecisionDate = Some value) "Decision remains finite"
    Expect.isTrue (fields.PaymentDate = Some value) "Payment remains finite"
    expectFiniteDateStorage reference

let tests =
    testList
        "finite date storage"
        [
            testCase "minimum supported date is not negative infinity" (fun () ->
                dateExtrema "0001-01-01")
            testCase "maximum supported date is not positive infinity" (fun () ->
                dateExtrema "9999-12-31")
        ]
