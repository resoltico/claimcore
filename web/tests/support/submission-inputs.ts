type PrefixedInputs<P extends string, T extends object> = {
  readonly [K in keyof T as `${P}${Extract<K, string>}`]: T[K];
};
/** Fixture facts use their declared token prefix without a hand-maintained field list. */
export const prefixedInputs = <P extends string, T extends object>(
  prefix: P,
  values: T,
): PrefixedInputs<P, T> =>
  Object.fromEntries(
    Object.entries(values).map(([key, value]) => [prefix + key, value]),
  ) as PrefixedInputs<P, T>;
