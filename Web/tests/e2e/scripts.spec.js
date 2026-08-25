//
//The Script Editor, opened over the console it runs against.
//
//Not a page and not routable, deliberately: a script runs on one instrument, so "which one" has to be
//answered before it means anything - and the honest place to answer it is the console you opened it
//from, which has already answered it.
//
//The scripts here are small on purpose. What is being checked is the window - that the toolbar is the
//desktop's toolbar, that Run and Stop trade places, that the output and the recorded rows land where
//they should - and not the language, which is Core's and has its own xUnit suite.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane, boxOf, setScript, scriptText, scriptTokens } = require('./helpers');
const { startInstrument } = require('./instrument');

///A script that uses one of everything this window has to show: a message, a captured reply, a
///recorded row under a named column. ScriptRunner reads COLUMNS before the run so the table has its
///headings from the start rather than gaining them halfway through.
const SCRIPT = [
    'PRINT starting',
    'COLUMNS volts',
    'MEASure:VOLTage:DC? -> v',
    'RECORD $v',
    'PRINT done'
].join('\n');

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument();
});

test.afterAll(async () => {
    await instrument.stop();
});

///
///The tool window, open over a connected console.
///
///`> .tool-head` and not a descendant: the editor carries its own AI window inside it, closed, and a
///descendant selector finds that one's head too.
///
async function openEditor(page) {
    await pane(page).getByRole('button', { name: /Scripts/ }).click();
    await expect(page.locator('dialog.tool[open]')).toBeVisible();
    await expect(page.locator('dialog.tool[open] > .tool-head')).toContainText('Script Editor');
    return page.locator('dialog.tool[open]');
}

///The editor's host element. Monaco builds its own DOM in here — see setScript and scriptText for why
///nothing fills or reads it directly.
function editor(page) {
    return page.locator('dialog.tool[open] .code.monaco');
}

///The status line along the foot of the window, which says what the last thing to happen was. It
///was under the editor, inside the card with it, until the desktop was read more carefully:
///ScriptForm docks its label to the bottom of the *window*, under the log and the results both.
function status(page) {
    return page.locator('dialog.tool[open] .row.runstatus .muted');
}

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await connect(page, instrument.address);
    instrument.received.length = 0;
});

test.describe('the toolbar', () => {
    ///
    ///The desktop's toolbar, control for control.
    ///
    ///ScriptForm and SequenceForm are two windows with one toolbar between them; only New and Save As…
    ///differ, and only because SequenceForm has neither. This is the ScriptForm half, so both are here.
    ///
    test('carries the file pair, the three ways a script comes into being, and running it', async ({ page }) => {
        const tool = await openEditor(page);
        const bar = tool.locator('.scripttools');

        //Anchored: unanchored, "Save" matches Save As… as well, and a count of two would pass a check
        //that meant to find one of each.
        const names = [/^New$/, /^Open/, /^Save$/, /^Save As/, /^Snippets/, /^Script with AI/, /^Run$/, /^Stop$/];
        for (const name of names) {
            await expect(bar.getByRole('button', { name }), `${name} is not on the toolbar exactly once`)
                .toHaveCount(1);
        }

        //Examples is a picker rather than a button, as the desktop's is: it replaces what is in the
        //editor, and a button that silently discards your work is a button that should have been a list.
        await expect(bar.locator('select')).toContainText('Examples');
    });

    ///
    ///Four groups, with a wider gap between them than within them.
    ///
    ///The file pair; then the three ways a script arrives; then running it. Without the steps it
    ///reads as nine buttons in a row.
    ///
    ///Three groups, which is ScriptForm's own count: it puts a gap on the first control of each,
    ///and Script with AI is not one of them — "Snippets and Script with AI sit beside the examples,
    ///because all three answer the same question — where a script comes from when you do not have
    ///one yet."
    ///
    test('sets the three groups apart', async ({ page }) => {
        const tool = await openEditor(page);
        await expect(tool.locator('.scripttools .gap')).toHaveCount(2);

        const within = await page.evaluate(() => {
            const bar = document.querySelector('dialog.tool[open] .scripttools');
            const a = bar.children[0].getBoundingClientRect();
            const b = bar.children[1].getBoundingClientRect();
            return Math.round((b.left - a.right) * 10) / 10;
        });

        const between = await boxOf(page, 'dialog.tool[open] .scripttools .gap');
        expect(between.width).toBeGreaterThan(within);
    });

    ///
    ///Nothing to save once there is nothing to save.
    ///
    ///Emptied rather than found empty: the window opens with a script already in it, so New is what
    ///makes the empty case reachable at all.
    ///
    test('Save goes dead when the editor is emptied', async ({ page }) => {
        const tool = await openEditor(page);

        await tool.getByRole('button', { name: /^New$/ }).click();
        expect(await scriptText(page)).toBe('');

        await expect(tool.getByRole('button', { name: /^Save$/ })).toBeDisabled();
        await expect(tool.getByRole('button', { name: /^Save As/ })).toBeDisabled();

        await setScript(page, 'PRINT hello');
        await expect(tool.getByRole('button', { name: /^Save$/ })).toBeEnabled();
    });

    ///
    ///And nothing to stop until something is running.
    ///
    test('Stop is dead until a script is running', async ({ page }) => {
        const tool = await openEditor(page);

        await expect(tool.getByRole('button', { name: /^Stop$/ })).toBeDisabled();
        await expect(tool.getByRole('button', { name: /^Run$/ })).toBeEnabled();
    });
});

test.describe('writing a script', () => {
    ///
    ///The window opens with something in it rather than with a blank page.
    ///
    ///The desktop opens on a commented worked example - what a comment looks like, then PRINT, then a
    ///REPEAT with a DELAY in it - which teaches the language in the place where it is needed. The web
    ///opens on a single `*IDN?`. Both are a starting point rather than an empty box; that they are not
    ///the *same* starting point is recorded in UI-SPEC §9 as a difference, not asserted here.
    ///
    test('opens on a starting script rather than a blank page', async ({ page }) => {
        await openEditor(page);
        expect(await scriptText(page)).not.toBe('');
    });

    ///
    ///An example replaces what is in the editor, which is what its tooltip promises.
    ///
    test('an example replaces what is in the editor', async ({ page }) => {
        const tool = await openEditor(page);
        const before = await scriptText(page);

        const picker = tool.locator('.scripttools select');
        const name = await picker.locator('option').nth(1).getAttribute('value');
        await picker.selectOption(name);

        expect(await scriptText(page)).not.toBe(before);
        expect(await scriptText(page)).not.toBe('');
        await expect(status(page)).toContainText('Loaded example');
    });

    ///
    ///Snippets is the language reference in the place where it is needed: press one and it is written
    ///into the editor rather than described to you.
    ///
    test('a snippet is written into the editor', async ({ page }) => {
        const tool = await openEditor(page);
        await tool.getByRole('button', { name: /^New$/ }).click();
        expect(await scriptText(page)).toBe('');

        await tool.getByRole('button', { name: /^Snippets/ }).click();
        const menu = tool.locator('.menu[aria-label="Snippets"]');
        await expect(menu).toBeVisible();

        //And the way out of it, for the words it does not have room to explain.
        await expect(menu).toContainText('What all of this means');

        await menu.locator('.item').first().click();
        expect(await scriptText(page)).not.toBe('');
    });
});

test.describe('the editor itself', () => {
    ///
    ///It colours what is typed, which is the whole reason it is not a textarea.
    ///
    ///The desktop's editor paints comments green, keywords blue, a captured name orange — and the port
    ///had been shipping a plain box. What is asserted is that the tokens land in the classes the grammar
    ///names, not what colour those classes end up: the colours are in one place (js/monaco.js, out of
    ///ScriptEditor.ColorFor) and a spec repeating them would be a second copy to keep.
    ///
    test('paints comments, keywords and captures as different things', async ({ page }) => {
        await openEditor(page);
        await setScript(page, '# a note\nPRINT hello\nMEASure:VOLTage:DC? -> v\nDELAY 500');

        const tokens = await scriptTokens(page);
        const typeOf = (text) => tokens.find((t) => t.text.includes(text))?.type ?? '';

        //The grammar's own names, which are the ones js/monaco.js gives colours to out of
        //ScriptEditor.ColorFor. What the colours are is stated in one place; that a comment is not a
        //keyword is stated here.
        expect(typeOf('# a note')).toMatch(/comment/);
        expect(typeOf('PRINT')).toMatch(/keyword/);
        expect(typeOf('->')).toMatch(/operator/);
        expect(tokens.some((t) => /variable/.test(t.type) && t.text.trim() === 'v')).toBe(true);
    });

    ///
    ///And the SCPI is left plain-ish, on purpose: it is the point of the line.
    ///
    test('leaves the command itself alone', async ({ page }) => {
        await openEditor(page);
        await setScript(page, 'PRINT hello\nMEASure:VOLTage:DC?');

        const tokens = await scriptTokens(page);
        const keyword = tokens.find((t) => t.text.includes('PRINT'))?.type;
        const command = tokens.find((t) => t.text.includes('MEASure'))?.type;

        //Plain, and that is the assertion. ScriptEditor.ColorFor gives Command no colour at all — it
        //falls through to the default — so a command that came out coloured would be the web inventing
        //a distinction the desktop does not draw.
        expect(command).toMatch(/^source|^$/);
        expect(command).not.toBe(keyword);
    });

    ///
    ///Suggestions, and they come from Core rather than from a list kept in the browser.
    ///
    ///ScriptLanguage.Complete is what knows the language — and knows more than a fixed list could, since
    ///it reads the script for the aliases it has declared and the names it has captured. The editor only
    ///asks. Two lists of what the language contains would be one list and one guess.
    ///
    test('offers the language own words', async ({ page }) => {
        await openEditor(page);
        await setScript(page, 'REP');

        //Ctrl+Space rather than waiting for the list to volunteer itself: what is being checked is that
        //there is something to offer, not when Monaco decides to offer it.
        await page.keyboard.press('Control+Space');

        const list = page.locator('.monaco-editor .suggest-widget');
        await expect(list).toBeVisible();
        await expect(list).toContainText('REPEAT');
    });

    ///
    ///A `$` has one meaning, so once it is typed nothing else is offered — and what *is* offered is read
    ///out of the script rather than out of any list.
    ///
    test('offers the names this script has captured, after a dollar', async ({ page }) => {
        await openEditor(page);
        await setScript(page, 'MEASure:VOLTage:DC? -> reading\nPRINT $');

        await page.keyboard.press('Control+Space');

        const list = page.locator('.monaco-editor .suggest-widget');
        await expect(list).toBeVisible();
        await expect(list).toContainText('reading');
    });
});

test.describe('running a script', () => {
    ///
    ///Run to completion: the messages land in the output, the SCPI reaches the instrument, and the
    ///status line says what happened.
    ///
    test('sends the script to the instrument and reports what came back', async ({ page }) => {
        const tool = await openEditor(page);
        await setScript(page, SCRIPT);

        await tool.getByRole('button', { name: /^Run$/ }).click();

        const output = tool.locator('.group', { hasText: 'Output' }).locator('.console');
        await expect(output).toContainText('starting');
        await expect(output).toContainText('done');
        await expect(status(page)).toHaveText('Run complete.', { timeout: 20000 });

        //The instrument was really asked, and asked exactly what the script said.
        expect(instrument.asked(/MEASure:VOLTage:DC\?/)).toHaveLength(1);
    });

    ///
    ///Run and Stop trade places while it runs.
    ///
    ///Checked on a script slow enough to catch in the act - a REPEAT with a DELAY in it - because one
    ///that finished before the assertion ran would pass whatever the buttons did.
    ///
    test('Run gives way to Stop while a script is running', async ({ page }) => {
        const tool = await openEditor(page);
        //PRINT first, then the delay. The other way round the run is still inside its first wait when
        //Stop arrives, and "what it collected" is nothing - which would pass or fail on timing rather
        //than on whether anything is kept.
        await setScript(page, 'REPEAT 40\n    PRINT tick\n    DELAY 200\nEND');

        await tool.getByRole('button', { name: /^Run$/ }).click();

        await expect(tool.getByRole('button', { name: /^Run$/ })).toBeDisabled();
        await expect(tool.getByRole('button', { name: /^Stop$/ })).toBeEnabled();

        await tool.getByRole('button', { name: /^Stop$/ }).click();

        await expect(tool.getByRole('button', { name: /^Run$/ })).toBeEnabled({ timeout: 20000 });
        await expect(tool.getByRole('button', { name: /^Stop$/ })).toBeDisabled();

        //And what it collected before being stopped is kept, which is what Stop's tooltip promises.
        await expect(tool.locator('.group', { hasText: 'Output' }).locator('.console'))
            .toContainText('tick');
    });

    ///
    ///RECORD builds the table, under the heading COLUMNS gave it.
    ///
    ///In the same pane the console uses, because it is the same control on the desktop: ResultsPanel,
    ///beside the output rather than under it, with the table and the curve as two tabs. It was a
    ///table and a plot stacked below the log, and only while there were rows - so a run you were
    ///watching pushed itself off the bottom of the window.
    ///
    test('a recorded row lands in the results pane, and the plot draws it', async ({ page }) => {
        const tool = await openEditor(page);
        await setScript(page, SCRIPT);
        await tool.getByRole('button', { name: /^Run$/ }).click();

        const results = tool.locator('.group', { hasText: 'Results table' });
        await expect(results.locator('thead th')).toHaveText(['volts'], { timeout: 20000 });
        await expect(results.locator('tbody tr')).toHaveCount(1);
        await expect(results.locator('tbody td').first()).toHaveText(/^[+-]?\d/);

        await expect(tool.getByRole('button', { name: 'Save CSV' })).toBeVisible();
        await expect(tool.getByRole('button', { name: 'Clear Results' })).toBeVisible();

        //The curve is the other tab, as it is in the console.
        await results.getByRole('button', { name: 'Plot' }).click();
        await expect(tool.locator('.plotcanvas')).toBeVisible();
    });

    ///
    ///The output pane has the same two buttons the console's log has, and the same rule about them.
    ///
    test('Clear Log empties the output and goes dead with it', async ({ page }) => {
        const tool = await openEditor(page);

        await expect(tool.getByRole('button', { name: 'Clear Log' })).toBeDisabled();
        await expect(tool.getByRole('button', { name: 'Save Log' })).toBeDisabled();

        await setScript(page, 'PRINT hello');
        await tool.getByRole('button', { name: /^Run$/ }).click();

        const output = tool.locator('.group', { hasText: 'Output' }).locator('.console');
        await expect(output).toContainText('hello');
        await expect(tool.getByRole('button', { name: 'Clear Log' })).toBeEnabled();

        await tool.getByRole('button', { name: 'Clear Log' }).click();
        await expect(output).toHaveText('');
        await expect(tool.getByRole('button', { name: 'Clear Log' })).toBeDisabled();
    });

    ///
    ///Clear Results takes the rows and leaves the log, as everywhere else in the app: the log is the
    ///record of how those rows were produced.
    ///
    test('Clear Results takes the table and leaves the output', async ({ page }) => {
        const tool = await openEditor(page);
        await setScript(page, SCRIPT);
        await tool.getByRole('button', { name: /^Run$/ }).click();

        await expect(tool.locator('.group', { hasText: 'Results table' }).locator('tbody tr'))
            .toHaveCount(1, { timeout: 20000 });

        await tool.getByRole('button', { name: 'Clear Results' }).click();

        //The headings stay. ResultsPanel.Clear keeps them — they are what the table is, not
        //what is in it — and the table itself is there from the moment the window opens.
        await expect(tool.locator('table.results thead th')).toHaveText(['volts']);
        await expect(tool.locator('table.results tbody tr')).toHaveCount(0);
        await expect(tool.locator('.group', { hasText: 'Output' }).locator('.console'))
            .toContainText('starting');
    });
});
