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
