namespace ClaimCore.Database

open System
open System.Globalization
open System.Security.Cryptography
open System.Text

module internal DatabaseRestoreWalObjectDigest =
    let compute (objects: FencedWalObject list) =
        let lines =
            objects
            |> List.sortBy _.RelativePath
            |> List.map (fun item ->
                String.Join(
                    "|",
                    [|
                        item.ObjectId.ToString("D")
                        item.Cluster
                        item.RelativePath
                        item.CiphertextSha256
                        item.CiphertextBytes.ToString(CultureInfo.InvariantCulture)
                        item.Segment
                        item.SegmentBytes.ToString(CultureInfo.InvariantCulture)
                    |]
                ))

        Encoding.ASCII.GetBytes(String.Join("\n", lines) + "\n")
        |> SHA256.HashData
        |> Convert.ToHexStringLower
