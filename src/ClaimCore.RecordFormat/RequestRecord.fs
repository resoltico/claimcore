namespace ClaimCore.RecordFormat

open System.Text.Json
open ClaimCore.Domain

/// Case-request parsing and canonical encoding.
module RequestRecord =
    let private registrationGroup (value: JsonElement) =
        let path = "$.act.groups.registration"

        match Json.text path "mode" value with
        | "KEEP" ->
            Json.properties path [ "mode" ] value
            RegistrationCorrection.Keep
        | "REPLACE" ->
            Json.properties path [ "mode"; "values" ] value

            RegistrationCorrection.Replace(
                Json.registration (path + ".values") (Json.required path "values" value)
            )
        | _ -> Json.reject (path + ".mode") "Use KEEP or REPLACE."

    let private decisionGroup (value: JsonElement) =
        let path = "$.act.groups.decision"

        match Json.text path "mode" value with
        | "KEEP" ->
            Json.properties path [ "mode" ] value
            DecisionCorrection.Keep
        | "REPLACE" ->
            Json.properties path [ "mode"; "values" ] value

            DecisionCorrection.Replace(
                Json.decision (path + ".values") (Json.required path "values" value)
            )
        | "CLEAR" ->
            Json.properties path [ "mode" ] value
            DecisionCorrection.Clear
        | _ -> Json.reject (path + ".mode") "Use KEEP, REPLACE or CLEAR."

    let private paymentValues path element =
        Json.properties path [ "paymentDate" ] element
        Json.text path "paymentDate" element

    let private paymentGroup (value: JsonElement) =
        let path = "$.act.groups.payment"

        match Json.text path "mode" value with
        | "KEEP" ->
            Json.properties path [ "mode" ] value
            PaymentCorrection.Keep
        | "REPLACE" ->
            Json.properties path [ "mode"; "values" ] value

            PaymentCorrection.Replace(
                paymentValues (path + ".values") (Json.required path "values" value)
            )
        | "CLEAR" ->
            Json.properties path [ "mode" ] value
            PaymentCorrection.Clear
        | _ -> Json.reject (path + ".mode") "Use KEEP, REPLACE or CLEAR."

    let private correction element =
        let path = "$.act.groups"
        Json.properties path [ "registration"; "decision"; "payment" ] element

        {
            Registration = registrationGroup (Json.required path "registration" element)
            Decision = decisionGroup (Json.required path "decision" element)
            Payment = paymentGroup (Json.required path "payment" element)
        }

    let private business kind element =
        let path = "$.act"

        let shape fields =
            Json.properties path ("kind" :: fields) element

        let values read =
            shape [ "values" ]
            read (path + ".values") (Json.required path "values" element)

        match kind with
        | "OPEN" -> BusinessCommand.Open(values Json.registration)
        | "AMEND_REGISTRATION" -> BusinessCommand.AmendRegistration(values Json.registration)
        | "CORRECT_CASE" ->
            shape [ "groups" ]
            BusinessCommand.CorrectCase(correction (Json.required path "groups" element))
        | "DECIDE" -> BusinessCommand.Decide(values Json.decision)
        | "RECORD_PAYMENT" -> BusinessCommand.RecordPayment(values paymentValues)
        | "WITHDRAW_DECISION" ->
            shape []
            BusinessCommand.WithdrawDecision
        | "CLEAR_PAYMENT" ->
            shape []
            BusinessCommand.ClearPayment
        | "CLOSE" ->
            shape []
            BusinessCommand.Close
        | "REOPEN" ->
            shape []
            BusinessCommand.Reopen
        | _ -> Json.reject (path + ".kind") "Unknown case act."

    let private act element =
        let path = "$.act"

        match Json.text path "kind" element with
        | "VOID" ->
            Json.properties path [ "kind"; "reason" ] element
            CaseAct.Stewardship(StewardshipAct.Void(Json.text path "reason" element))
        | "REINSTATE" ->
            Json.properties path [ "kind"; "reason" ] element
            CaseAct.Stewardship(StewardshipAct.Reinstate(Json.text path "reason" element))
        | "ERASE" ->
            Json.properties path [ "kind" ] element
            CaseAct.Stewardship StewardshipAct.Erase
        | kind -> CaseAct.Business(business kind element)

    let private read durable root =
        let members = [ "operationId"; "caseReference"; "expectedRevision"; "act" ]
        Json.properties "$" (if durable then "format" :: members else members) root

        let request =
            {
                OperationId = Json.guid "$" "operationId" root
                CaseReference = Json.text "$" "caseReference" root
                ExpectedRevision = Json.integer "$" "expectedRevision" root
                Act = act (Json.required "$" "act" root)
            }

        match Case.validateRequest request with
        | Ok() -> request
        | Error(CaseRefusal.InvalidInput(field, _)) ->
            Json.reject ("$." + InputTargets.token field) "Invalid case-request format."
        | Error _ -> Json.reject "$.act" "Invalid case-request format."

    // Append decoders when supporting another durable format; never reinterpret existing bytes.
    let decode maxBytes bytes =
        Json.bounded maxBytes bytes (Json.dispatch [ int64 RecordVersions.CaseRequest, read true ])

    let decodeWire maxBytes bytes =
        Json.bounded maxBytes bytes (read false)

    let private mode name = "mode", CanonicalValue.String name

    let private group name value =
        CanonicalValue.Object [ mode name; "values", value ]

    let private correctionValue (correction: CaseCorrection) =
        let registration =
            match correction.Registration with
            | RegistrationCorrection.Keep -> CanonicalValue.Object [ mode "KEEP" ]
            | RegistrationCorrection.Replace value -> group "REPLACE" (Json.registrationValue value)

        let decision =
            match correction.Decision with
            | DecisionCorrection.Keep -> CanonicalValue.Object [ mode "KEEP" ]
            | DecisionCorrection.Clear -> CanonicalValue.Object [ mode "CLEAR" ]
            | DecisionCorrection.Replace value -> group "REPLACE" (Json.decisionValue value)

        let payment =
            match correction.Payment with
            | PaymentCorrection.Keep -> CanonicalValue.Object [ mode "KEEP" ]
            | PaymentCorrection.Clear -> CanonicalValue.Object [ mode "CLEAR" ]
            | PaymentCorrection.Replace date ->
                group
                    "REPLACE"
                    (CanonicalValue.Object [ "paymentDate", CanonicalValue.String date ])

        CanonicalValue.Object
            [ "registration", registration; "decision", decision; "payment", payment ]

    let private actValue act =
        let payload =
            match act with
            | CaseAct.Business(BusinessCommand.Open value)
            | CaseAct.Business(BusinessCommand.AmendRegistration value) ->
                [ "values", Json.registrationValue value ]
            | CaseAct.Business(BusinessCommand.Decide value) ->
                [ "values", Json.decisionValue value ]
            | CaseAct.Business(BusinessCommand.RecordPayment date) ->
                [
                    "values", CanonicalValue.Object [ "paymentDate", CanonicalValue.String date ]
                ]
            | CaseAct.Business(BusinessCommand.CorrectCase value) ->
                [ "groups", correctionValue value ]
            | CaseAct.Stewardship(StewardshipAct.Void reason)
            | CaseAct.Stewardship(StewardshipAct.Reinstate reason) ->
                [ "reason", CanonicalValue.String reason ]
            | _ -> []

        CanonicalValue.Object(("kind", CanonicalValue.String(CaseActs.name act)) :: payload)

    let private value durable (request: CaseRequest) =
        match Case.validateRequest request with
        | Error _ -> invalidArg (nameof request) "Invalid case-request format."
        | Ok() ->
            let fields =
                [
                    "operationId", CanonicalValue.String(request.OperationId.ToString("D"))
                    "caseReference", CanonicalValue.String request.CaseReference
                    "expectedRevision", CanonicalValue.Integer request.ExpectedRevision
                    "act", actValue request.Act
                ]

            CanonicalValue.Object(
                if durable then
                    ("format", CanonicalValue.Integer(int64 RecordVersions.CaseRequest)) :: fields
                else
                    fields
            )

    let encode request =
        value true request |> CanonicalJson.encode

    let encodeWire request =
        value false request |> CanonicalJson.encode
