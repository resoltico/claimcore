import "vitest";

declare module "vitest" {
  // jest-dom 7 still augments Vitest's former one-parameter Assertion; setup.ts registers
  // the runtime matchers while this narrow declaration tracks only the matchers ClaimCore uses.
  interface Assertion<R extends void | Promise<void> = void, T = unknown> {
    toBeDisabled(this: Assertion<R, T>): R;
    toBeVisible(): R;
    toHaveAttribute(attribute: string, value?: unknown): R;
    toHaveTextContent(text: string | RegExp, options?: { normalizeWhitespace: boolean }): R;
    toHaveValue(value?: string | string[] | number | null): R;
  }
}
