import { describe, expect, it } from "vitest";
import { blankColumn, columnRequest, sqlTypes, supportsIdentity, withType } from "./schema-draft";

describe("schema draft", () => {
  it("only offers IDENTITY for integer types", () => {
    expect(supportsIdentity("int4")).toBe(true);
    expect(supportsIdentity("int8")).toBe(true);
    for (const type of sqlTypes.filter((candidate) => candidate !== "int4" && candidate !== "int8")) {
      expect(supportsIdentity(type)).toBe(false);
    }
  });

  it("clears IDENTITY when the type changes to a non-integer", () => {
    const integer = { ...blankColumn(), type: "int8", identity: true };
    expect(withType(integer, "text").identity).toBe(false);
    expect(withType(integer, "int4").identity).toBe(true);
  });

  it("never sends IDENTITY and DEFAULT together", () => {
    const draft = { ...blankColumn(), name: " id ", type: "int8", identity: true, defaultText: "nextval('x')" };
    expect(columnRequest(draft)).toEqual({
      name: "id",
      type: "int8",
      nullable: true,
      primaryKey: false,
      unique: false,
      identity: true
    });
  });

  it("sends a trimmed default when IDENTITY is off", () => {
    const draft = { ...blankColumn(), name: " id ", type: "uuid", defaultText: "  gen_random_uuid()  " };
    expect(columnRequest(draft)).toMatchObject({ name: "id", default: "gen_random_uuid()" });
    expect(columnRequest(draft)).not.toHaveProperty("identity");
  });
});
