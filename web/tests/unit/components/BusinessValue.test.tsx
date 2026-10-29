import { expect, it } from "vitest";
import { BusinessValue } from "../../../src/components/BusinessValue";
import { productMetadata } from "../../../src/protocol/productMetadata";
import { render } from "../../support/presentation-test-support";
import { preferenceKey } from "../../../src/presentation/preferences";
it("keeps Arabic-formatted numbers and references LTR while names retain automatic direction", () => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 2, language: "ar", displayLocale: "ar-EG", theme: "LIGHT" }),
  );
  for (const [name, value, direction] of [
    ["claimedAmount", "12.34", "ltr"],
    ["incidentDate", "2026-09-01", "ltr"],
    ["caseReference", "CASE-42", "ltr"],
    ["claimantName", "المطالب", "auto"],
  ] as const) {
    const field = productMetadata.catalog.fields.find((candidate) => candidate.name === name)!;
    const view = render(<BusinessValue value={value} field={field} />);
    expect(view.container.querySelector("bdi")).toHaveAttribute("dir", direction);
    if (name === "claimedAmount") {
      expect(view.container.querySelector("bdi")).toHaveTextContent("١٢");
    }
    view.unmount();
  }
});
