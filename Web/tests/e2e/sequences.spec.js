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
const fs = require('fs');
const { test, expect } = require('./fixtures');
const { freshBench, connect, boxOf, quick, setScript, scriptText, editorReady, BOOT_MS } = require('./helpers');
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

///The line along the foot of the window: what the last thing to happen was.
function status(page) {
    return page.locator('dialog.tool[open] .row.runstatus .muted');
}

///The output pane, where a run says it has finished.
function output(page) {
    return page.locator('dialog.tool[open] .split .console');
}

///
///An F5 made in the page rather than typed, sent to `selector` or, with none, to the page itself.
///Answers whether anything took it.
///
///For the places where F5 has to be left to the browser. A real key there is a real reload,
///which would take the window this spec is asking about away with it.
///
async function dispatchF5(page, selector) {
    return page.evaluate((sel) => {
        const e = new KeyboardEvent('keydown', { key: 'F5', bubbles: true, cancelable: true });
        (sel ? document.querySelector(sel) : document.body).dispatchEvent(e);
        return e.defaultPrevented;
    }, selector ?? null);
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

        //The strip is re-read once typing pauses, so until then it still shows the script before this
        //one - whose rows were unbound too, and would pass everything below for the wrong reason.
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm', 'spare']);

        //Two aliases, one instrument: the resolver takes the first for the first line and leaves the
        //second with nothing, which is the case a picker is for. Filled with the same meter, the run
        //would read it twice and report two.
        const spare = binding(page, 'spare');
        await expect(binding(page, 'dmm')).not.toHaveClass(/unbound/);
        await expect(spare).toHaveClass(/unbound/);
        await expect(spare.locator('select')).toHaveValue('');
        await expect(spare.locator('select option:checked')).toHaveText(/taken by dmm/);
        await expect(tool.getByRole('button', RUN)).toBeDisabled();

        //Given it on purpose, it is both of theirs: picking dmm's meter for spare is not dmm letting
        //go of it.
        const id = await binding(page, 'dmm').locator('select').inputValue();
        await spare.locator('select').selectOption(id);

        await expect(spare).not.toHaveClass(/unbound/);
        await expect(binding(page, 'dmm').locator('select')).toHaveValue(id);
        await expect(tool.getByRole('button', RUN)).toBeEnabled();
    });
});

test.describe('F5', () => {
    ///What the instrument has been asked to measure since `heard`.
    function measured(heard) {
        return instrument.received.slice(heard).filter((c) => c.startsWith('MEAS:'));
    }

    ///
    ///It runs the script, which is what the Run button says on hover. It used to reload the page,
    ///which shut the window and took the script being written with it.
    ///
    test('runs the script, as Run does', async ({ page }) => {
        await openSequences(page);
        await setScript(page, 'DEVICE dmm : SDM3065X\ndmm: MEAS:VOLT:DC?');
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm']);

        const heard = instrument.received.length;
        await page.keyboard.press('F5');

        await expect(output(page)).toContainText('--- done ---');
        expect(measured(heard)).toEqual(['MEAS:VOLT:DC?']);
    });

    ///
    ///Only when Run would. A DEVICE line with nothing behind it greys Run out, and F5 does not get
    ///round that: the status line says which line and why, and nothing is sent. That includes the
    ///command above the missing line, which is what the desktop's F5 once sent.
    ///
    test('is no way around a greyed-out Run', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, 'DEVICE dmm : SDM3065X\ndmm: MEAS:VOLT:DC?\nDEVICE scope : DS2202\nscope: *IDN?');
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm', 'scope']);
        await expect(tool.getByRole('button', RUN)).toBeDisabled();

        const heard = instrument.received.length;
        await page.keyboard.press('F5');
        await expect(status(page)).toHaveText('Not run — scope → DS2202 (not connected).');

        //To be sure nothing is still on its way, the same script without the missing part, which does
        //run. Had the first press sent its line, there would be two.
        await setScript(page, 'DEVICE dmm : SDM3065X\ndmm: MEAS:VOLT:DC?');
        await page.keyboard.press('F5');
        await expect(output(page)).toContainText('--- done ---');
        expect(measured(heard)).toEqual(['MEAS:VOLT:DC?']);
    });

    ///
    ///With nothing focused, the key belongs to the window last pressed in. Run, pressed with the
    ///mouse, leaves the focus on nothing: it greys out as the run starts. F5 after that is the
    ///editor's. On the bench it stays the browser's: the bench is not an editor, and a reload does
    ///not disconnect it. A window opened over the editor is not another way to lose the focus: it
    ///gives the focus back as it shuts (dialogs.spec.js).
    ///
    test('with nothing focused, belongs to the window last pressed in', async ({ page }) => {
        const tool = await openSequences(page);
        //Long enough for Run to stay grey past a frame. A run over before the next frame hands Run
        //back before the browser has taken the focus off it, which is a different case, and an
        //easier one: the focus is still in the window.
        await setScript(page, 'DEVICE dmm : SDM3065X\ndmm: MEAS:VOLT:DC?\nDELAY 1000');
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm']);
        const heard = instrument.received.length;
        const nothingFocused = () => page.evaluate(() => document.activeElement === document.body);

        await tool.getByRole('button', RUN).click();
        await expect(tool.getByRole('button', RUN)).toBeDisabled();
        await expect.poll(nothingFocused).toBe(true);
        await expect(output(page)).toContainText('--- done ---');

        await page.keyboard.press('F5');
        await expect.poll(() => measured(heard)).toHaveLength(2);
        await expect(output(page)).toContainText('--- done ---');

        //The bench: a control of its own with the focus, and then nothing with the focus after a press
        //in the margin the window leaves round itself. Dispatched rather than typed, because an F5 left
        //to the browser is a real reload.
        expect(await dispatchF5(page, '#addr')).toBe(false);
        await page.mouse.click(6, 500);
        expect(await nothingFocused()).toBe(true);
        expect(await dispatchF5(page)).toBe(false);
        expect(measured(heard)).toHaveLength(2);
    });

    ///
    ///Once, however it is pressed. Held down, F5 repeats. Pressed again mid-run, it is a second
    ///press. Run double-clicked is two presses before the first has heard back from the server. The
    ///desktop takes its token before anything else happens, so a second press finds it taken and
    ///returns. Here the double-click had been two runs, interleaved on one meter.
    ///
    test('is one run however it is pressed', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, 'DEVICE dmm : SDM3065X\ndmm: MEAS:VOLT:DC?\nDELAY 1500');
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm']);

        const heard = instrument.received.length;
        await page.keyboard.down('F5');
        await page.keyboard.down('F5');      // the second is a repeat, as a held key's are
        await page.keyboard.up('F5');

        await expect(tool.getByRole('button', { name: 'Stop', exact: true })).toBeEnabled();
        await page.keyboard.press('F5');

        await expect(output(page)).toContainText('--- done ---');
        await expect(output(page).locator('div', { hasText: '--- done ---' })).toHaveCount(1);
        expect(measured(heard)).toEqual(['MEAS:VOLT:DC?']);

        await tool.getByRole('button', RUN).dblclick();
        await expect(tool.getByRole('button', { name: 'Stop', exact: true })).toBeEnabled();
        await expect(output(page)).toContainText('--- done ---');
        expect(measured(heard)).toEqual(['MEAS:VOLT:DC?', 'MEAS:VOLT:DC?']);
    });

    ///
    ///And not in a window opened over the editor. The reference and the AI window are opened from it
    ///and sit inside it on the page, but they are windows of their own, and F5 there is not a Run.
    ///
    test('is not the editor\'s in a window opened over it', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, 'DEVICE dmm : SDM3065X\ndmm: MEAS:VOLT:DC?');
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm']);

        await tool.getByRole('button', { name: /^Snippets/ }).click();
        await tool.getByRole('button', { name: /What all of this means/ }).click();
        const over = 'dialog.tool[open] dialog.tool[open]';
        await expect(page.locator(over)).toBeVisible();

        const heard = instrument.received.length;
        expect(await dispatchF5(page, `${over} > .tool-head`)).toBe(false);

        //The editor's own window still takes it, and runs once: the press in the reference sent nothing.
        expect(await dispatchF5(page, 'dialog.tool[open] .row.scripttools')).toBe(true);
        await expect(output(page)).toContainText('--- done ---');
        expect(measured(heard)).toEqual(['MEAS:VOLT:DC?']);
    });
});

test.describe('snippets', () => {
    ///
    ///A snippet's dollar signs are the language's, not the editor's. The whole-sweep snippet records
    ///$f and $v, and Monaco reads a bare $f in a snippet as a variable of its own: it wrote "f", and
    ///the RECORD line recorded two words.
    ///
    test('keep their dollar signs', async ({ page }) => {
        await openSequences(page);
        await setScript(page, 'sweep');   // the snippet's word, with the caret after it
        await page.keyboard.press('Tab');

        const text = async () => (await scriptText(page)).replace(/\r\n/g, '\n');
        await expect.poll(text).toContain('RECORD $f, $v');
        expect(await text()).toContain('«gen»: «set frequency» $f');
        expect(await text()).toContain('DEVICE «gen» : «MODEL»');
    });
});

test.describe('Ctrl+S', () => {
    ///
    ///It saves the multi-instrument script as well, as SequenceForm's does, under this window's own
    ///name for one. Cmd+S does the same, since that is the save key on a Mac.
    ///
    test('saves the script, and so does Cmd+S', async ({ page }) => {
        await openSequences(page);
        const saved = [];
        page.on('download', (d) => saved.push(d));

        const script = 'DEVICE dmm : SDM3065X\ndmm: MEAS:VOLT:DC?';
        await setScript(page, script);
        await page.keyboard.press('Control+s');

        await expect(status(page)).toHaveText('Saved sequence.txt');
        await expect.poll(() => saved.length).toBe(1);
        expect(saved[0].suggestedFilename()).toBe('sequence.txt');
        expect(fs.readFileSync(await saved[0].path(), 'utf8').replace(/\r\n/g, '\n')).toBe(script);

        await page.keyboard.press('Meta+s');
        await expect.poll(() => saved.length).toBe(2);
    });
});

test.describe('Open in a tab', () => {
    ///Two parts of one model and one meter: the rule gives it to the first and leaves the second for a
    ///pick, so a pick that made the trip can be told from the rule's own answer.
    const PICKED = 'DEVICE dmm : SDM3065X\nDEVICE spare : SDM3065X\ndmm: *IDN?\nspare: *IDN?';
    const WRITTEN = PICKED + '\nPRINT "written in the tab"';

    ///The script in `where`, with Monaco's line endings (CRLF on Windows) read as the \n it was written with.
    const textIn = async (where) => (await scriptText(where)).replace(/\r\n/g, '\n');

    ///
    ///The window moves with what is being written in it: the script, and the parts picked for it by
    ///hand. The tab had opened on the first example, as a new window does, and the script went with
    ///the dialog that closed. Closing the tab brings the window back with what the tab held, kept as
    ///it changed because a tab that is closing cannot hand anything over. Opened afresh after that,
    ///it is a new window again, on the first example, as a new SequenceForm is.
    ///
    test('takes the script and its picks, and brings them back', async ({ page, context }) => {
        const tool = await openSequences(page);
        await setScript(page, PICKED);
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm', 'spare']);
        const id = await binding(page, 'dmm').locator('select').inputValue();
        await binding(page, 'spare').locator('select').selectOption(id);
        await expect(binding(page, 'spare')).not.toHaveClass(/unbound/);

        const [tab] = await Promise.all([
            context.waitForEvent('page'),
            tool.locator('> .tool-head a.btn').click(),
        ]);
        try {
            await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
            await expect(tab.locator('.code.monaco')).toBeVisible({ timeout: BOOT_MS });
            //Polled, not read once: the editor is on screen a moment before the carried script
            //has been put in it, and a one-shot read caught it empty about a third of the time.
            await expect.poll(() => textIn(tab), { timeout: BOOT_MS }).toBe(PICKED);
            const spare = tab.locator('.group.editor > .bindings tbody tr')
                .filter({ has: tab.locator('td:text-is("spare")') });
            await expect(spare).not.toHaveClass(/unbound/);
            await expect(spare.locator('select')).toHaveValue(id);

            await setScript(tab, WRITTEN);
            await expect.poll(() => page.evaluate(() => localStorage.getItem('lec.carried.sequences')))
                .toContain('written in the tab');
        } finally {
            await tab.close();
        }

        //Coming back to the bench is what notices the tab has gone.
        await page.bringToFront();
        await expect(page.locator('dialog.tool[open] > .tool-head'))
            .toContainText('Multi-Instrument Scripts', { timeout: 20000 });
        await editorReady(page);
        await expect.poll(() => textIn(page)).toBe(WRITTEN);
        await expect(binding(page, 'spare')).not.toHaveClass(/unbound/);

        await page.locator('dialog.tool[open] > .tool-head .shut').click();
        await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
        await openSequences(page);
        await expect.poll(() => textIn(page)).toMatch(/^# Frequency response/);
    });
});

test.describe('while a script runs', () => {
    ///Long enough that only a stop ends it inside the test: it holds the meter for a hundred seconds.
    const LONG = 'DEVICE dmm : SDM3065X\ndmm: MEAS:VOLT:DC?\nDELAY 100000';
    const QUESTION = 'A script is still running. Stop it and close?';

    ///
    ///Open in a tab is greyed, with the reason. The tab opens on an editor of its own, so the move
    ///left the run behind, holding the meter with nothing to watch it or to stop it from. A control
    ///that cannot do its job is present and greyed, with the reason in its tooltip, and this one is
    ///a link again when the run ends.
    ///
    test('the window does not move into a tab', async ({ page }) => {
        const tool = await openSequences(page);
        const head = tool.locator('> .tool-head');
        const out = head.locator('a.btn', { hasText: 'Open in a tab' });
        const held = head.getByRole('button', { name: 'Open in a tab' });
        await expect(out).toBeVisible();

        await setScript(page, LONG);
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm']);
        await tool.getByRole('button', RUN).click();
        await expect(quick(page, 'DC V')).toBeDisabled();

        await expect(held).toBeDisabled();
        await expect(held).toHaveAttribute('title', /Not while a script is running in this window/);
        await expect(out).toHaveCount(0);

        await tool.getByRole('button', { name: 'Stop', exact: true }).click();
        await expect(quick(page, 'DC V')).toBeEnabled({ timeout: 10000 });
        await expect(out).toBeVisible();
        await expect(held).toHaveCount(0);
    });

    ///
    ///SequenceForm asks first. No leaves the window up and the run going; Yes stops the run and
    ///closes. The window had closed without asking, and the run went on holding the meter, with
    ///nothing left to watch it or to stop it from.
    ///
    ///Esc gets there with the focus gone from Run as it greyed out under the pointer. The key goes to
    ///the window last pressed in, as F5 does, where it had reached no window at all. After a press on
    ///the bench it is nobody's.
    ///
    test('asks first, and stops the run only when told to', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, LONG);
        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['dmm']);

        const asked = [];
        let yes = false;
        page.on('dialog', (d) => { asked.push(d.message()); return yes ? d.accept() : d.dismiss(); });
        const nothingFocused = () => page.evaluate(() => document.activeElement === document.body);

        await tool.getByRole('button', RUN).click();
        await expect(quick(page, 'DC V')).toBeDisabled();
        await expect.poll(nothingFocused).toBe(true);

        //No: the window stays, and so does the run.
        await page.keyboard.press('Escape');
        await expect.poll(() => asked).toEqual([QUESTION]);
        await expect(tool).toHaveCount(1);
        await expect(tool.getByRole('button', { name: 'Stop', exact: true })).toBeEnabled();
        await expect(quick(page, 'DC V')).toBeDisabled();

        //A press on the bench, and the next Esc is not the window's.
        await page.mouse.click(6, 500);
        expect(await nothingFocused()).toBe(true);
        await page.keyboard.press('Escape');

        //Yes, by the ✕ this time: the window goes, and the run with it.
        yes = true;
        await tool.locator('> .tool-head .shut').click();
        await expect(tool).toHaveCount(0);
        await expect(quick(page, 'DC V')).toBeEnabled({ timeout: 10000 });
        expect(asked).toEqual([QUESTION, QUESTION]);
    });
});

test.describe('two instruments of one model', () => {
    ///The second meter: the same model as the first, told apart by its serial number.
    const SECOND = 'Siglent Technologies,SDM3065X,LEC-E2E-0002,1.00.00.00';

    let second;

    test.beforeEach(async ({ page }) => {
        second = await startInstrument({ identity: SECOND });
        await connect(page, second.address, { expectTabs: 2 });
    });

    test.afterEach(async () => {
        await second.stop();
    });

    ///An option naming this address, and not another one it happens to be the start of: both are
    ///127.0.0.1, and a port of four digits is inside one of five.
    function at(address) {
        return new RegExp(address.replace(/\./g, '\\.') + '$');
    }

    ///The session id the picker offers for an instrument, found by the address it shows.
    async function sessionOf(page, alias, address) {
        return binding(page, alias).locator('select option', { hasText: at(address) }).getAttribute('value');
    }

    ///
    ///Which of two identical meters is wired to the left channel is not something the order they
    ///were connected in can say, so neither row is filled: both wait, saying why. It used to fill both
    ///with the first meter, and the run read that one twice. Say which is left, and right is the one
    ///that is left over - and the run then drives exactly what the table shows.
    ///
    test('asks which is which, and runs on what it was told', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, [
            'DEVICE left  : SDM3065X',
            'DEVICE right : SDM3065X',
            'left:  MEAS:VOLT:DC?',
            'right: MEAS:CURR:DC?',
        ].join('\n'));

        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['left', 'right']);
        for (const alias of ['left', 'right']) {
            await expect(binding(page, alias)).toHaveClass(/unbound/);
            await expect(binding(page, alias).locator('select option:checked')).toHaveText(/2 connected/);
        }
        await expect(tool.getByRole('button', RUN)).toBeDisabled();

        //F5 is not a way round it either. The status line gives the picker's own reason, and names
        //the first line that has no instrument.
        const before = instrument.received.length;
        await page.keyboard.press('F5');
        await expect(status(page)).toHaveText('Not run — left → SDM3065X (2 connected: pick one).');
        expect(instrument.received.slice(before).filter((c) => c.startsWith('MEAS:'))).toEqual([]);
        expect(second.asked(/^MEAS:/)).toEqual([]);

        await binding(page, 'left').locator('select')
            .selectOption(await sessionOf(page, 'left', instrument.address));

        await expect(binding(page, 'right').locator('select option:checked')).toHaveText(at(second.address));
        await expect(tool.getByRole('button', RUN)).toBeEnabled();

        const heard = instrument.received.length;
        await tool.getByRole('button', RUN).click();
        await expect(tool.locator('.split .console')).toContainText('--- done ---');

        expect(instrument.received.slice(heard).filter((c) => c.startsWith('MEAS:'))).toEqual(['MEAS:VOLT:DC?']);
        expect(second.asked(/^MEAS:/)).toEqual(['MEAS:CURR:DC?']);
    });

    ///
    ///And a script can say it itself, with nothing picked: a serial number names one meter in
    ///particular, and the line asking for the model finds the one that is left.
    ///
    test('finds one by serial number and the other by what is left', async ({ page }) => {
        const tool = await openSequences(page);
        await setScript(page, 'DEVICE left : LEC-E2E-0002\nDEVICE right : SDM3065X');

        await expect(bindings(page).locator('tbody tr td:first-child')).toHaveText(['left', 'right']);
        await expect(binding(page, 'left').locator('select option:checked')).toHaveText(at(second.address));
        await expect(binding(page, 'right').locator('select option:checked')).toHaveText(at(instrument.address));
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
