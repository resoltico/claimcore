import { coreCatalogs, chromeCatalogs } from "./authored-catalogs";
import { installLanguage } from "../../src/presentation/languageCatalog";
import { presentationLanguageOptions } from "@contracts/client";
// Only the required three languages supply translated renderer inputs; real word resolution is tested through the host.
for (const { value: language } of presentationLanguageOptions) {
  const fixture = language === "lv" || language === "ar" ? language : "en";
  installLanguage(language, coreCatalogs[fixture], chromeCatalogs[fixture]);
}
import { clearRecoveryFixtures } from "./recovery.fixtures";
import { navigate } from "../../src/routing/navigation";
import * as matchers from "@testing-library/jest-dom/matchers";
import { cleanup } from "@testing-library/react";
import { beforeEach, afterEach, expect, vi } from "vitest";

expect.extend(matchers);
beforeEach(() => {
  vi.spyOn(navigator, "languages", "get").mockReturnValue(["en-GB"]);
});

afterEach(() => {
  cleanup();
  clearRecoveryFixtures();
  if (typeof document !== "undefined") {
    window.localStorage.clear();
    navigate("/cases", true);
    document.documentElement.lang = "en";
    document.documentElement.dir = "ltr";
  }
});
