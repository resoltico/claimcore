import { legal } from "@contracts/legal";
/** Available before authentication as well as in the workspace. */
export const LegalNotice = () => (
  <aside aria-label="License and source" className="legal-notice" lang="en" dir="ltr">
    <details>
      <summary>ClaimCore · {legal.license} · License and source</summary>
      <p>
        {legal.copyright}. {legal.rights}
      </p>
      <p>
        <a href="/LICENSE.txt" rel="license">
          License
        </a>
        {" · "}
        <a href={`/${legal.sourceFile}`} download>
          Source for this build
        </a>
        {" · "}
        <a href={`/${legal.projectNotice}`}>Licensing and source details</a>
        {" · "}
        <a href={`/${legal.commercialNotice}`}>Commercial terms pending</a>
      </p>
    </details>
  </aside>
);
