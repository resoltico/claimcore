namespace ClaimCore.Witness

open System.IO
open System.Text

/// A role's admission script in two parts. `Structure` answers from catalog state alone and may be
/// vouched for by a `CatalogEpoch`; `Liveness` (session settings, role attributes, installation
/// identity, epoch and tip) can change without a catalog write and runs on every checkout.
[<NoEquality; NoComparison>]
type internal AdmissionScripts = { Structure: string; Liveness: string }

module internal WitnessAdmission =
    let resourceText (name: string) =
        use stream =
            typeof<Identity>.Assembly.GetManifestResourceStream(name)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Witness admission SQL is missing.")

        use buffer = new MemoryStream()
        stream.CopyTo(buffer)
        UTF8Encoding(false, true).GetString(buffer.ToArray())

    let private scripts =
        lazy
            {
                Structure = resourceText "ClaimCore.Witness.Admission.Structure.sql"
                Liveness = resourceText "ClaimCore.Witness.Admission.Liveness.sql"
            }

    let script () = scripts.Value
