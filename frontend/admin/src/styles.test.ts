import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const css = readFileSync(new URL("./styles.css", import.meta.url), "utf8");

describe("app shell layout", () => {
  it("uses one column for Projects and two only inside a project", () => {
    expect(css).toMatch(/\.app__body\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
    expect(css).toMatch(
      /\.app:not\(\[data-section="projects"\]\) \.app__body\s*\{\s*grid-template-columns:\s*15rem minmax\(0,\s*1fr\)/
    );
  });

  it("keeps a single column on narrow viewports", () => {
    expect(css).toMatch(
      /@media \(max-width: 900px\)[\s\S]*?\.app__body,\s*\.app:not\(\[data-section="projects"\]\) \.app__body\s*\{\s*grid-template-columns:\s*minmax\(0,\s*1fr\)/
    );
  });
});
