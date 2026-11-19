/**
 * Error returned by the SDK. The shape matches the wire error envelope:
 * `{ code, message, details?, hint?, status? }`. No SQL, stack traces or
 * credentials are ever exposed through this type.
 */
export interface DomiBaseErrorShape {
  code: string;
  message: string;
  details?: string;
  hint?: string;
  status?: number;
}

export class DomiBaseError extends Error implements DomiBaseErrorShape {
  readonly code: string;

  readonly details?: string;

  readonly hint?: string;

  readonly status?: number;

  constructor(shape: DomiBaseErrorShape) {
    super(shape.message);
    this.name = "DomiBaseError";
    this.code = shape.code;

    if (shape.details !== undefined) {
      this.details = shape.details;
    }

    if (shape.hint !== undefined) {
      this.hint = shape.hint;
    }

    if (shape.status !== undefined) {
      this.status = shape.status;
    }
  }

  toJSON(): DomiBaseErrorShape {
    const shape: DomiBaseErrorShape = {
      code: this.code,
      message: this.message
    };

    if (this.details !== undefined) {
      shape.details = this.details;
    }

    if (this.hint !== undefined) {
      shape.hint = this.hint;
    }

    if (this.status !== undefined) {
      shape.status = this.status;
    }

    return shape;
  }
}
