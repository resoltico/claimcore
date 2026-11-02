import { catalogs as coreCatalogs } from "../../src/generated/core-catalogs";
import { catalogs as chromeCatalogs } from "../../src/generated/chrome-catalogs";
import { installLanguage } from "../../src/presentation/languageCatalog";
for (const language of ["en", "lv", "ar"] as const) {
  installLanguage(language, coreCatalogs[language], chromeCatalogs[language]);
}
import { clearRecoveryFixtures } from "./recovery.fixtures";
import { navigate, setNavigationLock } from "../../src/routing/navigation";
import * as matchers from "@testing-library/jest-dom/matchers";
import { cleanup } from "@testing-library/react";
import { afterEach, expect } from "vitest";

expect.extend(matchers);

afterEach(() => {
  cleanup();
  clearRecoveryFixtures();
  if (typeof document !== "undefined") {
    window.localStorage.clear();
    setNavigationLock(false);
    navigate("/cases", true);
    document.documentElement.lang = "en";
    document.documentElement.dir = "ltr";
  }
});
