module ClaimCore.Tests.DraftTests

open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private values kind =
    let fields = (Claim.view (paid ())).Fields |> FieldDefinitions.values |> Map.ofList

    CommandDefinitions.inputFields kind
    |> List.map (fun name -> name, (Map.find name fields |> Option.defaultValue ""))

let private draft kind =
    {
        OperationId = (request 0L Command.Close).OperationId
        CaseReference = "UNIT-001"
        ExpectedVersion = 1L
        Kind = kind
        Values = values kind
    }

let private definitionInventoryTests =
    testList
        "form definitions"
        [
            testCase "all eight forms cover the canonical commands, in canonical order" (fun () ->
                Expect.equal
                    (CommandDefinitions.all |> List.map (fun form -> form.Kind))
                    CommandKinds.all
                    "One form per command"

                for form in CommandDefinitions.all do
                    let inputFields = form.Inputs |> List.map (fun input -> input.FieldName)

                    let names =
                        FieldDefinitions.all |> List.map (fun field -> field.Name) |> Set.ofList

                    Expect.isTrue
                        (Set.isSubset (Set.ofList inputFields) names)
                        "No extra business fields"

                    Expect.equal
                        inputFields.Length
                        (Set.ofList inputFields).Count
                        "No duplicate prompts")
        ]

let private bindingTests =
    testList
        "form binding"
        [
            testCase
                "each form binds the intended command without a renderer-specific recipe"
                (fun () ->
                    for kind in CommandKinds.all do
                        let bound = Drafts.bind (draft kind) |> accepted
                        Expect.equal (Commands.kind bound.Command) kind "Command family"
                        Expect.equal bound.ExpectedVersion 1L "Caller revision is not rewritten"
                        let supplied = Map.ofList (values kind)

                        let same name actual =
                            Expect.equal
                                actual
                                (Map.find name supplied)
                                ("Exact raw binding: " + name)

                        match bound.Command with
                        | Command.Open input
                        | Command.AmendRegistration input ->
                            same "incidentDate" input.IncidentDate
                            same "incidentNotificationDate" input.IncidentNotificationDate
                            same "incidentCountry" input.IncidentCountry
                            same "claimantName" input.ClaimantName
                            same "insurerName" input.InsurerName
                            same "claimedAmount" input.ClaimedAmount
                            same "claimedCurrency" input.ClaimedCurrency
                        | Command.Decide input ->
                            same "paymentDecisionDate" input.PaymentDecisionDate
                            same "payableAmount" input.PayableAmount
                            same "payableCurrency" input.PayableCurrency
                        | Command.RecordPayment date -> same "paymentDate" date
                        | Command.WithdrawDecision
                        | Command.ClearPayment
                        | Command.Close
                        | Command.Reopen -> ())
        ]

let private validationTests =
    testList
        "draft validation"
        [
            testCase "missing extra and duplicate form values are refused" (fun () ->
                for kind in CommandKinds.all do
                    let original = draft kind

                    Expect.isError
                        (Drafts.bind
                            { original with
                                Values = original.Values @ [ "notes", "extra" ]
                            })
                        "Extra input"

                    match original.Values with
                    | head :: rest ->
                        Expect.isError
                            (Drafts.bind { original with Values = rest })
                            "Missing input"

                        Expect.isError
                            (Drafts.bind
                                { original with
                                    Values = head :: original.Values
                                })
                            "Duplicate input"
                    | [] -> ())
            testCase "form binding does not trim, uppercase or round business input" (fun () ->
                let original = draft CommandKind.Decide

                for amount in [ " 7"; "-7"; "7.00001"; "7e2" ] do
                    let edited =
                        original.Values
                        |> List.map (fun (name, value) ->
                            name, if name = "payableAmount" then amount else value)

                    Expect.isError
                        (Drafts.bind { original with Values = edited })
                        "Core validation"

                let edited =
                    original.Values
                    |> List.map (fun (name, value) ->
                        name, if name = "payableCurrency" then "eur" else value)

                Expect.isError
                    (Drafts.bind { original with Values = edited })
                    "No case normalization")
        ]

let private projectionTests =
    testList
        "field projection"
        [
            testCase
                "field projection is exactly the thirteen native fields with absence preserved"
                (fun () ->
                    let fields = (Claim.view (opened ())).Fields
                    let actual = FieldDefinitions.values fields

                    Expect.equal
                        (List.map fst actual)
                        (FieldDefinitions.all |> List.map (fun field -> field.Name))
                        "Order"

                    Expect.equal
                        (actual |> Map.ofList |> Map.find "paymentDate")
                        None
                        "Absent is not zero/date"

                    Expect.equal
                        (actual |> Map.ofList |> Map.find "status")
                        (Some "OPENED")
                        "Core status")
        ]

let tests =
    testList
        "native form contract"
        [ definitionInventoryTests; bindingTests; validationTests; projectionTests ]
