import { expect, it } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { current } from "../../../support/operation-form.fixtures";
import { definition } from "../../../support/service-ui.fixtures";
import { CaseProgress } from "../../../../src/views/case/CaseProgress";

it("renders the core-supplied progress without deriving it from nullable groups", () => {
  render(
    <CaseProgress
      current={{
        ...current,
        state: {
          ...current.state,
          fields: { ...current.state.fields, progress: "PAID", decision: null, payment: null },
        },
      }}
      definition={definition.catalog}
    />,
  );
  expect(screen.getByText("Paid", { exact: true })).toHaveAttribute("aria-current", "step");
});
