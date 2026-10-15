namespace ClaimCore.RecordFormat

open ClaimCore.Domain

module CaseRecord =
    let private optionalText =
        function
        | Some text -> CanonicalValue.String text
        | None -> CanonicalValue.Null

    /// Exactly the thirteen business fields; technical state remains outside the object.
    let fieldsValue (fields: CaseFields) =
        CanonicalValue.Object
            [
                "incidentDate", CanonicalValue.String fields.IncidentDate
                "incidentNotificationDate", CanonicalValue.String fields.IncidentNotificationDate
                "incidentCountry", CanonicalValue.String fields.IncidentCountry
                "claimantName", CanonicalValue.String fields.ClaimantName
                "insurerName", CanonicalValue.String fields.InsurerName
                "claimedAmount", CanonicalValue.String fields.ClaimedAmount
                "claimedCurrency", CanonicalValue.String fields.ClaimedCurrency
                "caseReference", CanonicalValue.String fields.CaseReference
                "paymentDecisionDate", optionalText fields.PaymentDecisionDate
                "payableAmount", optionalText fields.PayableAmount
                "payableCurrency", optionalText fields.PayableCurrency
                "paymentDate", optionalText fields.PaymentDate
                "status", CanonicalValue.String(CaseStatuses.token fields.Status)
            ]

    let encodeSnapshot (snapshot: CaseSnapshot) =
        match Case.ofAccepted snapshot with
        | Error _ -> invalidArg (nameof snapshot) "Invalid accepted case format."
        | Ok _ ->
            match snapshot.View with
            | CaseView.Erased -> Array.empty
            | CaseView.Live(fields, disposition) ->
                CanonicalValue.Object
                    [
                        "format", CanonicalValue.Integer(int64 RecordVersions.Snapshot)
                        "revision", CanonicalValue.Integer snapshot.Revision
                        "disposition",
                        CanonicalValue.String(
                            if disposition = Disposition.Active then
                                "ACTIVE"
                            else
                                "VOIDED"
                        )
                        "fields", fieldsValue fields
                    ]
                |> CanonicalJson.encode

    let private readFields root : CaseFields =
        let path = "$.fields"

        Json.properties
            path
            [
                "incidentDate"
                "incidentNotificationDate"
                "incidentCountry"
                "claimantName"
                "insurerName"
                "claimedAmount"
                "claimedCurrency"
                "caseReference"
                "paymentDecisionDate"
                "payableAmount"
                "payableCurrency"
                "paymentDate"
                "status"
            ]
            root

        let status =
            match Json.text path "status" root |> CaseStatuses.tryParse with
            | Some value -> value
            | None -> Json.reject "$.fields.status" "Unknown persisted case status."

        {
            IncidentDate = Json.text path "incidentDate" root
            IncidentNotificationDate = Json.text path "incidentNotificationDate" root
            IncidentCountry = Json.text path "incidentCountry" root
            ClaimantName = Json.text path "claimantName" root
            InsurerName = Json.text path "insurerName" root
            ClaimedAmount = Json.text path "claimedAmount" root
            ClaimedCurrency = Json.text path "claimedCurrency" root
            CaseReference = Json.text path "caseReference" root
            PaymentDecisionDate = Json.optionalString path "paymentDecisionDate" root
            PayableAmount = Json.optionalString path "payableAmount" root
            PayableCurrency = Json.optionalString path "payableCurrency" root
            PaymentDate = Json.optionalString path "paymentDate" root
            Status = status
        }

    let private readFormat3 root =
        Json.properties "$" [ "format"; "fields"; "revision"; "disposition" ] root
        let fields = Json.required "$" "fields" root |> readFields

        let disposition =
            match Json.text "$" "disposition" root with
            | "ACTIVE" -> Disposition.Active
            | "VOIDED" -> Disposition.Voided
            | _ -> Json.reject "$.disposition" "Unknown persisted disposition."

        let snapshot =
            {
                Reference = fields.CaseReference
                Revision = Json.integer "$" "revision" root
                View = CaseView.Live(fields, disposition)
            }

        match Case.ofAccepted snapshot with
        | Ok _ -> snapshot
        | Error _ -> Json.reject "$.fields" "Invalid accepted case format."

    let decodeSnapshot bytes =
        Json.bounded 262144 bytes (Json.dispatch [ 3L, readFormat3 ])
