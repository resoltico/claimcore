import { it } from "vitest";
import { expectDomainHelpersExact } from "./domain-metadata.fixtures";

it("keeps descriptor prefill, dirty state, grouped defaults and frozen requests exact", () => {
  expectDomainHelpersExact();
});
