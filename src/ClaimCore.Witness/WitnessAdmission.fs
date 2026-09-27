namespace ClaimCore.Witness

open System.IO
open System.Text

module internal WitnessAdmission =
    let script () =
        use stream =
            typeof<Identity>.Assembly.GetManifestResourceStream("ClaimCore.Witness.Admission.sql")
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Witness admission SQL is missing.")

        use buffer = new MemoryStream()
        stream.CopyTo(buffer)
        UTF8Encoding(false, true).GetString(buffer.ToArray())
