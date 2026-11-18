namespace ClaimCore.Domain.Inputs

open ClaimCore.Domain

/// Acquires the one explicitly selected related case.
module RelatedCaseInput =
    let selector inputs =
        Admission.map2
            (fun reference id -> reference, id)
            (Parsing.optionalText InputTarget.LinkTargetReference inputs)
            (Parsing.optionalText InputTarget.LinkTargetId inputs)
        |> Result.bind (fun (reference, id) ->
            match reference, id with
            | Some value, None ->
                CaseReference.parse value
                |> Parsing.at InputTarget.LinkTargetReference
                |> Result.map (CaseReference.format >> CaseSelector.Reference)
            | None, Some raw ->
                match System.Guid.TryParseExact(raw, "D") with
                | true, value ->
                    CaseId.create value
                    |> Option.map (CaseSelector.Id >> Ok)
                    |> Option.defaultValue (
                        Error(Parsing.issue InputTarget.LinkTargetId Violation.CaseIdRequired)
                    )
                | _ -> Error(Parsing.issue InputTarget.LinkTargetId Violation.CaseIdRequired)
            | _ ->
                Error(Parsing.issue InputTarget.LinkTargetReference Violation.CaseSelectionInvalid))
