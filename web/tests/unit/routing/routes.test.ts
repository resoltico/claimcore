import { expect, it } from "vitest";
import { actPath, casePath, parseRoute } from "../../../src/routing/routes";
import { navigate, setNavigationLock } from "../../../src/routing/navigation";
import { rememberLoginReturn, restoreLoginReturn } from "../../../src/routing/loginReturn";

it.each(["CASE-0042", "new", "NEW", ".", "..", "/", "%", "~", "~bmV3", "A/B", "100%", '!~<>"'])(
  "round trips valid reference %s through actual URL normalization",
  (reference) => {
    const location = new URL(casePath(reference), window.location.origin);
    expect(parseRoute(location)).toEqual({ screen: "case", reference });
    expect(parseRoute(new URL(actPath(reference, "CORRECT"), window.location.origin))).toEqual({
      screen: "caseAct",
      reference,
      act: "CORRECT",
    });
  },
);
it("reserves the OPEN route while preserving all ordinary deep links", () => {
  expect(parseRoute(new URL("/cases/new", window.location.origin))).toEqual({ screen: "newCase" });
  expect(parseRoute(new URL("/operations/op-id", window.location.origin))).toEqual({
    screen: "operations",
    operationId: "op-id",
  });
  expect(parseRoute(new URL("/authority/actor-id", window.location.origin))).toEqual({
    screen: "authority",
    actorId: "actor-id",
    history: false,
  });
  expect(parseRoute(new URL("/cases/%", window.location.origin))).toEqual({ screen: "missing" });
});
it("keeps worklist state in the URL and blocks mutation navigation and unload", () => {
  navigate("/cases?view=recent&q=R%C4%ABga&order=RECENT");
  expect(parseRoute(window.location)).toEqual({
    screen: "cases",
    query: "?view=recent&q=R%C4%ABga&order=RECENT",
  });
  setNavigationLock(true);
  expect(navigate("/authority")).toBe(false);
  const unload = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(unload);
  expect(unload.defaultPrevented).toBe(true);
  setNavigationLock(false);
});
it("restores a same-origin deep link once after login and refuses an external return", () => {
  navigate("/cases/CASE-0042/acts/DECIDE");
  rememberLoginReturn();
  navigate("/cases");
  restoreLoginReturn();
  expect(window.location.pathname).toBe("/cases/CASE-0042/acts/DECIDE");
  navigate("/cases");
  restoreLoginReturn();
  expect(window.location.pathname).toBe("/cases");
  sessionStorage.setItem("claimcore.loginReturn", "//outside.example/cases");
  restoreLoginReturn();
  expect(window.location.pathname).toBe("/cases");
  expect(sessionStorage.getItem("claimcore.loginReturn")).toBeNull();
});
