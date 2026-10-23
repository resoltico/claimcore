namespace ClaimCore.Domain

open ClaimCore.Contract

module CaseReference =
    let validate reference =
        Validation.text InputTarget.CaseReference reference |> Result.map ignore

    /// The profile is ASCII; folding never consults culture or Unicode normalization.
    let key (reference: string) =
        reference
        |> Seq.map (fun character ->
            if character >= 'a' && character <= 'z' then
                char (int character - 32)
            else
                character)
        |> Seq.toArray
        |> System.String
