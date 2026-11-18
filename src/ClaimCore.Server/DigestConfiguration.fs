namespace ClaimCore.Server

open System
open System.Globalization
open System.IO
open System.Security.Cryptography.X509Certificates
open ClaimCore.Presentation
open MimeKit

[<RequireQualifiedAccess>]
type DigestSmtpTls =
    | Implicit
    | StartTls

[<NoEquality; NoComparison>]
type DigestSmtpConfiguration =
    {
        Host: string
        Port: int
        Tls: DigestSmtpTls
        Credentials: (string * string) option
        TrustRoot: X509Certificate2 option
        From: MailboxAddress
    }

[<NoEquality; NoComparison>]
type DigestConfiguration =
    {
        Smtp: DigestSmtpConfiguration
        Time: TimeOnly
        Language: string
    }

module internal DigestMailbox =
    let parse (value: string) =
        let mutable parsed: MailboxAddress | null = null

        if MailboxAddress.TryParse(value, &parsed) then
            parsed
            |> Option.ofObj
            |> Option.filter (fun address ->
                address.Address = value && String.IsNullOrEmpty address.Name)
        else
            None

module internal DigestConfiguration =
    let private invalid setting =
        WebStartupDiagnostics.refuse (WebStartupProblem.InvalidSetting setting)

    let private credentials path =
        let setting = WebSetting.DigestSmtpCredentialsFile

        let contents =
            match PrivateInputs.text 8192 path with
            | Ok text -> text
            | Error _ -> invalid setting

        use lines = new StringReader(contents)

        let line () =
            match lines.ReadLine() |> Option.ofObj with
            | Some value when
                not (String.IsNullOrWhiteSpace value) && not (Seq.exists Char.IsControl value)
                ->
                value
            | _ -> invalid setting

        let user = line ()
        let password = line ()

        if not (isNull (lines.ReadLine())) then
            invalid setting

        user, password

    let load () =
        let settings =
            WebSettings.digest
            |> List.map (fun setting ->
                setting,
                Environment.GetEnvironmentVariable(WebSettings.token setting) |> Option.ofObj)
            |> Map.ofList

        let required setting =
            settings[setting]
            |> Option.defaultWith (fun () ->
                WebStartupDiagnostics.refuse (WebStartupProblem.MissingSetting setting))

        if settings |> Map.forall (fun _ value -> value.IsNone) then
            None
        else
            let host = required WebSetting.DigestSmtpHost

            if Uri.CheckHostName host = UriHostNameType.Unknown then
                invalid WebSetting.DigestSmtpHost

            let port =
                match
                    Int32.TryParse(
                        required WebSetting.DigestSmtpPort,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture
                    )
                with
                | true, value when 1 <= value && value <= 65535 -> value
                | _ -> invalid WebSetting.DigestSmtpPort

            let tls =
                match required WebSetting.DigestSmtpTls with
                | "IMPLICIT" -> DigestSmtpTls.Implicit
                | "STARTTLS" -> DigestSmtpTls.StartTls
                | _ -> invalid WebSetting.DigestSmtpTls

            let sender =
                required WebSetting.DigestFrom
                |> DigestMailbox.parse
                |> Option.defaultWith (fun () -> invalid WebSetting.DigestFrom)

            let timeSource = required WebSetting.DigestTime
            let timeFormat = "HH:mm"

            let time =
                match
                    TimeOnly.TryParseExact(
                        timeSource,
                        timeFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None
                    )
                with
                | true, value when
                    value.ToString(timeFormat, CultureInfo.InvariantCulture) = timeSource
                    ->
                    value
                | _ -> invalid WebSetting.DigestTime

            let language = settings[WebSetting.DigestLanguage] |> Option.defaultValue "en"

            if not (List.contains language LocalizationPolicy.languages) then
                invalid WebSetting.DigestLanguage

            let smtpCredentials =
                settings[WebSetting.DigestSmtpCredentialsFile] |> Option.map credentials

            let trustRoot =
                settings[WebSetting.DigestSmtpCaCertFile]
                |> Option.map (
                    PrivateTrustRoot.load (
                        WebStartupProblem.InvalidSetting WebSetting.DigestSmtpCaCertFile
                    )
                )

            Some
                {
                    Smtp =
                        {
                            Host = host
                            Port = port
                            Tls = tls
                            Credentials = smtpCredentials
                            TrustRoot = trustRoot
                            From = sender
                        }
                    Time = time
                    Language = language
                }

    let dispose (configuration: DigestConfiguration) =
        configuration.Smtp.TrustRoot |> Option.iter _.Dispose()
