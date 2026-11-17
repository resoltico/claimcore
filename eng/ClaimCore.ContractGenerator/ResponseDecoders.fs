namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Json
open ClaimCore.Wire

/// Generates structural response admission from the same finite schemas as native codecs.
module ResponseDecoders =
    let private quote (value: string) = JsonSerializer.Serialize value
    let private array values = "[" + String.concat "," values + "]"
    let private number value = string value

    let private optional encode =
        Option.map encode >> Option.defaultValue "null"

    let rec private schema =
        function
        | Schema.JsonObject -> "[\"jsonObject\"]"
        | Schema.Object(properties, optional) ->
            let fields =
                properties
                |> List.map (fun (key, value) -> quote key + ":" + schema value)
                |> String.concat ","

            array [ quote "object"; "{" + fields + "}"; optional |> List.map quote |> array ]
        | Schema.Array(item, minimum, maximum) ->
            array [ quote "array"; schema item; number minimum; number maximum ]
        | Schema.String(pattern, format, minimum, maximum, bytes) ->
            format
            |> Option.iter (function
                | "uuid"
                | "date"
                | "date-time" -> ()
                | value -> invalidArg "format" ("Unsupported browser scalar format: " + value))
            // Reject regex features outside the ECMAScript Unicode subset used by our declarations.
            pattern
            |> Option.iter (fun value ->
                if
                    value.Contains("(?<")
                    || value.Contains("(?(")
                    || value.Contains("\\A")
                    || value.Contains("\\z")
                    || value.Contains("\\Z")
                then
                    invalidArg "pattern" "Unsupported browser scalar pattern.")

            array
                [
                    quote "string"
                    optional quote pattern
                    optional quote format
                    optional number minimum
                    optional number maximum
                    optional number bytes
                ]
        | Schema.Integer(minimum, maximum) ->
            array [ quote "integer"; number minimum; number maximum ]
        | Schema.Boolean -> "[\"boolean\"]"
        | Schema.Null -> "[\"null\"]"
        | Schema.Const value -> array [ quote "const"; quote value ]
        | Schema.OneOf(variants, _) -> array [ quote "oneOf"; variants |> List.map schema |> array ]
        | Schema.Ref name ->
            if not (Map.containsKey name OpenApi.definitions) then
                invalidArg "schema" ("Missing browser schema reference: " + name)

            array [ quote "ref"; quote name ]
        | Schema.Vocabulary(_, value) -> schema value
        | Schema.EnumPartition(shape, values, selected, rejected, key) ->
            array
                [
                    quote "partition"
                    schema shape
                    values |> List.map quote |> array
                    quote selected
                    quote rejected
                    quote key
                ]

    let source () =
        let definitions =
            OpenApi.definitions
            |> Map.toList
            |> List.map (fun (key, value) -> quote key + ":" + schema value)
            |> String.concat ","

        let roots =
            Api.all
            |> List.map (fun endpoint ->
                quote (Api.id endpoint) + ":" + schema endpoint.OutputSchema)
            |> String.concat ","

        "const responseDefinitions: Readonly<Record<string,ResponseSchema>> = {"
        + definitions
        + "};\n"
        + "const responseRoots: Readonly<Record<EndpointId,ResponseSchema>> = {"
        + roots
        + "};\n"
        + """
// Structural admission preserves exact strings. Interpretation/correlation/ownership remain consumer checks.
// JSON.parse has already lost duplicate-member and numeric-lexeme evidence.
type ResponseSchema =
  | readonly ["jsonObject"]
  | readonly [
      "object",
      Readonly<Record<string, ResponseSchema>>,
      ReadonlyArray<string>,
    ]
  | readonly ["array", ResponseSchema, number, number]
  | readonly [
      "string",
      string | null,
      string | null,
      number | null,
      number | null,
      number | null,
    ]
  | readonly ["integer", number, number]
  | readonly ["boolean"]
  | readonly ["null"]
  | readonly ["const", string]
  | readonly ["oneOf", ReadonlyArray<ResponseSchema>]
  | readonly ["ref", string]
  | readonly [
      "partition",
      ResponseSchema,
      ReadonlyArray<string>,
      string,
      string,
      string,
    ];
type Admission = (schema: ResponseSchema, value: unknown) => boolean;
const responseObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const responseDate = (value: string): boolean => {
  const parts = /^([0-9]{4})-([0-9]{2})-([0-9]{2})$/u.exec(value);
  if (parts === null) {
    return false;
  }
  const year = Number(parts[1]);
  const month = Number(parts[2]);
  const day = Number(parts[3]);
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const maximum = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][
    month - 1
  ];
  return year >= 1 && maximum !== undefined && day >= 1 && day <= maximum;
};
const responseFormat = (format: string | null, value: string): boolean => {
  switch (format) {
    case null:
      return true;
    case "uuid":
      return (
        /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(
          value,
        ) && value !== "00000000-0000-0000-0000-000000000000"
      );
    case "date":
      return responseDate(value);
    case "date-time": {
      const match =
        /^([0-9]{4}-[0-9]{2}-[0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})\.[0-9]{6,7}Z$/u.exec(
          value,
        );
      return (
        match !== null &&
        responseDate(match[1] ?? "") &&
        Number(match[2]) < 24 &&
        Number(match[3]) < 60 &&
        Number(match[4]) < 60
      );
    }
    default:
      return false;
  }
};
const responsePatterns = new Map<string, RegExp>();
const responseMatch = (pattern: string, value: string): boolean => {
  let compiled = responsePatterns.get(pattern);
  if (compiled === undefined) {
    compiled = new RegExp(pattern, "u");
    responsePatterns.set(pattern, compiled);
  }
  const match = compiled.exec(value);
  return (
    match !== null && match.index === 0 && match[0].length === value.length
  );
};
const responseStringBounds = (
  schema: Extract<ResponseSchema, { readonly 0: "string" }>,
  value: string,
): boolean => {
  const scalarCount = Array.from(value).length;
  return (
    (schema[3] === null || scalarCount >= schema[3]) &&
    (schema[4] === null || scalarCount <= schema[4]) &&
    (schema[5] === null || new TextEncoder().encode(value).length <= schema[5])
  );
};
const responseString = (
  schema: Extract<ResponseSchema, { readonly 0: "string" }>,
  value: unknown,
): boolean =>
  typeof value === "string" &&
  isRepresentable(value) &&
  responseStringBounds(schema, value) &&
  (schema[1] === null || responseMatch(schema[1], value)) &&
  responseFormat(schema[2], value);
const responseMembers = (
  schema: Extract<ResponseSchema, { readonly 0: "object" }>,
  value: unknown,
  child: Admission,
): boolean =>
  responseObject(value) &&
  Object.keys(value).every((key) => Object.hasOwn(schema[1], key)) &&
  Object.entries(schema[1]).every(([key, shape]) =>
    Object.hasOwn(value, key)
      ? child(shape, value[key])
      : schema[2].includes(key),
  );
const responseArray = (
  schema: Extract<ResponseSchema, { readonly 0: "array" }>,
  value: unknown,
  child: Admission,
): boolean =>
  Array.isArray(value) &&
  value.length >= schema[2] &&
  value.length <= schema[3] &&
  value.every((item) => child(schema[1], item));
const responseReference = (
  name: string,
  value: unknown,
  admit: Admission,
): boolean => {
  const shape = responseDefinitions[name];
  return shape !== undefined && admit(shape, value);
};
const responsePartition = (
  schema: Extract<ResponseSchema, { readonly 0: "partition" }>,
  value: unknown,
  admit: Admission,
): boolean => {
  if (!admit(schema[1], value) || !responseObject(value)) {
    return false;
  }
  const selected = value[schema[3]];
  const rejected = value[schema[4]];
  if (!Array.isArray(selected) || !Array.isArray(rejected)) {
    return false;
  }
  const selectedValues: ReadonlyArray<unknown> = selected;
  const rejectedValues: ReadonlyArray<unknown> = rejected;
  const members: unknown[] = [
    ...selectedValues,
    ...rejectedValues.map((item) =>
      responseObject(item) ? item[schema[5]] : undefined,
    ),
  ];
  return (
    members.length === schema[2].length &&
    new Set(members).size === members.length &&
    schema[2].every((token) => members.includes(token))
  );
};
const responseCompound = (
  schema: ResponseSchema,
  value: unknown,
  admit: Admission,
  child: Admission,
): boolean => {
  switch (schema[0]) {
    case "object":
      return responseMembers(schema, value, child);
    case "array":
      return responseArray(schema, value, child);
    case "oneOf":
      return schema[1].filter((shape) => admit(shape, value)).length === 1;
    case "ref":
      return responseReference(schema[1], value, admit);
    case "partition":
      return responsePartition(schema, value, admit);
    default:
      return false;
  }
};
const responseStructure = (
  schema: ResponseSchema,
  value: unknown,
  depth = 0,
): boolean => {
  if (depth > strictByteProfile.maximumDepth) {
    return false;
  }
  const admit: Admission = (shape, item) =>
    responseStructure(shape, item, depth);
  const child: Admission = (shape, item) =>
    responseStructure(shape, item, depth + 1);
  switch (schema[0]) {
    case "jsonObject":
      return responseObject(value);
    case "string":
      return responseString(schema, value);
    case "integer":
      return (
        typeof value === "number" &&
        Number.isSafeInteger(value) &&
        value >= schema[1] &&
        value <= schema[2]
      );
    case "boolean":
      return typeof value === "boolean";
    case "null":
      return value === null;
    case "const":
      return value === schema[1];
    default:
      return responseCompound(schema, value, admit, child);
  }
};
export const isEndpointResponse = <E extends EndpointId>(
  endpoint: E,
  value: unknown,
): value is ResponseByEndpoint[E] =>
  responseStructure(responseRoots[endpoint], value);
"""
