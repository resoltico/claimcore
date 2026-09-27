module ClaimCore.AcceptanceTests.Configuration

open System
open System.IO

[<NoEquality; NoComparison>]
type Inputs =
    {
        CliDll: string
        PrivateDirectory: string
        ServiceUrl: string
        Issuer: string
        OidcCa: string
        WebCertificate: string
        ClientId: string
        SecretFile: string
        PublicClientId: string
        OidcCredentialsFile: string
        BrowserDirectory: string
        BrowserDriver: string
    }

let private required name =
    match Environment.GetEnvironmentVariable(name) |> Option.ofObj with
    | Some value when not (String.IsNullOrWhiteSpace(value)) -> value
    | _ -> invalidOp ($"Published CLI acceptance prerequisite {name} is missing.")

let private directory name =
    let path = required name

    if not (Path.IsPathFullyQualified(path)) then
        invalidOp ($"Published CLI acceptance prerequisite {name} is not absolute.")

    let information = DirectoryInfo(path)
    let attributes = information.Attributes

    if not information.Exists || attributes.HasFlag(FileAttributes.ReparsePoint) then
        invalidOp ($"Published CLI acceptance prerequisite {name} is not a regular directory.")

    path

let private file name =
    let path = required name

    if not (Path.IsPathFullyQualified(path)) then
        invalidOp ($"Published CLI acceptance prerequisite {name} is not absolute.")

    let information = FileInfo(path)
    let attributes = information.Attributes

    if
        not information.Exists
        || attributes.HasFlag(FileAttributes.Directory)
        || attributes.HasFlag(FileAttributes.ReparsePoint)
    then
        invalidOp ($"Published CLI acceptance prerequisite {name} is not a regular file.")

    path

let load () =
    let cliDirectory = directory "CLAIMCORE_ACCEPTANCE_CLI_DIR"
    let manifest = file "CLAIMCORE_ACCEPTANCE_CLI_MANIFEST"
    PublishManifest.verify "publish-cli" cliDirectory manifest |> ignore

    let cliDll = Path.Combine(cliDirectory, "ClaimCore.Cli.dll")

    if not (File.Exists(cliDll)) then
        invalidOp "Verified published CLI assembly is missing."

    let service = required "CLAIMCORE_ACCEPTANCE_SERVICE_URL"
    let issuer = required "CLAIMCORE_ACCEPTANCE_ISSUER"

    if not (service.StartsWith("https://", StringComparison.Ordinal)) then
        invalidOp "Published service URL is not HTTPS."

    if not (issuer.StartsWith("https://", StringComparison.Ordinal)) then
        invalidOp "Published issuer URL is not HTTPS."

    {
        CliDll = cliDll
        PrivateDirectory = directory "CLAIMCORE_ACCEPTANCE_PRIVATE_DIR"
        ServiceUrl = service
        Issuer = issuer
        OidcCa = file "CLAIMCORE_ACCEPTANCE_OIDC_CA_FILE"
        WebCertificate = file "CLAIMCORE_ACCEPTANCE_WEB_CERT_FILE"
        ClientId = required "CLAIMCORE_ACCEPTANCE_SERVICE_CLIENT_ID"
        SecretFile = file "CLAIMCORE_ACCEPTANCE_SERVICE_SECRET_FILE"
        PublicClientId = required "CLAIMCORE_ACCEPTANCE_PUBLIC_CLIENT_ID"
        OidcCredentialsFile = file "CLAIMCORE_ACCEPTANCE_OIDC_CREDENTIALS_FILE"
        BrowserDirectory = directory "CLAIMCORE_ACCEPTANCE_BROWSER_DIR"
        BrowserDriver = file "CLAIMCORE_ACCEPTANCE_DRIVER_PATH"
    }
