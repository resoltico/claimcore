module ClaimCore.TestSupport.PostgresBaseline

open System.IO
open System.Text
open ClaimCore.Application.Storage

let parse text =
    use stream = new MemoryStream(Encoding.UTF8.GetBytes(text: string))
    let baseline = ServerSupport.parse stream
    baseline.ContainerImage, baseline.InitdbArguments

let configuration () =
    let root = RepositoryRoot.find ()
    File.ReadAllText(Path.Combine(root, "db/postgresql-baseline.json")) |> parse

let image () = configuration () |> fst
