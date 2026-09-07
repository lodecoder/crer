import type { Step } from "./types.ts";

export class ExecutionContext {
  stopped = false;
  #callStack: string[] = [];

  stop() {
    this.stopped = true;
  }

  enterFunction(name: string): () => void {
    if (this.#callStack.includes(name)) {
      throw new Error(`recursive function call: ${[...this.#callStack, name].join(" -> ")}`);
    }
    this.#callStack.push(name);
    return () => {
      const exited = this.#callStack.pop();
      if (exited !== name) throw new Error("function call stack is unbalanced");
    };
  }
}

export type ExecuteChildren<Cache> = (
  steps: Step[],
  parentIndex?: string,
  initialCache?: Cache,
) => Promise<void>;

export type StepFrame<Cache> = {
  step: Step;
  index: string;
  cachedTemplate?: Cache;
};

/** Owns recursive traversal, stable step indexes, stop state, and one-child template cache scope. */
export class StepExecutor<Cache> {
  constructor(
    readonly context: ExecutionContext,
    private readonly handler: (
      frame: StepFrame<Cache>,
      executeChildren: ExecuteChildren<Cache>,
    ) => Promise<void>,
  ) {}

  async execute(
    steps: Step[],
    parentIndex = "",
    initialCache?: Cache,
  ): Promise<void> {
    let nextCache = initialCache;
    for (const [offset, step] of steps.entries()) {
      if (this.context.stopped) return;
      const cachedTemplate = nextCache;
      nextCache = undefined;
      const index = parentIndex ? `${parentIndex}.${offset}` : String(offset);
      await this.handler(
        { step, index, cachedTemplate },
        (children, childParent = index, cache) => this.execute(children, childParent, cache),
      );
    }
  }
}
