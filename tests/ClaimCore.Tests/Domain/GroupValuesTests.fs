module ClaimCore.Tests.GroupValuesTests

open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testList
        "complete typed business groups"
        [
            testCase "registration constructors reject reversed chronology" (fun () ->
                let original = Inputs.admittedRegistration Inputs.registration
                let values = Registration.values original

                let set field date =
                    values
                    |> List.map (fun (key, value) ->
                        key,
                        if key = field then
                            Admitted.Date(CaseFixtures.date date)
                        else
                            value)

                Expect.isError
                    (Registration.create (set BusinessField.IncidentDate "2026-08-04"))
                    "Incident after notification is permanently invalid"

                Expect.isOk
                    (Registration.create (set BusinessField.IncidentDate "2026-08-03"))
                    "Same-day chronology is valid"

            )
            testCase "authored constructors reject missing duplicate and foreign fields" (fun () ->
                let values = RegistrationInput.values Inputs.registration

                Expect.isError
                    (RegistrationInput.create (
                        values |> List.filter (fst >> (<>) BusinessField.IncidentDate)
                    ))
                    "The required incident date cannot be omitted"

                Expect.isError
                    (RegistrationInput.create (
                        values @ [ BusinessField.IncidentDate, "2026-08-01" ]
                    ))
                    "A field cannot occur twice"

                Expect.isError
                    (DecisionInput.create values)
                    "Registration values cannot construct a decision"

                Expect.isOk
                    (RegistrationInput.create values)
                    "A complete authored group remains constructible")
            testCase "admitted constructors enforce value kinds and completeness" (fun () ->
                let values = Registration.values (Inputs.admittedRegistration Inputs.registration)

                let wrongKind =
                    values
                    |> List.map (fun (field, value) ->
                        field,
                        if field = BusinessField.IncidentDate then
                            Admitted.Name(Inputs.name "Synthetic")
                        else
                            value)

                Expect.isError (Registration.create wrongKind) "A name cannot occupy a date field"

                Expect.isError
                    (Registration.create (
                        values |> List.filter (fst >> (<>) BusinessField.ClaimantName)
                    ))
                    "A required name cannot be omitted"

                Expect.isOk
                    (Registration.create values)
                    "Complete typed values remain constructible")
        ]
