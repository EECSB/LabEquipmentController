//
//The screen capture window, and what it says while the picture is still on its way.
//
//The desktop takes the bitmap first and builds the viewer around it, so its window never exists without
//a picture in it and there is nothing in there to press. Here the window is open before the transfer
//starts, which means the window itself has to say what it is doing - and what it said was the sentence
//for having no instrument at all.
//
//The fake answers as a scope, because that is the family with a screen to capture. It cannot actually
//produce a bitmap - its reply is not an IEEE block - so the capture fails, which is beside the point:
//this is about the state the window is in while it waits.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane } = require('./helpers');
const { startInstrument } = require('./instrument');

///A Rigol scope, which is a profile carrying a screen capture command. The serial says what it is.
const SCOPE = 'RIGOL TECHNOLOGIES,DS2202,LEC-E2E-0002,00.01.00';

///Long enough that the wait is a state a person would see rather than a frame.
const SLOW_MS = 1200;

let scope;

test.beforeAll(async () => {
    scope = await startInstrument({ identity: SCOPE, delayMs: SLOW_MS });
});

test.afterAll(async () => {
    await scope.stop();
});

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await connect(page, scope.address);
});

test.describe('the screen capture window', () => {
    ///
    ///It says it is capturing, with a ring that turns - and never says the other thing.
    ///
    ///The desktop says the same word in the console the moment it starts: capturing screen, and the
    ///command it is sending. Here the window stood on the branch for having no instrument for the whole
    ///of the transfer, because the session is fetched in the same await as the capture and nothing is
    ///rendered in between: several seconds of a window saying the connection is missing while it was in
    ///the middle of using it.
    ///
    ///Watched from before the press rather than sampled after it. The wrong sentence appearing for one
    ///frame is the whole of the defect, and an assertion that arrived late would miss exactly that.
    ///
    test('says it is capturing, and never that there is no instrument', async ({ page }) => {
        await page.evaluate(() => {
            window.__lec = { wrong: false, capturing: false, spun: false };
            const look = () => {
                const win = document.querySelector('dialog.tool[open]');
                if (!win) return;
                const text = win.innerText || '';
                if (text.includes('Connect an instrument first')) window.__lec.wrong = true;
                if (/Capturing/.test(text)) window.__lec.capturing = true;
                if (win.querySelector('.spin')) window.__lec.spun = true;
            };
            new MutationObserver(look).observe(document.body,
                { subtree: true, childList: true, characterData: true });
            window.__lecTick = setInterval(look, 20);
        });

        await pane(page).getByRole('button', { name: 'Capture Screen' }).click();
        await expect(page.locator('dialog.tool[open]')).toBeVisible();

        //While it is in flight: a ring, and the word.
        await expect(page.locator('dialog.tool[open] .spin')).toBeVisible();
        await expect(page.locator('dialog.tool[open]')).toContainText(/Capturing/);

        //Then it is over, one way or the other.
        await expect(page.locator('dialog.tool[open] .spin')).toHaveCount(0, { timeout: 30000 });

        const said = await page.evaluate(() => {
            clearInterval(window.__lecTick);
            return window.__lec;
        });

        expect(said.spun).toBe(true);
        expect(said.capturing).toBe(true);
        expect(said.wrong, 'it said there was no instrument while it was using one').toBe(false);
    });
});
