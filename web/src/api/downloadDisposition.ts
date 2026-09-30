const exactFilenameParameter = (name: string, value: string, expected: string): boolean => {
  if (name === "filename") {
    return value === expected || value === `"${expected}"`;
  }
  if (name === "filename*") {
    return value === `UTF-8''${encodeURIComponent(expected)}`;
  }
  return false;
};

/** ASP.NET FileResult emits both ASCII filename parameters; accept no competing suggestion. */
export const exactDownloadDisposition = (header: string | null, expected: string): boolean => {
  if (header === null) {
    return false;
  }
  const [kind, ...parameters] = header.split(";").map((part) => part.trim());
  if (kind?.toLowerCase() !== "attachment" || parameters.length !== 2) {
    return false;
  }
  const seen = new Set<string>();
  for (const parameter of parameters) {
    const equals = parameter.indexOf("=");
    if (equals < 1) {
      return false;
    }
    const name = parameter.slice(0, equals).toLowerCase();
    const value = parameter.slice(equals + 1);
    if (seen.has(name)) {
      return false;
    }
    if (!exactFilenameParameter(name, value, expected)) {
      return false;
    }
    seen.add(name);
  }
  return seen.has("filename") && seen.has("filename*");
};
