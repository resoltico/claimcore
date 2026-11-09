module ClaimCore.Tests.RegistrationIdentifiersTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.TestSupport

let private required value =
    value |> Result.defaultWith (fun error -> failtestf "%A" error)

let private today = DateOnly(2026, 9, 7)

let private run number revision act previous =
    Case.parse
        {
            OperationId = Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)
            CaseReference = "IDENTIFIERS"
            ExpectedRevision = revision
            Act = act
        }
    |> Result.mapError CaseDecisionError.Refused
    |> Result.bind (fun command ->
        Case.decide today CasePaymentContext.NoneRequired command previous)
    |> required
    |> _.After
    |> Case.ofAccepted
    |> required

let private registration record =
    (Case.snapshot record).State |> CaseState.fields |> Option.get |> _.Registration

let private patch members =
    RegistrationUpdateInput.create members |> required

let private facts =
    Inputs.registrationWith
        [
            BusinessField.PolicyNumber, "LV / Polise 42"
            BusinessField.InsurerClaimReference, "ملف / 17"
        ]
        Inputs.registration

[<Tests>]
let tests =
    testList
        "declared registration identifiers"
        [
            testCase
                "optional identifiers open amend correct and erase through ordinary field inputs"
                (fun () ->
                    let absent = run 1 0L (CaseAct.Open Inputs.registration) None

                    Expect.isNone
                        (Registration.policyNumber (registration absent))
                        "Omitted policy is absent"

                    let opened = run 2 0L (CaseAct.Open facts) None

                    Expect.equal
                        (Registration.policyNumber (registration opened)
                         |> Option.map Identifier.format)
                        (Some "LV / Polise 42")
                        "Authored policy"

                    Expect.equal
                        (Registration.insurerClaimReference (registration opened)
                         |> Option.map Identifier.format)
                        (Some "ملف / 17")
                        "Unicode insurer reference"

                    let amended =
                        run
                            3
                            1L
                            (CaseAct.AmendRegistration(
                                patch [ BusinessField.PolicyNumber, Some "Policy 43" ]
                            ))
                            (Some opened)

                    Expect.equal
                        (Registration.policyNumber (registration amended)
                         |> Option.map Identifier.format)
                        (Some "Policy 43")
                        "Only selected fact changes"

                    Expect.equal
                        (Registration.insurerClaimReference (registration amended)
                         |> Option.map Identifier.format)
                        (Some "ملف / 17")
                        "Omitted fact stays"

                    let decided = run 4 2L (CaseAct.Decide Inputs.decision) (Some amended)

                    let corrected =
                        run
                            5
                            3L
                            (CaseAct.Correct
                                {
                                    Registration =
                                        Amendment.Update(
                                            patch [ BusinessField.PolicyNumber, None ]
                                        )
                                    Decision = Change.Keep
                                })
                            (Some decided)

                    Expect.isNone
                        (Registration.policyNumber (registration corrected))
                        "Explicit optional clear"

                    Expect.equal
                        (Registration.insurerClaimReference (registration corrected)
                         |> Option.map Identifier.format)
                        (Some "ملف / 17")
                        "Correction preserves the other identifier"

                    let erased = run 6 4L (CaseAct.Bare BareAct.Erase) (Some corrected)

                    Expect.equal
                        (Case.snapshot erased).State
                        CaseState.Erased
                        "Erasure retains no registration facts")
            testCase
                "identifier admission counts Unicode scalars and refuses whitespace controls and excess"
                (fun () ->
                    let admit value =
                        Identifier.parse value |> Result.bind Identifier.admit

                    for value in
                        [ String.replicate 80 "a"; String.replicate 80 "😀"; "Police / رقم 7" ] do
                        Expect.equal
                            (admit value |> Result.map Identifier.format)
                            (Ok value)
                            "Valid exact authored identifier"

                    for value in
                        [
                            ""
                            " leading"
                            "trailing "
                            "line\nbreak"
                            "tab\tvalue"
                            String.replicate 81 "a"
                            String.replicate 81 "😀"
                        ] do
                        Expect.isError (admit value) "Invalid identifier is refused")
            testCase
                "canonical codecs use optional identifiers and flat atomic money pairs"
                (fun () ->
                    let encoded = RequestCodecs.registration.Write facts

                    Expect.equal
                        (JsonValue.tryMember "policyNumber" encoded)
                        (Some(JsonValue.String "LV / Polise 42"))
                        "Declaration reaches the create codec"

                    let decoded =
                        Codec.decode
                            4096
                            RequestCodecs.registrationUpdate
                            (Encoding.UTF8.GetBytes
                                "{\"policyNumber\":null,\"claimedAmount\":\"12.50\",\"claimedCurrency\":\"EUR\"}")
                        |> required

                    Expect.equal
                        (RegistrationUpdateInput.tryValue BusinessField.ClaimedAmount decoded)
                        (Some "12.50")
                        "Flat amount remains authored"

                    let parsed = RegistrationUpdateInput.parse decoded |> required

                    Expect.equal
                        (RegistrationUpdate.changes parsed
                         |> List.tryFind (fst >> (=) BusinessField.PolicyNumber))
                        (Some(BusinessField.PolicyNumber, None))
                        "Clear survives parsing"

                    for json in
                        [
                            "{\"claimedAmount\":\"12.50\"}"
                            "{\"claimedCurrency\":\"EUR\"}"
                            "{\"claimed\":{\"amount\":\"12.50\",\"currency\":\"EUR\"}}"
                        ] do
                        Expect.isError
                            (Codec.decode
                                4096
                                RequestCodecs.registrationUpdate
                                (Encoding.UTF8.GetBytes json))
                            "Incomplete or nested money refuses"

                    Expect.isOk
                        (Codec.decode
                            4096
                            RequestCodecs.decisionUpdate
                            (Encoding.UTF8.GetBytes
                                "{\"payableAmount\":\"12.50\",\"payableCurrency\":\"EUR\"}"))
                        "Decision money uses the same flat shape"

                    Expect.isError
                        (Codec.decode
                            4096
                            RequestCodecs.decisionUpdate
                            (Encoding.UTF8.GetBytes "{\"payableAmount\":\"12.50\"}"))
                        "Decision money cannot be half asserted")
        ]
