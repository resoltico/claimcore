import { beforeEach, expect, it } from "vitest";
import {
  markSignedOut,
  recordAutomaticSignIn,
  resetSignIn,
  signInBookkeeping,
} from "../../../src/routing/signInAttempt";

beforeEach(() => {
  sessionStorage.clear();
});

it("stops the third automatic sign-in inside the same minute", () => {
  expect(recordAutomaticSignIn(1_000)).toBe(true);
  expect(recordAutomaticSignIn(15_000)).toBe(true);
  expect(recordAutomaticSignIn(60_999)).toBe(false);
  expect(recordAutomaticSignIn(61_000)).toBe(true);
});

it("retains sign-out in the tab and clears it on an explicit sign-in", () => {
  recordAutomaticSignIn(1_000);
  markSignedOut();
  expect(signInBookkeeping()).toBe("signedOut");
  resetSignIn();
  expect(signInBookkeeping()).toBe("ready");
  expect(recordAutomaticSignIn(2_000)).toBe(true);
  expect(recordAutomaticSignIn(3_000)).toBe(true);
});

it("does not equate unavailable bookkeeping with sign-out or permit a redirect", () => {
  const denied = () => {
    throw new Error("Storage getter denied");
  };
  expect(signInBookkeeping(denied)).toBe("unavailable");
  expect(recordAutomaticSignIn(1_000, denied)).toBe(false);
  expect(() => {
    markSignedOut(denied);
  }).not.toThrow();
  expect(() => {
    resetSignIn(denied);
  }).not.toThrow();
});
