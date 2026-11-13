namespace ClaimCore.Domain

open System

/// Authored eligibility snapshots; names and formatting belong to Presentation.
/// Country source: RIPE NCC's maintained ISO 3166-1 table (retrieved 2026-11-13).
/// https://www.ripe.net/community/internet-governance/internet-technical-community/the-rir-system/list-of-country-codes-and-rirs/
/// Official assignment scope: https://www.iso.org/iso-3166-country-codes.html
/// XK and ZZ are explicit ClaimCore extensions; no other reserved/user-assigned codes.
/// Currency source: SIX ISO 4217 List One, published 2026-09-17, retrieved 2026-11-13.
/// https://www.six-group.com/dam/download/financial-information/data-center/iso-currrency/lists/list-one.xml
/// XML SHA256 33139b438657d1cee116ba737807ea71d19d6de4b90f799a09c56f0cc6a1b0ff.
/// Excludes fund-marked and X-prefixed non-national codes; retains XAF/XCD/XOF/XPF/XCG.
/// These vocabularies do not impose per-currency minor-unit admission rules.
module CodeVocabularies =
    let countryAsOf = "2026-11-13"
    let currencyAsOf = "2026-09-17"

    let countrySource =
        "https://www.ripe.net/community/internet-governance/internet-technical-community/the-rir-system/list-of-country-codes-and-rirs/"

    let currencySource =
        "https://www.six-group.com/dam/download/financial-information/data-center/iso-currrency/lists/list-one.xml"

    let countries =
        "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ
         BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS
         BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN
         CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE
         EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF
         GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM
         HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM
         JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC
         LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK
         ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA
         NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG
         PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW
         SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS
         ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO
         TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI
         VN VU WF WS XK YE YT ZA ZM ZW ZZ"
            .Split([| ' '; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    let currencies =
        "AED AFN ALL AMD AOA ARS AUD AWG AZN BAM BBD BDT BHD BIF BMD BND
         BOB BRL BSD BTN BWP BYN BZD CAD CDF CHF CLP CNY COP CRC CUP CVE
         CZK DJF DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD
         GNF GTQ GYD HKD HNL HTG HUF IDR ILS INR IQD IRR ISK JMD JOD JPY
         KES KGS KHR KMF KPW KRW KWD KYD KZT LAK LBP LKR LRD LSL LYD MAD
         MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MYR MZN NAD NGN NIO
         NOK NPR NZD OMR PAB PEN PGK PHP PKR PLN PYG QAR RON RSD RUB RWF
         SAR SBD SCR SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB
         TJS TMT TND TOP TRY TTD TWD TZS UAH UGX USD UYU UZS VED VES VND
         VUV WST XAF XCD XCG XOF XPF YER ZAR ZMW ZWG"
            .Split([| ' '; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    let private countrySet = Set.ofList countries
    let private currencySet = Set.ofList currencies
    let containsCountry code = Set.contains code countrySet
    let containsCurrency code = Set.contains code currencySet
