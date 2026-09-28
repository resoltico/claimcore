namespace ClaimCore.Witness

open System.IO
open System.Text

module internal WitnessAuditAdmission =
    let script () =
        use stream =
            typeof<Identity>
                .Assembly.GetManifestResourceStream("ClaimCore.Witness.AuditAdmission.sql")
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Witness auditor admission SQL is missing.")

        use buffer = new MemoryStream()
        stream.CopyTo(buffer)
        UTF8Encoding(false, true).GetString(buffer.ToArray())
