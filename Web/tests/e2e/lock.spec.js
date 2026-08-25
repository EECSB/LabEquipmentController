//
//The console, locked out while something else is driving the link.
//
//The desktop sets `InstrumentSession.IsBusy` in exactly two places - a script starting, and a readout
//beginning to poll - and greys the console out while it is set. The reason is not that a second command
//would corrupt a reply: everything here goes through SerializedInstrumentClient, which would queue it
//safely. The reason is that it would *interleave*, and a command that changes the instrument's function
//between two steps of a sweep changes what the sweep measured.
//
//The web went without this for a while, which the specs found rather than a person. It is held on the
//server now, not in the page that started the run, because the bench is one shared workspace: a console
//open in a second tab is watching the same instrument and has the same reason not to be typed into.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane, quick, consoleButton, setScript } = require('./helpers');
const { startInstrument } = require('./instrument');

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument();
});

test.afterAll(async () => {
    await instrument.stop();
});

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await connect(page, instrument.address);
    instrument.received.length = 0;
});

///A script long enough to still be running while a spec looks at the console under it.
const SLOW = 'REPEAT 40\n    PRINT tick\n    DELAY 250\nEND';

///Everything the lock reaches, and what it must leave alone.
async function lockState(page) {
    return page.evaluate(() => {
        const p = document.querySelector('.group.consoles .pane:not([hidden])');
        const named = (text) => [...p.querySelectorAll('.toolbar button, .row.cmd button')]
            .find((b) => b.textContent.trim().startsWith(text));
        return {
            quick: [...p.querySelectorAll('button.quick')].every((b) => b.disabled),
            box: p.querySelector('.row.cmd input').disabled,
            send: named('Send')?.disabled,
            discover: named('Discover Commands')?.disabled,
            readout: named('Live Readout')?.disabled,
            clearLog: named('Clear Log')?.disabled,
            saveLog: named('Save Log')?.disabled,
            scripts: named('Scripts')?.disabled
        };
    });
}

test.describe('while a script is running', () => {
    ///
    ///The console goes out of reach, and comes back.
    ///
    test('the console locks itself out, and unlocks when the run ends', async ({ page }) => {
        const before = await lockState(page);
        expect(before.quick).toBe(false);
        expect(before.box).toBe(false);

        await pane(page).getByRole('button', { name: /Scripts/ }).click();
        const tool = page.locator('dialog.tool[open]');
        await setScript(page, SLOW);
        await tool.getByRole('button', { name: /^Run$/ }).click();

        await expect(quick(page, 'DC V')).toBeDisabled();

        const during = await lockState(page);
        expect(during.quick, 'a quick command stayed live during a run').toBe(true);
        expect(during.box, 'the command box stayed live during a run').toBe(true);
        expect(during.send).toBe(true);
        expect(during.discover, 'Discover Commands sends its own commands and stayed live').toBe(true);

        await tool.getByRole('button', { name: /^Stop$/ }).click();

        await expect(quick(page, 'DC V')).toBeEnabled({ timeout: 20000 });
        const after = await lockState(page);
        expect(after.box).toBe(false);
        expect(after.discover).toBe(false);

        //Send through the box rather than off lockState: with the run over it is still disabled, for
        //the other reason - there is nothing in the box to send. Typing tells the two apart.
        await pane(page).locator('.row.cmd input').fill('*IDN?');
        await expect(consoleButton(page, 'Send')).toBeEnabled();
    });

    ///
    ///What the lock does not touch.
    ///
    ///Clear Log and Save Log act on the log, which is a record rather than a thing on the wire, and the
    ///desktop leaves them alone for that reason. Scripts… is the window doing the running. And Live
    ///Readout is deliberately exempt everywhere - see the next block for why.
    ///
    test('leaves the log buttons and the tool windows alone', async ({ page }) => {
        await quick(page, 'DC V').click();
        await expect(pane(page).locator('.console')).toContainText('MEASure:VOLTage:DC?');

        await pane(page).getByRole('button', { name: /Scripts/ }).click();
        const tool = page.locator('dialog.tool[open]');
        await setScript(page, SLOW);
        await tool.getByRole('button', { name: /^Run$/ }).click();
        await expect(quick(page, 'DC V')).toBeDisabled();

        const during = await lockState(page);
        expect(during.clearLog).toBe(false);
        expect(during.saveLog).toBe(false);
        expect(during.scripts).toBe(false);

        await tool.getByRole('button', { name: /^Stop$/ }).click();
        await expect(quick(page, 'DC V')).toBeEnabled({ timeout: 20000 });
    });

    ///
    ///And the greyed controls say why.
    ///
    ///A row that goes grey with no explanation reads as a fault. The one thing worth knowing is that
    ///this ends by itself.
    ///
    test('the locked controls carry the reason', async ({ page }) => {
        await pane(page).getByRole('button', { name: /Scripts/ }).click();
        const tool = page.locator('dialog.tool[open]');
        await setScript(page, SLOW);
        await tool.getByRole('button', { name: /^Run$/ }).click();
        await expect(quick(page, 'DC V')).toBeDisabled();

        await expect(pane(page).locator('.row.cmd input'))
            .toHaveAttribute('title', /driving this instrument/);
        await expect(quick(page, 'DC V')).toHaveAttribute('title', /driving this instrument/);

        await tool.getByRole('button', { name: /^Stop$/ }).click();
        await expect(quick(page, 'DC V')).toBeEnabled({ timeout: 20000 });
    });
});

test.describe('while a readout is polling', () => {
    ///
    ///Polling holds the link too, and holds it for as long as it runs.
    ///
    test('the console locks itself out, and unlocks on Stop', async ({ page }) => {
        await pane(page).getByRole('button', { name: /Live Readout/ }).click();
        const tool = page.locator('dialog.tool[open]');
        await tool.locator('#every').fill('200');
        await tool.locator('#every').blur();
        await tool.getByRole('button', { name: 'Start' }).click();

        await expect(quick(page, 'DC V')).toBeDisabled();
        await expect(pane(page).locator('.row.cmd input')).toBeDisabled();

        await tool.getByRole('button', { name: 'Stop' }).click();

        await expect(quick(page, 'DC V')).toBeEnabled({ timeout: 20000 });
        await expect(pane(page).locator('.row.cmd input')).toBeEnabled();
    });

    ///
    ///Live Readout stays reachable while the readout is what is running.
    ///
    ///The one exception, and the desktop makes it in the same words: that window is how you stop it. A
    ///lock that took away the button you need to press to release it is a lock with no key.
    ///
    test('Live Readout is the one button a run does not take away', async ({ page }) => {
        await pane(page).getByRole('button', { name: /Live Readout/ }).click();
        const tool = page.locator('dialog.tool[open]');
        await tool.locator('#every').fill('200');
        await tool.locator('#every').blur();
        await tool.getByRole('button', { name: 'Start' }).click();

        await expect(quick(page, 'DC V')).toBeDisabled();
        await expect(consoleButton(page, /Live Readout/)).toBeEnabled();

        await tool.getByRole('button', { name: 'Stop' }).click();
        await expect(quick(page, 'DC V')).toBeEnabled({ timeout: 20000 });
    });

    ///
    ///Closing the window releases it, without pressing Stop.
    ///
    ///Cancelling the stream is what closing the pane comes to, and the hold is given back when the
    ///enumerator is disposed. A lock that outlived the thing holding it would need the page reloading.
    ///
    test('closing the window while it polls releases the console', async ({ page }) => {
        await pane(page).getByRole('button', { name: /Live Readout/ }).click();
        const tool = page.locator('dialog.tool[open]');
        await tool.locator('#every').fill('200');
        await tool.locator('#every').blur();
        await tool.getByRole('button', { name: 'Start' }).click();

        await expect(quick(page, 'DC V')).toBeDisabled();

        await tool.locator('> .tool-head .shut').click();
        await expect(page.locator('dialog.tool[open]')).toHaveCount(0);

        await expect(quick(page, 'DC V')).toBeEnabled({ timeout: 20000 });
    });
});

test.describe('across tabs', () => {
    ///
    ///A second browser watching the same instrument locks too.
    ///
    ///The bench is one shared workspace: the session is a socket the *server* holds, and a console open
    ///somewhere else is looking at the same instrument. A lock that only reached the page that started
    ///the run would be a lock over the one console that already knew.
    ///
    ///The second window is opened before the instrument is connected, because opening the app closes
    ///the bench (§3.6) - opened afterwards, it would take away the console this test is about.
    ///
    test('a console in another tab locks with the run', async ({ page, context }) => {
        const other = await context.newPage();
        await other.goto('/');
        await expect(other.locator('#iface')).toBeVisible({ timeout: 45000 });

        //That opening took the beforeEach's connection with it. Make it again, from the first page -
        //and the second window is told, without having asked, which is the point of the push.
        await connect(page, instrument.address);
        await expect(other.locator('.tabstrip .tab')).toHaveCount(1, { timeout: 20000 });
        await expect(other.locator('.pane:not([hidden]) button.quick').first()).toBeEnabled();

        await pane(page).getByRole('button', { name: /Scripts/ }).click();
        const tool = page.locator('dialog.tool[open]');
        await setScript(page, SLOW);
        await tool.getByRole('button', { name: /^Run$/ }).click();

        //The other tab was told, without having asked.
        await expect(other.locator('.pane:not([hidden]) button.quick').first()).toBeDisabled();
        await expect(other.locator('.pane:not([hidden]) .row.cmd input')).toBeDisabled();

        await tool.getByRole('button', { name: /^Stop$/ }).click();
        await expect(other.locator('.pane:not([hidden]) button.quick').first())
            .toBeEnabled({ timeout: 20000 });

        await other.close();
    });
});
