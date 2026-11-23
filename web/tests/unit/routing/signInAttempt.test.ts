import { beforeEach, expect, it } from "vitest";
import {
  markSignedOut,
  recordAutomaticSignIn,
  resetSignIn,
  signedOut,
} from "../../../src/routing/signInAttempt";

beforeEach(() => {
  sessionStorage.clear();
});

it("stops the third automatic sign-in inside the same minute", () => {
  expect(recordAutomaticSignIn(sessionStorage, 1_000)).toBe(true);
  expect(recordAutomaticSignIn(sessionStorage, 15_000)).toBe(true);
  expect(recordAutomaticSignIn(sessionStorage, 60_999)).toBe(false);
  expect(recordAutomaticSignIn(sessionStorage, 61_000)).toBe(true);
});

it("retains sign-out in the tab and clears it on an explicit sign-in", () => {
  recordAutomaticSignIn(sessionStorage, 1_000);
  markSignedOut(sessionStorage);
  expect(signedOut(sessionStorage)).toBe(true);
  resetSignIn(sessionStorage);
  expect(signedOut(sessionStorage)).toBe(false);
  expect(recordAutomaticSignIn(sessionStorage, 2_000)).toBe(true);
  expect(recordAutomaticSignIn(sessionStorage, 3_000)).toBe(true);
});
