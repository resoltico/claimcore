namespace ClaimCore.Wire

/// Shared project facts; interfaces choose their own presentation.
module LegalNotice =
    let copyright = "Copyright © 2026 Ervins Strauhmanis"
    let productName = "ClaimCore"
    let licenseName = "GNU Affero General Public License, version 3"
    let license = "AGPL-3.0-only"
    let licenseFile = "LICENSE"
    let sourceFile = "SOURCE.tar.gz"
    let projectNotice = "PROJECT-NOTICE.md"
    let commercialNotice = "LICENSE-COMMERCIAL.md"
    let thirdPartyNotices = "THIRD-PARTY-NOTICES.txt"
    let rights = "No warranty. You may redistribute under the license terms."

    let summary =
        copyright
        + ". "
        + license
        + ". "
        + rights
        + "\nLicense: "
        + licenseFile
        + "; source for this build: "
        + sourceFile
        + "; licensing and source details: "
        + projectNotice
        + ".\n"
