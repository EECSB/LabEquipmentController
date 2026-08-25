//
//The multi-instrument script window, and the strip that says what its DEVICE lines resolve to.
//
//A sequence names instruments by model rather than by address - `DEVICE gen : SDG2042X` - so the same
//saved script finds its instruments after DHCP has moved them. Which means the window has to answer a
//question the single-instrument editor never has to ask: is what this script names actually on the
//bench right now?
//
//SequenceForm answers it without being asked. The strip is docked between its toolbar and its editor,
//it is re-read on every keystroke and on a one-second timer, and Run follows it. This build put that
//answer behind a Check devices button and printed it in a card of its own below the editor - a press to
//be told something the window already knew, on a toolbar the desktop's does not have.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, boxOf, setScript, scriptText, editorReady } = require('./helpers');
const { startInstrument } = require('./instrument');

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument();
});

test.afterAll(async () => {
    await instrument.stop();
});

///The multi-instrument window, open over the bench.
async function openSequences(page) {
    await page.getByRole('button', { name: /Multi-Instrument Scripts/ }).click();
    await expect(page.locator('dialog.tool[open]')).toBeVisible();
    await expect(page.locator('dialog.tool[open] > .tool-head')).toContainText('Multi-Instrument Scripts');
    await editorReady(page);
    return page.locator('dialog.tool[open]');
}

///The strip, when the script names instruments: a row per alias, with a picker for the part it plays.
function bindings(page) {
    return page.locator('dialog.tool[open] .group.editor > .bindings');
}

///One row of it, by the alias in its first cell.
function binding(page, alias) {
    return bindings(page).locator('tbody tr').filter({ has: page.locator(`td:text-is("${alias}")`) });
}

///And the sentence that stands in its place while the script names none.
function strip(page) {
    return page.locator('dialog.tool[open] .group.editor > .devices');
}

const RUN = { name: 'Run', exact: true };

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await connect(page, instrument.address);
});

test.describe('the toolbar', () => {
    ///
    ///SequenceForm's toolbar is ScriptForm's less New and Save As...: Open, Save, the examples box,
    ///Snippets, Script with AI, Run, Stop. There is no Check devices on it, and there is nothing for
    ///one to do - the window already knows the answer and shows it.
    ///
    test('is the desktop\'s, with nothing added to it', async ({ page }) => {
        const tool = await openSequences(page);

        await expect(tool.locator('.row.scripttools')).not.toContainText('Check devices');

        const names = await tool.locator('.row.scripttools > button, .row.scripttools .settings > button')
            .allTextContents();
        expect(names.map((n) => n.trim())).toEqual(
            ['Open…', 'Save', 'Snippets ▾', 'Script with AI…', 'Run', 'Stop']);
    });
});

test.describe('the binding strip', () => {
    ///
    ///Docked between the toolbar and the editor, in the card with the text it is about - which is where
    ///SequenceForm docks its own label, and the reason it is not a card of its own.
    ///
    test('sits between the toolbar and the editor, in the card with it', async ({ page }) => {
        const tool = await openSequences(page);

        await expect(bindings(page)).toBeVisible();

        const toolbar = await boxOf(page, 'dialog.tool[open] .row.scripttools');
        const line = await boxOf(page, 'dialog.tool[open] .group.editor > .bindings');
        const editor = await boxOf(page, 'dialog.tool[open] .code.monaco');
        const card = await boxOf(page, 'dialog.tool[open] .group.editor');

        expect(line.top).toBeGreaterThan(toolbar.bottom);
        expect(line.bottom).toBeLessThanOrEqual(editor.top);

        //In the card, not above it: the card's own left padding puts the strip in line with the editor
        //under it rather than with the toolbar over it.
        expect(line.top).toBeGreaterThan(card.top);
        expect(line.left).toBe(editor.left);

        //And it is the only place any of it is said. It was a line here and a card of bindings under
        //the editor - the same three facts about the same aliases, twice, and only one of the two
        //answerable. The table is the one that stayed, and it stayed up here.
        await expect(tool.locator('.panel h2')).toHaveCount(0);
        await expect(tool.locator('.group.editor > .devices')).toHaveCount(0);
    });

    ///
    ///A script with no DEVICE lines is not a broken script. PRINT, DELAY and RECORD need no instrument,
    ///and one that does carry a command the runner will refuse is refused by name - which says more than
    ///a greyed-out button. SequenceForm leaves Run alone here, and says what to type to get started.
    ///
    test('says so when nothing is declared, and leaves Run available', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, 'PRINT "nothing to bind"');

        await expect(strip(page)).toHaveText(/No instruments declared/);
        expect(await strip(page).getAttribute('class')).toContain('muted');

        //A table with no rows says less than the sentence does, so there is no table.
        await expect(bindings(page)).toHaveCount(0);

        await expect(tool.getByRole('button', RUN)).toBeEnabled();
    });

    ///
    ///What each alias resolved to: the name, the model the script asks for, and the open connection
    ///playing the part - which is also where you say which one, so it reports and answers at once.
    ///
    test('names what each alias resolved to, and lets it run', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, `DEVICE dmm : SDM3065X\nWITH dmm\n  MEAS:VOLT:DC?\nEND`);

        await expect(bindings(page).locator('tbody tr')).toHaveCount(1);

        const row = binding(page, 'dmm');
        await expect(row.locator('td').nth(1)).toHaveText('SDM3065X');
        await expect(row.locator('select')).toHaveValue(/.+/);
        await expect(row.locator('select option:checked')).toContainText(instrument.address);
        await expect(row).not.toHaveClass(/unbound/);

        await expect(tool.getByRole('button', RUN)).toBeEnabled();
    });

    ///
    ///And when it is not there. Red, said by name, and Run withheld - the point of naming instruments up
    ///front is to fail before anything has been sent, because a sweep that dies three lines in has
    ///already changed the instrument's state.
    ///
    test('turns red on an instrument that is not there, and takes Run away', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, `DEVICE dmm : SDM3065X\nDEVICE scope : DS2202\nWITH dmm\n  MEAS:VOLT:DC?\nEND`);

        await expect(bindings(page).locator('tbody tr')).toHaveCount(2);

        //The one that is there, and the one that is not.
        await expect(binding(page, 'dmm')).not.toHaveClass(/unbound/);
        await expect(binding(page, 'scope')).toHaveClass(/unbound/);
        await expect(binding(page, 'scope').locator('select')).toHaveValue('');

        //Red, where SequenceForm's line was red - on the columns naming the part rather than on the
        //picker, which is the answer and not the complaint.
        const red = await binding(page, 'scope').locator('td.mono').first()
            .evaluate((el) => getComputedStyle(el).color);
        const plain = await binding(page, 'dmm').locator('td.mono').first()
            .evaluate((el) => getComputedStyle(el).color);
        expect(red).not.toBe(plain);

        await expect(tool.getByRole('button', RUN)).toBeDisabled();
    });

    ///
    ///All of that from typing, with nothing pressed. This is what the Check devices button was for, and
    ///the reason it is gone: the answer changes as the script is written, so a press only ever tells you
    ///what was true at the moment you pressed.
    ///
    test('follows the editor with no button pressed', async ({ page }) => {
        await openSequences(page);

        await setScript(page, 'DEVICE dmm : SDM3065X');
        await expect(binding(page, 'dmm')).not.toHaveClass(/unbound/);

        await setScript(page, 'DEVICE nope : NOTHERE');
        await expect(bindings(page)).toContainText('NOTHERE');
        await expect(binding(page, 'nope')).toHaveClass(/unbound/);

        await setScript(page, 'PRINT "and back again"');
        await expect(bindings(page)).toHaveCount(0);
        await expect(strip(page)).toHaveText(/No instruments declared/);
    });

    ///
    ///And the picker is the answer as well as the report: choosing a connection binds the part, which
    ///is what the line above the editor could never do.
    ///
    test('binds a part from the strip itself', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, `DEVICE dmm : SDM3065X\nDEVICE spare : SDM3065X`);

        //Two aliases, one instrument: the resolver takes the first for the first line and leaves the
        //second with nothing, which is the case a picker is for.
        const spare = binding(page, 'spare');
        await expect(tool.getByRole('button', RUN)).toBeDisabled();

        const id = await binding(page, 'dmm').locator('select').inputValue();
        await spare.locator('select').selectOption(id);

        await expect(spare).not.toHaveClass(/unbound/);
        await expect(tool.getByRole('button', RUN)).toBeEnabled();
    });
});

test.describe('the editor it opens with', () => {
    ///
    ///On a worked example, not a blank page - `_editor.Text = SequenceExamples.All[0].Script`.
    ///
    ///A language whose whole point is DEVICE, WITH and FOR teaches none of it from an empty box, and
    ///an empty box also leaves the AI window's "Revise the current script" with nothing to revise:
    ///the desktop ticks that switch when there is a script, and on the desktop there always is one.
    ///
    test('holds the first example, so there is something to revise', async ({ page }) => {
        await openSequences(page);

        const script = await scriptText(page);
        expect(script.length).toBeGreaterThan(80);
        expect(script).toMatch(/^\s*#/);          // the example opens on its own explanation
        expect(script).toMatch(/\bDEVICE\b/);

        //And the strip agrees, without a button being pressed.
        await expect(bindings(page).locator('tbody tr').first()).toBeVisible();
    });
});
