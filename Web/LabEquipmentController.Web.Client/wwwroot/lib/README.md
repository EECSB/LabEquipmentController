# Vendored third-party libraries

Checked in on purpose. They are served as-is at runtime: there is no bundler and no `npm install`
in the build, because `dotnet build` is the whole build and a bench PC that can run the desktop app
should be able to run this one without a Node toolchain. Record the version here whenever one is
added or updated — there is no `package.json` to read it from.

The e2e tests under `Web/tests/` *do* use npm, which is fine: nothing it installs reaches `wwwroot`
or the build. Do not let a dependency cross that line.

| Library | Version | Files | Licence | Source |
|---|---|---|---|---|
| Monaco Editor | 0.41.0 | `monaco/vs/` | MIT | the `monaco-editor` npm package, `min/vs/` (the AMD distribution) |

## Monaco Editor

The script editors use it — syntax colouring for the script language, and completion that comes from
`Core.ScriptLanguage` rather than from a word list kept here.

Only the seven files the app actually needs are vendored:

| File | Why |
|---|---|
| `vs/loader.js` | Monaco's AMD loader |
| `vs/editor/editor.main.js` | the editor itself |
| `vs/editor/editor.main.css` | editor styles |
| `vs/editor/editor.main.nls.js` | the default (English) UI strings. **Required** — the loader derives this path from the bundle name at runtime, so it never appears as a literal string in `editor.main.js`. Leaving it out fails the whole load with "Failed trying to load default language strings" |
| `vs/base/browser/ui/codicons/codicon/codicon.ttf` | icon font, referenced by `editor.main.css` |
| `vs/base/worker/workerMain.js` | the editor web worker |
| `vs/base/common/worker/simpleWorker.nls.js` | the worker's counterpart to `editor.main.nls.js` |

Deliberately **not** vendored:

- `vs/basic-languages/**` — the app registers its own `lecscript` Monarch grammar in
  [`../js/monaco.js`](../js/monaco.js) and uses no built-in language. Note that `editor.main.js`
  carries lazy-load stubs for every built-in language, so selecting one would 404 unless its grammar
  file were vendored too.
- `vs/language/**` — the JSON/CSS/HTML/TypeScript language services and their workers. Unused.
- the localized `*.nls.*.js` files — English only.

To update: `npm view monaco-editor version`, install that version into a scratch folder, copy the
seven files above out of its `min/vs/`, and update the table. Then open a script editor and watch the
browser console — a missing file shows up there as a loader error, not as a build failure.
