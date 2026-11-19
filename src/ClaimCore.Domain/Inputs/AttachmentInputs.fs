module ClaimCore.Domain.Inputs.AttachmentInputs

open System
open System.Globalization
open ClaimCore.Domain

let private target = TaskInputId.Target

let private requiredValue violation parse value =
    parse value |> Option.map Ok |> Option.defaultValue (Error violation)

let private length (value: string) =
    match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, length when length >= 0L && length <= int64 AttachmentLimits.FileBytes -> Some length
    | _ -> None

let intent inputs =
    let name =
        Parsing.scalar
            InputTarget.AttachmentName
            (requiredValue Violation.AttachmentNameInvalid AttachmentName.parse)
            inputs

    let count =
        Parsing.scalar
            InputTarget.AttachmentLength
            (requiredValue Violation.AttachmentLengthInvalid length)
            inputs

    let digest =
        Parsing.scalar
            InputTarget.AttachmentDigest
            (requiredValue Violation.AttachmentDigestInvalid AttachmentDigest.parse)
            inputs

    Admission.map2 (fun name count -> name, count) name count
    |> fun nameAndCount ->
        Admission.map2
            (fun (name, count) digest -> AttachmentIntent.create name count digest |> Option.get)
            nameAndCount
            digest

let canonicalInputs (inputs: InputMap) intent =
    let values =
        inputs.Values
        |> Map.add
            (target InputTarget.AttachmentName)
            (InputValue.Text(AttachmentName.value (AttachmentIntent.name intent)))
        |> Map.add
            (target InputTarget.AttachmentLength)
            (InputValue.Text(
                (AttachmentIntent.length intent).ToString(CultureInfo.InvariantCulture)
            ))
        |> Map.add
            (target InputTarget.AttachmentDigest)
            (InputValue.Text(AttachmentDigest.value (AttachmentIntent.digest intent)))

    InputMap.create values (inputs.Collections |> Map.map (fun _ rows -> rows |> List.map _.Values))
    |> Option.get
