import { DomiBaseError } from "./errors.js";
import type { HttpTransport, WireRequest } from "./transport.js";
import type { DomiBaseResponse, FilterValue, OrderOptions, Row } from "./types.js";

type Operation = "select" | "insert" | "update" | "delete";

type RowMode = "list" | "single" | "maybeSingle";

interface QueryState<TRow extends Row> {
  operation: Operation | null;
  select: string[] | null;
  filters: string[];
  order: string[];
  limit: number | null;
  offset: number | null;
  insertValues: Partial<TRow> | Partial<TRow>[] | null;
  updateValues: Partial<TRow> | null;
  signal: AbortSignal | undefined;
}

const MAX_SELECT_COLUMNS = 50;
const MAX_FILTERS = 10;
const MAX_IN_VALUES = 100;
const MAX_ORDER_COLUMNS = 3;
const MAX_LIMIT = 1000;
const MAX_OFFSET = 1_000_000;
const MAX_INSERT_ROWS = 100;
const MAX_IDENTIFIER_LENGTH = 63;

const METHODS: Record<Operation, WireRequest["method"]> = {
  select: "GET",
  insert: "POST",
  update: "PATCH",
  delete: "DELETE"
};

export interface QueryBuilder<TRow extends Row = Row> extends PromiseLike<DomiBaseResponse<TRow[]>> {
  select(columns?: string): QueryBuilder<TRow>;

  insert(values: Partial<TRow> | readonly Partial<TRow>[]): QueryBuilder<TRow>;

  update(values: Partial<TRow>): QueryBuilder<TRow>;

  delete(): QueryBuilder<TRow>;

  eq(column: string, value: FilterValue): QueryBuilder<TRow>;

  neq(column: string, value: FilterValue): QueryBuilder<TRow>;

  gt(column: string, value: FilterValue): QueryBuilder<TRow>;

  gte(column: string, value: FilterValue): QueryBuilder<TRow>;

  lt(column: string, value: FilterValue): QueryBuilder<TRow>;

  lte(column: string, value: FilterValue): QueryBuilder<TRow>;

  like(column: string, pattern: string): QueryBuilder<TRow>;

  ilike(column: string, pattern: string): QueryBuilder<TRow>;

  in(column: string, values: readonly FilterValue[]): QueryBuilder<TRow>;

  is(column: string, value: null | boolean): QueryBuilder<TRow>;

  order(column: string, options?: OrderOptions): QueryBuilder<TRow>;

  limit(count: number): QueryBuilder<TRow>;

  range(from: number, to: number): QueryBuilder<TRow>;

  abortSignal(signal: AbortSignal): QueryBuilder<TRow>;

  single(): PromiseLike<DomiBaseResponse<TRow>>;

  maybeSingle(): PromiseLike<DomiBaseResponse<TRow>>;
}

/**
 * Immutable query builder. Every method returns a new builder, so a base
 * `db.from("table")` can be reused safely.
 */
export class QueryBuilderImpl<TRow extends Row> implements QueryBuilder<TRow> {
  constructor(
    private readonly transport: HttpTransport,
    private readonly table: string,
    private readonly state: QueryState<TRow>
  ) {}

  static create<TRow extends Row>(transport: HttpTransport, table: string): QueryBuilderImpl<TRow> {
    return new QueryBuilderImpl<TRow>(transport, table, {
      operation: null,
      select: null,
      filters: [],
      order: [],
      limit: null,
      offset: null,
      insertValues: null,
      updateValues: null,
      signal: undefined
    });
  }

  select(columns?: string): QueryBuilderImpl<TRow> {
    const parsed = parseColumns(columns);

    return this.next((state) => {
      state.select = parsed;

      if (state.operation === null) {
        state.operation = "select";
      }
    });
  }

  insert(values: Partial<TRow> | readonly Partial<TRow>[]): QueryBuilderImpl<TRow> {
    const isBulk = Array.isArray(values);
    const rows: Partial<TRow>[] = isBulk
      ? [...(values as readonly Partial<TRow>[])]
      : [values as Partial<TRow>];

    if (rows.length === 0) {
      throw invalidArgument("insert requires at least one row.");
    }

    if (rows.length > MAX_INSERT_ROWS) {
      throw invalidArgument(`insert accepts at most ${MAX_INSERT_ROWS} rows.`);
    }

    for (const row of rows) {
      if (!isRecord(row) || Object.keys(row).length === 0) {
        throw invalidArgument("insert requires non-empty objects.");
      }
    }

    return this.next((state) => {
      state.operation = "insert";
      state.insertValues = isBulk
        ? rows
        : ({ ...(rows[0] as Partial<TRow>) } as Partial<TRow>);
    });
  }

  update(values: Partial<TRow>): QueryBuilderImpl<TRow> {
    if (!isRecord(values) || Object.keys(values).length === 0) {
      throw invalidArgument("update requires at least one column.");
    }

    return this.next((state) => {
      state.operation = "update";
      state.updateValues = { ...values };
    });
  }

  delete(): QueryBuilderImpl<TRow> {
    return this.next((state) => {
      state.operation = "delete";
    });
  }

  eq(column: string, value: FilterValue): QueryBuilderImpl<TRow> {
    return this.addFilter(column, "eq", value);
  }

  neq(column: string, value: FilterValue): QueryBuilderImpl<TRow> {
    return this.addFilter(column, "neq", value);
  }

  gt(column: string, value: FilterValue): QueryBuilderImpl<TRow> {
    return this.addFilter(column, "gt", value);
  }

  gte(column: string, value: FilterValue): QueryBuilderImpl<TRow> {
    return this.addFilter(column, "gte", value);
  }

  lt(column: string, value: FilterValue): QueryBuilderImpl<TRow> {
    return this.addFilter(column, "lt", value);
  }

  lte(column: string, value: FilterValue): QueryBuilderImpl<TRow> {
    return this.addFilter(column, "lte", value);
  }

  like(column: string, pattern: string): QueryBuilderImpl<TRow> {
    return this.addFilter(column, "like", requirePattern(column, pattern));
  }

  ilike(column: string, pattern: string): QueryBuilderImpl<TRow> {
    return this.addFilter(column, "ilike", requirePattern(column, pattern));
  }

  in(column: string, values: readonly FilterValue[]): QueryBuilderImpl<TRow> {
    if (!Array.isArray(values) || values.length === 0) {
      throw invalidArgument("in requires at least one value.");
    }

    if (values.length > MAX_IN_VALUES) {
      throw invalidArgument(`in accepts at most ${MAX_IN_VALUES} values.`);
    }

    const serialized = values.map((value) => serializeFilterValue(value));

    if (serialized.some((item) => item.includes(","))) {
      throw invalidArgument("in values must not contain commas.");
    }

    return this.addFilter(column, "in", serialized.join(","));
  }

  is(column: string, value: null | boolean): QueryBuilderImpl<TRow> {
    if (value !== null && typeof value !== "boolean") {
      throw invalidArgument("is only accepts null, true or false.");
    }

    const text = value === null ? "null" : value ? "true" : "false";
    return this.addFilter(column, "is", text);
  }

  order(column: string, options: OrderOptions = {}): QueryBuilderImpl<TRow> {
    if (this.state.order.length >= MAX_ORDER_COLUMNS) {
      throw invalidArgument(`order accepts at most ${MAX_ORDER_COLUMNS} columns.`);
    }

    const direction = options.ascending === false ? "desc" : "asc";
    const entry = `${normalizeIdentifier(column, "column")}.${direction}`;

    return this.next((state) => {
      state.order.push(entry);
    });
  }

  limit(count: number): QueryBuilderImpl<TRow> {
    assertInteger(count, 1, MAX_LIMIT, "limit");

    return this.next((state) => {
      state.limit = count;
    });
  }

  range(from: number, to: number): QueryBuilderImpl<TRow> {
    assertInteger(from, 0, MAX_OFFSET, "range start");
    assertInteger(to, 0, MAX_OFFSET, "range end");

    if (to < from) {
      throw invalidArgument("range end must be greater than or equal to its start.");
    }

    const size = to - from + 1;
    if (size > MAX_LIMIT) {
      throw invalidArgument(`range cannot request more than ${MAX_LIMIT} rows.`);
    }

    return this.next((state) => {
      state.offset = from;
      state.limit = size;
    });
  }

  abortSignal(signal: AbortSignal): QueryBuilderImpl<TRow> {
    if (!isAbortSignal(signal)) {
      throw invalidArgument("abortSignal requires an AbortSignal instance.");
    }

    return this.next((state) => {
      state.signal = signal;
    });
  }

  single(): PromiseLike<DomiBaseResponse<TRow>> {
    return this.runSingle("single");
  }

  maybeSingle(): PromiseLike<DomiBaseResponse<TRow>> {
    return this.runSingle("maybeSingle");
  }

  then<TResult1 = DomiBaseResponse<TRow[]>, TResult2 = never>(
    onfulfilled?: ((value: DomiBaseResponse<TRow[]>) => TResult1 | PromiseLike<TResult1>) | null,
    onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null
  ): Promise<TResult1 | TResult2> {
    return this.execute().then(onfulfilled, onrejected);
  }

  private async runSingle(mode: Exclude<RowMode, "list">): Promise<DomiBaseResponse<TRow>> {
    const result = await this.execute();

    if (result.data === null || result.error !== null) {
      return { data: null, error: result.error, count: result.count ?? null };
    }

    const rows = result.data;

    if (rows.length === 1) {
      return { data: rows[0] as TRow, error: null, count: result.count ?? 1 };
    }

    if (mode === "maybeSingle" && rows.length === 0) {
      return { data: null, error: null, count: result.count ?? 0 };
    }

    return {
      data: null,
      error: new DomiBaseError({
        code: "SINGLE_ROW_ERROR",
        message:
          rows.length === 0
            ? "Expected exactly one row but received none."
            : `Expected exactly one row but received ${rows.length}.`
      }),
      count: rows.length
    };
  }

  private async execute(): Promise<DomiBaseResponse<TRow[]>> {
    const operation = this.state.operation;

    if (operation === null) {
      return fail("INVALID_QUERY", "Call select, insert, update or delete before executing.");
    }

    if ((operation === "update" || operation === "delete") && this.state.filters.length === 0) {
      return fail(
        "FILTER_REQUIRED",
        "At least one filter is required for updates and deletes."
      );
    }

    const query = new URLSearchParams();

    if (this.state.select !== null) {
      query.set("select", this.state.select.join(","));
    }

    for (const filter of this.state.filters) {
      query.append("f", filter);
    }

    if (this.state.order.length > 0) {
      query.set("order", this.state.order.join(","));
    }

    if (this.state.limit !== null) {
      query.set("limit", String(this.state.limit));
    }

    if (this.state.offset !== null) {
      query.set("offset", String(this.state.offset));
    }

    const request: WireRequest = {
      method: METHODS[operation],
      path: `/rest/v1/${encodeURIComponent(this.table)}`,
      query
    };

    if (operation === "insert" && this.state.insertValues !== null) {
      request.body = this.state.insertValues;
    }

    if (operation === "update" && this.state.updateValues !== null) {
      request.body = this.state.updateValues;
    }

    if (this.state.signal !== undefined) {
      request.signal = this.state.signal;
    }

    const result = await this.transport.send<TRow[]>(request);

    if (result.error === null && !Array.isArray(result.data)) {
      return fail("INVALID_RESPONSE", "The server response did not contain a row list.");
    }

    return result;
  }

  private addFilter(column: string, operator: string, value: FilterValue | string): QueryBuilderImpl<TRow> {
    if (this.state.filters.length >= MAX_FILTERS) {
      throw invalidArgument(`At most ${MAX_FILTERS} filters are allowed.`);
    }

    const text = typeof value === "string" ? value : serializeFilterValue(value);
    const filter = `${normalizeIdentifier(column, "column")}.${operator}.${text}`;

    return this.next((state) => {
      state.filters.push(filter);
    });
  }

  private next(mutate: (state: QueryState<TRow>) => void): QueryBuilderImpl<TRow> {
    const state: QueryState<TRow> = {
      operation: this.state.operation,
      select: this.state.select === null ? null : [...this.state.select],
      filters: [...this.state.filters],
      order: [...this.state.order],
      limit: this.state.limit,
      offset: this.state.offset,
      insertValues: this.state.insertValues,
      updateValues: this.state.updateValues,
      signal: this.state.signal
    };

    mutate(state);

    return new QueryBuilderImpl<TRow>(this.transport, this.table, state);
  }
}

function parseColumns(columns: string | undefined): string[] | null {
  if (columns === undefined) {
    return null;
  }

  const parts = columns.split(",").map((column) => column.trim());

  if (parts.length === 0 || parts.some((column) => column.length === 0)) {
    throw invalidArgument("select requires a non-empty, comma-separated column list.");
  }

  if (parts.length > MAX_SELECT_COLUMNS) {
    throw invalidArgument(`select accepts at most ${MAX_SELECT_COLUMNS} columns.`);
  }

  if (new Set(parts).size !== parts.length) {
    throw invalidArgument("select cannot contain duplicate columns.");
  }

  for (const column of parts) {
    normalizeIdentifier(column, "column");
  }

  return parts;
}

function normalizeIdentifier(value: string, kind: "table" | "column"): string {
  if (typeof value !== "string") {
    throw invalidArgument(`${kind} must be a string.`);
  }

  const trimmed = value.trim();

  if (trimmed.length === 0 || trimmed.length > MAX_IDENTIFIER_LENGTH) {
    throw invalidArgument(`${kind} is invalid.`);
  }

  // Dots and commas separate the wire format; control characters never belong in an identifier.
  if (/[.,\u0000-\u001f]/.test(trimmed)) {
    throw invalidArgument(`${kind} '${trimmed}' is invalid.`);
  }

  return trimmed;
}

function serializeFilterValue(value: FilterValue): string {
  if (value === null || value === undefined) {
    throw invalidArgument("Null filters require is(column, null).");
  }

  if (value instanceof Date) {
    if (Number.isNaN(value.getTime())) {
      throw invalidArgument("Date filter values must be valid dates.");
    }

    return value.toISOString();
  }

  if (typeof value === "number") {
    if (!Number.isFinite(value)) {
      throw invalidArgument("Numeric filter values must be finite.");
    }

    return String(value);
  }

  if (typeof value === "boolean") {
    return value ? "true" : "false";
  }

  return value;
}

function requirePattern(column: string, pattern: string): string {
  if (typeof pattern !== "string") {
    throw invalidArgument(`like/ilike on '${column}' requires a string pattern.`);
  }

  return pattern;
}

function assertInteger(value: number, min: number, max: number, name: string): void {
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max) {
    throw invalidArgument(`${name} must be an integer between ${min} and ${max}.`);
  }
}

function isAbortSignal(value: unknown): value is AbortSignal {
  return (
    typeof value === "object" &&
    value !== null &&
    typeof (value as AbortSignal).aborted === "boolean" &&
    typeof (value as AbortSignal).addEventListener === "function"
  );
}

function invalidArgument(message: string): DomiBaseError {
  return new DomiBaseError({ code: "INVALID_ARGUMENT", message });
}

function fail(code: string, message: string): DomiBaseResponse<never> {
  return { data: null, error: new DomiBaseError({ code, message }), count: null };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
