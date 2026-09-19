//Monaco, self-hosted: no npm, no bundler, and no CDN.
//
//The seven files it needs are vendored under wwwroot/lib/monaco — see the README beside them. The AMD
//loader is injected lazily on first use, after the app's own scripts have attached to window, so
//Monaco's global `define`/`require` cannot take them over.
//
//**Why Monaco at all.** The desktop's script editors colour what you type: comments green, keywords
//blue, an alias purple, a captured name orange (ScriptLanguage.Tokenize, and ScriptEditor.ColorFor).
//A <textarea> cannot do that, and the port had been shipping one. The same window also completes —
//ScriptLanguage.Complete knows the keywords, the snippets, the aliases a sequence has declared and
//the names a capture has bound — and that is asked of the server here rather than reimplemented.
(function () {
    'use strict';

    ///The language id. One grammar for both dialects: the sequence words are a superset, and colouring
    ///DEVICE in a single-instrument script as a keyword is a smaller wrong than not colouring it in a
    ///sequence.
    const LANGUAGE = 'lecscript';

    ///The editors on the page, by the element they were created in. Two can exist at once — a console's
    ///script window over the bench while the multi-instrument page is behind it — so nothing here keeps
    ///"the" editor.
    const editors = new Map();

    let loading = null;
    let registered = false;

    function baseUrl() {
        let href = document.baseURI;
        return href.endsWith('/') ? href : href + '/';
    }

    function vsUrl() {
        return baseUrl() + 'lib/monaco/vs';
    }

    function loadScript(src) {
        return new Promise(function (resolve, reject) {
            const el = document.createElement('script');
            el.src = src;
            el.onload = resolve;
            el.onerror = () => reject(new Error('could not load ' + src));
            document.head.appendChild(el);
        });
    }

    ///Load the loader and the editor core, exactly once however many editors are asked for.
    function ensureMonaco() {
        if (loading) return loading;

        const vs = vsUrl();

        //The worker is fetched same-origin through a blob that importScripts the real one with the
        //right base — the file itself is under our own origin, and a worker created from a URL on a
        //different one would be blocked.
        window.MonacoEnvironment = {
            getWorkerUrl: function () {
                const js = "self.MonacoEnvironment={baseUrl:'" + baseUrl() + "lib/monaco/'};"
                         + "importScripts('" + vs + "/base/worker/workerMain.js');";
                return URL.createObjectURL(new Blob([js], { type: 'text/javascript' }));
            }
        };

        loading = loadScript(vs + '/loader.js').then(function () {
            return new Promise(function (resolve) {
                window.require.config({ paths: { vs: vs } });
                window.require(['vs/editor/editor.main'], resolve);
            });
        });

        return loading;
    }

    ///
    ///The grammar, following Core's ScriptLanguage.Tokenize rather than inventing one.
    ///
    ///That method decides the desktop's colours line by line: a comment is the whole line; a line whose
    ///first word is a keyword is a keyword line, and the words after it that are inner keywords are
    ///keywords too; anything else is a SCPI command, left plain-ish on purpose because it is the point
    ///of the line. Then `-> name` binds a capture, numbers are numbers, and `$name` wins over whatever
    ///it is inside.
    ///
    ///Monarch is a state machine over one line at a time, which suits that exactly.
    ///
    function registerLanguage() {
        if (registered) return;
        registered = true;

        monaco.languages.register({ id: LANGUAGE });

        monaco.languages.setLanguageConfiguration(LANGUAGE, {
            comments: { lineComment: '#' },
            brackets: [['(', ')']],
            //REPEAT, WITH and FOR all end with END, so a new line inside one starts indented.
            onEnterRules: [{
                beforeText: /^\s*(REPEAT|WITH|FOR)\b.*$/i,
                action: { indentAction: monaco.languages.IndentAction.Indent }
            }]
        });

        monaco.languages.setMonarchTokensProvider(LANGUAGE, {
            ignoreCase: true,

            //ScriptLanguage.Keywords and SequenceKeywords, together. Held here rather than fetched
            //because a grammar has to be registered before the first character is drawn, and a list
            //that arrived late would leave the first paint uncoloured.
            keywords: [
                'DELAY', 'WAIT', 'PRINT', 'ECHO', 'LOG', 'REPEAT', 'END',
                'DEVICE', 'WITH', 'FOR', 'RECORD', 'COLUMNS'
            ],

            //The words that only mean something inside a FOR: FOR f = 100 TO 100k POINTS 40 LOG.
            inner: ['TO', 'STEP', 'POINTS', 'LOG'],

            tokenizer: {
                //
                //One state, and no state at all between lines.
                //
                //Monarch tokenizes a line at a time and carries its state stack into the next, and it
                //stops the moment a line is consumed — so a rule matching end-of-line never fires, and
                //there is no reliable way to say "every line starts fresh". A line that ended inside a
                //keyword's arguments left the next line's first word being read as an argument.
                //
                //Tokenize does not need states either: it is handed one line, decides from its first
                //word what kind of line it is, and picks out the same few things wherever they appear.
                //Anchoring on ^ says the same thing here.
                //
                root: [
                    //A comment is the rest of the line, either spelling, wherever it starts.
                    [/(#|\/\/).*$/, 'comment'],

                    //DEVICE and WITH name an instrument. Coloured as one so the eye can follow an alias
                    //from where it is declared to where it is used — and only here, because only here is
                    //it certain: `MEASure:VOLTage:DC?` has the same shape as `gen:` further along, and
                    //Tokenize tells them apart by checking the aliases the script declared, which a
                    //line-at-a-time state machine cannot do.
                    [/^(\s*)(DEVICE|WITH)(\s+)([A-Za-z_]\w*)/, ['white', 'keyword', 'white', 'alias']],

                    //The first word decides the line. A keyword is coloured; anything else is SCPI, and
                    //SCPI is left alone — ScriptEditor.ColorFor gives Command no colour of its own,
                    //because it is the point of the line rather than an annotation on it.
                    [/^(\s*)([A-Za-z_]\w*)/, ['white', { cases: { '@keywords': 'keyword', '@default': '' } }]],

                    //`-> name` binds the reply to a name, and both halves are worth seeing.
                    [/(->)(\s*)([A-Za-z_]\w*)/, ['operator', 'white', 'variable']],

                    //A $name wins wherever it appears, including inside a SCPI argument.
                    [/\$[A-Za-z_]\w*/, 'variable'],

                    //The words that only mean something inside a FOR: FOR f = 100 TO 100k POINTS 40 LOG.
                    [/\b(TO|STEP|POINTS|LOG)\b/, 'keyword'],

                    [/[-+]?\d+(\.\d+)?([eE][-+]?\d+)?/, 'number'],
                    [/[ \t]+/, 'white'],
                    [/./, '']
                ]
            }
        });

        //The desktop's own colours, out of ScriptEditor.ColorFor. Two themes because this app has two,
        //and the light one's blue on a dark console would be unreadable — same hues, lifted.
        monaco.editor.defineTheme('lec-light', {
            base: 'vs',
            inherit: true,
            rules: [
                { token: 'comment', foreground: '008040' },
                { token: 'keyword', foreground: '0000C8' },
                { token: 'alias', foreground: '8C008C' },
                { token: 'variable', foreground: 'B45000' },
                { token: 'number', foreground: '6E5A00' },
                { token: 'operator', foreground: '787878' },
                { token: 'command', foreground: '14161A' }
            ],
            colors: {}
        });

        monaco.editor.defineTheme('lec-dark', {
            base: 'vs-dark',
            inherit: true,
            rules: [
                { token: 'comment', foreground: '4EC98A' },
                { token: 'keyword', foreground: '7FA6FF' },
                { token: 'alias', foreground: 'D98CD9' },
                { token: 'variable', foreground: 'E0A060' },
                { token: 'number', foreground: 'D6C06A' },
                { token: 'operator', foreground: '9AA4B2' },
                { token: 'command', foreground: 'E7EAEE' }
            ],
            colors: {}
        });

        //
        //Suggestions, from the server.
        //
        //ScriptLanguage.Complete is the one that knows what can come next — including the aliases this
        //script has declared and the names it has captured, which no fixed list could. Registered once;
        //which dialect and which instrument are read off the editor being completed, so one provider
        //serves every editor on the page.
        //
        monaco.languages.registerCompletionItemProvider(LANGUAGE, {
            triggerCharacters: ['$', ' '],
            provideCompletionItems: async function (model, position) {
                const held = [...editors.values()].find((e) => e.editor.getModel() === model);
                const word = model.getWordUntilPosition(position);

                //`$` is not part of a word to Monaco, so a prefix starting with one has to be recovered
                //from the line — and it is the one character that changes what may be offered at all.
                const before = model.getValueInRange({
                    startLineNumber: position.lineNumber, startColumn: 1,
                    endLineNumber: position.lineNumber, endColumn: position.column
                });
                const dollar = /\$[\w]*$/.test(before);
                const prefix = (dollar ? '$' : '') + word.word;

                const range = {
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: word.startColumn - (dollar ? 1 : 0),
                    endColumn: word.endColumn
                };

                let offered = [];
                try {
                    const res = await fetch(baseUrl() + 'api/script/complete', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            script: model.getValue(),
                            prefix: prefix,
                            sequence: held ? held.sequence : true,
                            sessionId: held ? held.sessionId : null
                        })
                    });
                    if (res.ok) offered = await res.json();
                } catch {
                    //A server that is not answering has already been reported by the page; an editor
                    //with no suggestions is a worse editor, not a broken one.
                    return { suggestions: [] };
                }

                return {
                    suggestions: offered.map(function (c) {
                        const snippet = c.body != null && c.body.length > 0;
                        return {
                            label: c.text,
                            detail: c.detail,
                            kind: kindOf(c.kind),
                            //Core marks a snippet's holes with « », which is Monaco's ${1:name} said
                            //another way. Translated here rather than in Core, because the marks are
                            //the desktop editor's too and it reads them itself.
                            insertText: snippet ? toSnippet(c.body) : c.text,
                            insertTextRules: snippet
                                ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet
                                : undefined,
                            range: range
                        };
                    })
                };
            }
        });
    }

    ///
    ///Wait until the host has a box.
    ///
    ///A tool window renders its body and *then* opens, so an editor built at that moment is built inside
    ///a dialog that is still display:none. Monaco measures zero, and the view it sets up at that size
    ///never comes back: the model holds the right text and the pane stays blank, every line painted as
    ///one default token however the grammar tokenizes it. automaticLayout does not rescue it, and an
    ///explicit layout() afterwards only fixes the render that follows the next edit.
    ///
    ///So the editor is not created until there is something to create it in. The ceiling is there so a
    ///host that never gets a box — a component torn down while opening — gives up rather than waiting
    ///for the rest of the session.
    ///
    function whenSized(el) {
        if (el.clientHeight > 0 && el.clientWidth > 0) return Promise.resolve(true);

        return new Promise(function (resolve) {
            let done = false;
            const finish = (ok) => { if (done) return; done = true; watch.disconnect(); resolve(ok); };
            const watch = new ResizeObserver(() => {
                if (el.clientHeight > 0 && el.clientWidth > 0) finish(true);
            });
            watch.observe(el);
            setTimeout(() => finish(el.clientHeight > 0), 5000);
        });
    }

    ///
    ///The editor's two keys, taken in whichever script editor's window they are pressed in:
    ///**F5 runs** and **Ctrl+S saves**. ScriptForm and SequenceForm take both anywhere in the
    ///window, and the Run and Save buttons say so on hover.
    ///
    ///A browser has its own use for each, and here both are worse than a key doing nothing. F5
    ///reloads, which shuts the window and the script being written goes with it. Ctrl+S saves the
    ///page, which is this app's HTML and not the script. So the keys are taken inside an editor's
    ///window and left to the browser everywhere else: on the bench, which a reload does not
    ///disconnect, and in every window that is not an editor, including the AI window and the
    ///reference, which sit inside the editor that opened them. Ctrl+F5 is never taken, so there is
    ///still a reload from anywhere. On a Mac, Cmd+S is the save key, so it is taken as Ctrl+S is.
    ///
    ///Each goes to the handler its button calls, and the page decides whether anything happens. A
    ///second check here would be two checks, and two checks are how F5 on the desktop came to run a
    ///script whose Run button was greyed out.
    ///
    const keys = [
        { method: 'RunKey', is: (e) => e.key === 'F5' && !e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey },
        { method: 'SaveKey', is: (e) => (e.key === 's' || e.key === 'S') && (e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey }
    ];

    document.addEventListener('keydown', function (e) {
        const key = keys.find((k) => k.is(e));
        if (!key) return;

        const where = windowFor(e);
        for (const [host, held] of editors) {
            if (!host.isConnected || windowOf(host) !== where) continue;

            e.preventDefault();
            //Held down, it is one press, as holding a button down is one click. A held Ctrl+S would
            //otherwise be a download for every repeat.
            if (!e.repeat) held.ref.invokeMethodAsync(key.method);
            return;
        }
    }, true);

    ///The window a node is in: the innermost dialog around it, or null for the page under them all.
    function windowOf(node) {
        return node && node.closest ? node.closest('dialog') : null;
    }

    ///
    ///The windows around the last press, innermost first.
    ///
    ///A key belongs to the window with the focus, except when nothing has it. Run, pressed with the
    ///mouse, greys out as the run starts and the focus falls back to the page, and a window closing
    ///under the focus leaves it there too. A click on the log does not: the browser gives the focus to
    ///the window. A key pressed with nothing focused belongs to the window last pressed in. If that
    ///one has closed since (the AI window does, when its script is used), the key goes to the window
    ///it was open over.
    ///
    let pressed = [];

    document.addEventListener('mousedown', function (e) {
        pressed = [];
        for (let w = windowOf(e.target); w; w = windowOf(w.parentElement)) pressed.push(w);
    }, true);

    function windowFor(e) {
        const t = e.target;
        if (t && t !== document.body && t !== document.documentElement) return windowOf(t);
        return pressed.find((w) => w.isConnected && w.open) || null;
    }

    ///Core's kinds, in Monaco's vocabulary.
    function kindOf(kind) {
        switch (kind) {
            case 'Snippet': return monaco.languages.CompletionItemKind.Snippet;
            case 'Keyword': return monaco.languages.CompletionItemKind.Keyword;
            case 'Alias': return monaco.languages.CompletionItemKind.Variable;
            case 'Variable': return monaco.languages.CompletionItemKind.Variable;
            default: return monaco.languages.CompletionItemKind.Function;
        }
    }

    ///`«count»` becomes `${1:count}`, in the order they appear — which is the order Tab visits them,
    ///and the order ScriptSnippet.Placeholders reports for the desktop.
    function toSnippet(body) {
        let n = 0;
        return body.replace(/«([^»]*)»/g, function (_, name) {
            n += 1;
            return '${' + n + ':' + name + '}';
        });
    }

    ///Everything the app calls, under the name the rest of the interop already uses.
    window.lec = window.lec || {};
    window.lec.monaco = {
        ///
        ///Put an editor in `el` and hand its changes back to `ref.Typed(text)`.
        ///
        ///Idempotent per element: a dialog that opens, closes and opens again hands back the same host
        ///the second time, and creating a second editor in it would leave the first drawing underneath.
        ///
        create: async function (el, ref, text, sequence, sessionId, dark) {
            await ensureMonaco();
            registerLanguage();
            await whenSized(el);

            const existing = editors.get(el);
            if (existing) existing.editor.dispose();

            //Whatever was asked for while this was still being built wins over what the component was
            //holding when it started — it is the newer of the two.
            const pending = el.dataset.lecPending;
            delete el.dataset.lecPending;

            const editor = monaco.editor.create(el, {
                value: pending !== undefined ? pending : (text || ''),
                language: LANGUAGE,
                theme: dark ? 'lec-dark' : 'lec-light',
                //The console's own font, because a script is SCPI and SCPI is monospace everywhere else
                //in this app.
                fontFamily: 'ui-monospace, "Cascadia Mono", Consolas, monospace',
                fontSize: 12.5,
                automaticLayout: true,
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                renderLineHighlight: 'none',
                //A script is a column of short lines; wrapping one would hide that a line is long.
                wordWrap: 'off',
                tabSize: 4,
                insertSpaces: true,
                //Nothing here is a folder, and the gutter is width taken from a narrow panel.
                folding: false,
                lineNumbersMinChars: 3,
                scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 }
            });

            const held = { editor, ref, sequence: !!sequence, sessionId: sessionId || null, quiet: false };
            editors.set(el, held);

            //
            //**Paint it.**
            //
            //Monaco built inside a container that only just gained a box sets up its view and then does
            //not draw into it: getValue() answers correctly, `.view-lines` exists and is empty, and it
            //stays that way through every edit. One layout wakes it, and after that it behaves. Waiting
            //for the box before creating is not enough on its own — a dialog that has just been shown
            //has a box before it has finished being laid out.
            //
            //Twice, a frame apart, because which of those two moments the editor is ready for is not
            //something worth finding out the hard way a second time.
            //
            const paint = () => { editor.layout(); editor.render(true); };
            requestAnimationFrame(paint);
            setTimeout(paint, 60);

            //An editor whose host has left the document is one nobody can reach, and it is still
            //holding a model and a worker. Swept here rather than tracked, because the thing that
            //removed it was Blazor rebuilding a dialog body and it did not tell anyone.
            for (const [host, other] of [...editors]) {
                if (host !== el && !host.isConnected) {
                    other.editor.dispose();
                    editors.delete(host);
                }
            }

            editor.onDidChangeModelContent(function () {
                //Set from C# is not typing. Without this the round trip is: bind pushes a value in,
                //this fires, C# is told, the parameter changes, the value is pushed in again — and the
                //caret goes back to the start on every keystroke.
                if (held.quiet) return;
                ref.invokeMethodAsync('Typed', editor.getValue());
            });

            return true;
        },

        ///What is in it. Used on the way out, so a save cannot miss the last keystroke.
        get: function (el) {
            const held = editors.get(el);
            return held ? held.editor.getValue() : '';
        },

        ///
        ///Replace what is in it — an example loaded, a script the model wrote, New.
        ///
        ///Through the model's own edit rather than setValue, so undo still reaches back past it: an
        ///example loaded over work in progress is exactly the thing someone wants to undo.
        ///
        set: function (el, text) {
            const held = editors.get(el);

            //
            //Asked for before there was an editor to ask.
            //
            //Monaco has to be fetched and the host has to have a box, so `create` finishes some way
            //after the component that called it rendered — and anything the component does in between,
            //like New emptying the box, would land on nothing and be lost. The editor would then come
            //up holding whatever the value was when its render started, which is the value the user
            //has just replaced. Remembered on the element, and applied when it is built.
            //
            if (!held) {
                el.dataset.lecPending = text || '';
                return;
            }

            if (held.editor.getValue() === (text || '')) return;

            held.quiet = true;
            try {
                const model = held.editor.getModel();
                held.editor.executeEdits('lec', [{ range: model.getFullModelRange(), text: text || '' }]);
                held.editor.pushUndoStop();
            } finally {
                held.quiet = false;
            }
        },

        ///Write at the caret, which is what the Snippets menu does.
        insert: function (el, text) {
            const held = editors.get(el);
            if (!held) return;

            const selection = held.editor.getSelection();
            held.editor.executeEdits('lec', [{ range: selection, text: text, forceMoveMarkers: true }]);
            held.editor.focus();
        },

        ///Paint the example scripts in a language reference with the editor's own colouriser.
        ///
        ///The same grammar and the same theme the editor uses, so what is read in the reference
        ///looks like what is typed in the editor - which is what the desktop's ScriptReferenceForm
        ///does, colouring its examples with the tokenizer its own editor is built on.
        ///
        ///Each block is painted once. The source text is kept on the element so a block that has
        ///not changed can be recognised and left alone - Monaco replaces the innerHTML with spans,
        ///and painting an already-painted block would nest them - and so that a theme change can
        ///put the plain text back and paint it again in the other set of colours.
        colorizeIn: async function (root, dark) {
            if (!root) return;
            await ensureMonaco();
            registerLanguage();

            const theme = dark ? 'lec-dark' : 'lec-light';
            monaco.editor.setTheme(theme);

            //Twice on the same element destroys it. colorizeElement replaces the text with painted
            //spans and writes runs of spaces as non-breaking ones, so an element that has been
            //painted no longer reads back as what it was painted from - and a guard comparing the
            //two saw a changed example and painted the painted markup, which comes out empty. So
            //what is remembered is what the element says *after* painting: unchanged means leave it,
            //and anything that put the raw text back gets painted again.
            for (const el of root.querySelectorAll('pre.sample')) {
                const text = el.textContent;
                if (el.dataset.lecPainted === text) continue;
                el.dataset.lecSource = text;
                await monaco.editor.colorizeElement(el, { mimeType: LANGUAGE, theme: theme });
                el.dataset.lecPainted = el.textContent;
            }
        },

        ///Follow the app's theme when it changes under an open editor.
        theme: function (dark) {
            if (typeof monaco === 'undefined') return;
            monaco.editor.setTheme(dark ? 'lec-dark' : 'lec-light');

            //Painted examples carry the colours of the theme they were painted in, as inline
            //styles. Put the text back and paint it again in the new one.
            for (const el of document.querySelectorAll('pre.sample[data-lec-source]')) {
                el.textContent = el.dataset.lecSource;
                delete el.dataset.lecSource;
                delete el.dataset.lecPainted;
            }
            window.lec.monaco.colorizeIn(document.body, dark);
        },

        ///Let go. The host element goes away with the dialog, and an editor left behind holds its model,
        ///its worker and every listener it registered.
        destroy: function (el) {
            const held = editors.get(el);
            if (!held) return;
            held.editor.dispose();
            editors.delete(el);
        }
    };
})();
