// vitest-specific subpath (not the bare "@testing-library/jest-dom" import): jest-dom's default
// export augments Jest's `expect`; vitest 5's `Assertion` type shape needs this entry instead, or
// `.toBeInTheDocument()` etc. type-check as missing even though the matchers work at runtime
// (jest-dom's runtime `expect.extend()` doesn't care which import path was used — only the type
// augmentation does).
import "@testing-library/jest-dom/vitest";
