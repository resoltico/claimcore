module ClaimCore.Tests.DraftTests

open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private fieldValues =
    (Claim.view (paid ())).Fields |> FieldDefinitions.values |> Map.ofList

let private values fields =
    fields
    |> List.map (fun field -> field, (Map.find field fieldValues |> Option.defaultValue ""))

let private flatValues kind =
    match CommandDefinitions.flatInputFields kind with
    | Ok fields -> values (fields |> List.map _.FieldName)
    | Error message -> invalidOp message

let private flatDraft kind : CommandDraft =
    {
        OperationId = (request 0L Command.Close).OperationId
        CaseReference = "UNIT-001"
        ExpectedVersion = 1L
        Command = DraftCommand.Flat(kind, flatValues kind)
    }

let private correctionDraft registration decision payment : CommandDraft =
    {
        OperationId = (request 0L Command.Close).OperationId
        CaseReference = "UNIT-001"
        ExpectedVersion = 1L
        Command = DraftCommand.Correction(registration, decision, payment)
    }

let private replacement names =
    CorrectionDraftAction.Replace(values names)

let private defaultCorrection =
    correctionDraft
        (replacement
            [
                "incidentDate"
                "incidentNotificationDate"
                "incidentCountry"
                "claimantName"
                "insurerName"
                "claimedAmount"
                "claimedCurrency"
            ])
        (replacement [ "paymentDecisionDate"; "payableAmount"; "payableCurrency" ])
        (replacement [ "paymentDate" ])

let private draft kind =
    if kind = CommandKind.CorrectCase then
        defaultCorrection
    else
        flatDraft kind

let private definitionInventoryTests =
    testList
        "form definitions"
        [
            testCase "all nine forms cover the canonical commands in canonical order" (fun () ->
                Expect.equal
                    (CommandDefinitions.all |> List.map (fun form -> form.Kind))
                    CommandKinds.all
                    "One form per command"

                let names = FieldDefinitions.all |> List.map _.Name |> Set.ofList

                for form in CommandDefinitions.all do
                    let fields =
                        match form.Inputs with
                        | CommandInputShape.Fields values -> values
                        | CommandInputShape.CorrectionGroups groups ->
                            groups |> List.collect (fun group -> group.ReplaceFields)

                    let fieldNames = fields |> List.map _.FieldName

                    Expect.isTrue
                        (Set.isSubset (Set.ofList fieldNames) names)
                        "No extra business fields"

                    Expect.equal
                        fieldNames.Length
                        (Set.ofList fieldNames).Count
                        "No duplicate prompts")
        ]

let private bindingTests =
    testList
        "form binding"
        [
            testCase
                "each declared form binds its closed command without a renderer recipe"
                (fun () ->
                    for kind in CommandKinds.all do
                        let bound = Drafts.bind (draft kind) |> accepted
                        Expect.equal (Commands.kind bound.Command) kind "Command family"
                        Expect.equal bound.ExpectedVersion 1L "Caller revision is not rewritten")
            testCase "grouped correction binding retains each replacement scalar exactly" (fun () ->
                let bound = Drafts.bind defaultCorrection |> accepted

                match bound.Command with
                | Command.CorrectCase correction ->
                    match correction.Registration, correction.Decision, correction.Payment with
                    | RegistrationCorrection.Replace registration,
                      DecisionCorrection.Replace decision,
                      PaymentCorrection.Replace paymentDate ->
                        Expect.equal
                            registration.ClaimantName
                            fieldValues["claimantName"].Value
                            "Registration"

                        Expect.equal
                            decision.PayableAmount
                            fieldValues["payableAmount"].Value
                            "Decision"

                        Expect.equal paymentDate fieldValues["paymentDate"].Value "Payment"
                    | _ -> failtest "Expected complete grouped replacements."
                | _ -> failtest "Expected CORRECT_CASE.")
        ]

let private flatFormShapeValidation =
    testCase "flat forms reject missing extra and duplicate values" (fun () ->
        for kind in CommandKinds.all |> List.filter ((<>) CommandKind.CorrectCase) do
            let original = flatDraft kind

            match original.Command with
            | DraftCommand.Flat(_, fields) ->
                Expect.isError
                    (Drafts.bind
                        { original with
                            Command = DraftCommand.Flat(kind, fields @ [ "notes", "extra" ])
                        })
                    "Extra input"

                match fields with
                | head :: rest ->
                    Expect.isError
                        (Drafts.bind
                            { original with
                                Command = DraftCommand.Flat(kind, rest)
                            })
                        "Missing input"

                    Expect.isError
                        (Drafts.bind
                            { original with
                                Command = DraftCommand.Flat(kind, head :: fields)
                            })
                        "Duplicate input"
                | [] -> ()
            | DraftCommand.Correction _ -> failtest "Expected a flat command.")

let private correctionActionValidation =
    testCase "grouped correction requires complete replacements and one actual action" (fun () ->
        let incomplete =
            correctionDraft
                (CorrectionDraftAction.Replace [ "claimantName", "Synthetic" ])
                CorrectionDraftAction.Keep
                CorrectionDraftAction.Keep

        let allKeep =
            correctionDraft
                CorrectionDraftAction.Keep
                CorrectionDraftAction.Keep
                CorrectionDraftAction.Keep

        Expect.isError (Drafts.bind incomplete) "Replacement fields are complete"

        Expect.equal
            (Drafts.bind allKeep)
            (Error DomainError.CorrectionNoChanges)
            "All KEEP does not create an operation")

let private businessInputValidation =
    testCase "form binding does not trim uppercase or round business input" (fun () ->
        let original = flatDraft CommandKind.Decide

        match original.Command with
        | DraftCommand.Flat(_, supplied) ->
            for amount in [ " 7"; "-7"; "7.00001"; "7e2" ] do
                let edited =
                    supplied
                    |> List.map (fun (name, value) ->
                        name, if name = "payableAmount" then amount else value)

                Expect.isError
                    (Drafts.bind
                        { original with
                            Command = DraftCommand.Flat(CommandKind.Decide, edited)
                        })
                    "Core validation"

            let edited =
                supplied
                |> List.map (fun (name, value) ->
                    name, if name = "payableCurrency" then "eur" else value)

            Expect.isError
                (Drafts.bind
                    { original with
                        Command = DraftCommand.Flat(CommandKind.Decide, edited)
                    })
                "No case normalization"
        | DraftCommand.Correction _ -> failtest "Expected a flat command.")

let private validationTests =
    testList
        "draft validation"
        [ flatFormShapeValidation; correctionActionValidation; businessInputValidation ]

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
                        (FieldDefinitions.all |> List.map _.Name)
                        "Order"

                    Expect.equal
                        (actual |> Map.ofList |> Map.find "paymentDate")
                        None
                        "Absent is not zero"

                    Expect.equal
                        (actual |> Map.ofList |> Map.find "status")
                        (Some "OPENED")
                        "Core status")
        ]

let tests =
    testList
        "native form contract"
        [ definitionInventoryTests; bindingTests; validationTests; projectionTests ]
