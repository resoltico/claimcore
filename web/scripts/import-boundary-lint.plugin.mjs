// Load only the configured upstream rules through their public runtime exports. Their type
// declarations target the retired TypeScript compiler API, which the native checker does not expose.
/** @param {string} name */
const upstreamRule = async (name) => {
  const specifier = `eslint-plugin-import-x/rules/${name}`;
  /** @type {unknown} */
  const namespace = await import(specifier);
  if (typeof namespace !== "object" || namespace === null || !("default" in namespace)) {
    throw new Error("The upstream import-boundary rule export is unavailable.");
  }
  return namespace.default;
};

export default {
  rules: {
    "no-unresolved": await upstreamRule("no-unresolved"),
    "no-nodejs-modules": await upstreamRule("no-nodejs-modules"),
    "no-restricted-paths": await upstreamRule("no-restricted-paths"),
  },
};
