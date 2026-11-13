import { presentationLanguageOptions } from "@contracts/client";
import { expect, it } from "vitest";
import { codeSuggestions, rankCodeSuggestions } from "../../../src/presentation/codeSuggestions";

it("ranks exact LV above name matches and US code prefix above a name substring", () => {
  const countries = [
    { code: "MV", name: "Maldives" },
    { code: "LV", name: "Latvia" },
    { code: "SV", name: "El Salvador" },
  ];
  expect(rankCodeSuggestions(countries, "lv", "en")[0]?.code).toBe("LV");
  for (const option of presentationLanguageOptions) {
    expect(
      rankCodeSuggestions(codeSuggestions(option.value, false, "Unknown"), "LV", option.value)[0]
        ?.code,
    ).toBe("LV");
  }
  expect(
    rankCodeSuggestions(
      [
        { code: "AUD", name: "Australian Dollar" },
        { code: "USD", name: "US Dollar" },
      ],
      "US",
      "en",
    )[0]?.code,
  ).toBe("USD");
});
it("puts German DE first and offers neither retired nor pseudo codes", () => {
  const countries = codeSuggestions("de", false, "Unbekannt oder international");
  expect(rankCodeSuggestions(countries, "DE", "de")[0]?.code).toBe("DE");
  expect(countries.map((item) => item.code)).not.toContain("DD");
  expect(countries.map((item) => item.code)).not.toContain("XA");
  expect(countries.map((item) => item.code)).toContain("XK");
  expect(codeSuggestions("en", true, "Unknown").map((item) => item.code)).not.toContain("XXX");
});
