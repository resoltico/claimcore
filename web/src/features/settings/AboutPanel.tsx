import { legal } from "@contracts/legal";
import { productMetadata } from "../../protocol/productMetadata";
import { usePresentation } from "../../presentation/context";
import "./about.css";

const legalLinks = [
  { href: `/${legal.licenseFile}.txt`, label: "ui.legalLicense", license: true },
  { href: `/${legal.sourceFile}`, label: "ui.legalSource", download: true },
  { href: `/${legal.projectNotice}`, label: "ui.legalProjectNotice" },
  { href: `/${legal.thirdPartyNotices}`, label: "ui.legalThirdParty" },
  { href: `/${legal.commercialNotice}`, label: "ui.legalCommercial" },
] as const;

export const AboutPanel = () => {
  const p = usePresentation();
  return (
    <div className="about-panel">
      <div lang="en">
        <p className="about-version">
          {legal.productName} {productMetadata.productVersion}
        </p>
        <p>
          {legal.licenseName} · <bdi>{legal.license}</bdi>
        </p>
        <p>{legal.copyright}</p>
        <p>{legal.rights}</p>
      </div>
      <ul className="about-links">
        {legalLinks.map((link) => (
          <li key={link.href}>
            <a
              href={link.href}
              rel={"license" in link ? "license" : undefined}
              download={"download" in link ? true : undefined}
            >
              {p.text(link.label)}
            </a>
          </li>
        ))}
      </ul>
      <details>
        <summary>{p.text("label.technicalDetails")}</summary>
        <p>
          <bdi dir="ltr">{productMetadata.contractIdentity}</bdi>
        </p>
      </details>
    </div>
  );
};
