namespace ClaimCore.Domain.Inputs

open ClaimCore.Domain
open ClaimCore.Domain.Changes

module internal CaseAdmission =
    /// Equality with accepted state preserves existing reassertion semantics; omission never re-admits.
    let fieldAdmission today (before: CaseContent option) =
        let previous =
            before
            |> Option.map (fun claim ->
                CaseFields.groupValues claim.Registration claim.Finance.Decision |> Map.ofList)
            |> Option.defaultValue Map.empty

        fun field value ->
            if Map.tryFind field previous = Some value then
                Ok value
            else
                FieldValue.admit today value
