//
//The settings menu, the two modal boxes, and the tool windows - and the line between the last two.
//
//A tool window is a window: it opens over the work, the page behind it stays live, and it can be moved
//out of the way. A modal is a decision or a statement: nothing behind it is reachable and it does not
//move, because there is nothing behind it to uncover. Both are `<dialog>` here, and which one a box is
//comes down to show() against showModal() - which `:modal` will answer directly.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane, quick, openSettings, closeDialog, boxOf,
        consoleButton, editorReady, settle, styleOf, scriptText, setScript } = require('./helpers');
const { startInstrument } = require('./instrument');
const http = require('node:http');

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument();
});

test.afterAll(async () => {
    await instrument.stop();
});

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
});

///Whether the top dialog is in the top layer - which is what showModal() puts it in, and show() does not.
async function isModal(page, selector) {
    return page.evaluate((sel) => document.querySelector(sel)?.matches(':modal') ?? null, selector);
}

///How far forward one window is, by the name in its title bar. Non-modal dialogs are not in the top
///layer, so the stack is plain z-index and can be read straight off.
///Drag whatever is on top down out of the way. A tool window opens over the console it came from,
///so the button for the next tool is behind it - moving it is what a person does, and the drag is
///there for exactly this.
async function moveAside(page) {
    await settle(page);
    const head = await boxOf(page, 'dialog.tool[open] .tool-head');

    //To the foot of the screen, not merely below the console toolbar. These windows are as tall as
    //the viewport allows, so "under the toolbar" still covered the card above it — and a spec that
    //then pressed something up there failed on the pointer rather than on what it was testing.
    const clear = (await page.evaluate(() => window.innerHeight)) - 80;

    await page.mouse.move(head.left + 30, head.top + head.height / 2);
    await page.mouse.down();
    await settle(page);
    await page.mouse.move(head.left + 30, clear + head.height / 2, { steps: 8 });
    await page.mouse.up();
}

async function zOf(page, title) {
    return page.evaluate((wanted) => {
        const el = [...document.querySelectorAll('dialog.tool[open]')]
            .find((d) => d.querySelector(':scope > .tool-head > .name')?.textContent.trim() === wanted);
        return el ? Number(getComputedStyle(el).zIndex) : null;
    }, title);
}

test.describe('the settings menu', () => {
    ///
    ///Everything the desktop's two menu strips hold, under one gear.
    ///
    ///Tools does things to the bench and Help explains things; that is the line the two are drawn along,
    ///and it is why the command library sits under Help - it acts on nothing.
    ///
    test('holds the theme, Help, AI settings and About', async ({ page }) => {
        const menu = await openSettings(page);

        await expect(menu).toContainText('Theme');
        await expect(menu.locator('.item.branch')).toContainText('Help');
        await expect(menu.getByRole('button', { name: /AI settings/ })).toBeVisible();
        await expect(menu.getByRole('button', { name: 'About' })).toBeVisible();
    });

    ///
    ///The two references are under Help, and the submenu hangs to the left of it.
    ///
    ///A menu in the middle of a title bar has room on that side and not on the other; hung to the right
    ///it opened on top of the menu that spawned it.
    ///
    test('Help opens the two references, clear of its parent', async ({ page }) => {
        const menu = await openSettings(page);
        await menu.locator('.item.branch').hover();

        const sub = menu.locator('.menu.sub');
        await expect(sub).toBeVisible();
        await expect(sub).toContainText('Command library');
        await expect(sub).toContainText('Script language');

        const parent = await boxOf(page, '.settings > .menu');
        const child = await boxOf(page, '.settings .menu.sub');
        expect(child.right).toBeLessThanOrEqual(parent.left + 1);
    });
});

test.describe('the About box', () => {
    test.beforeEach(async ({ page }) => {
        const menu = await openSettings(page);
        await menu.getByRole('button', { name: 'About' }).click();
        await expect(page.locator('dialog.sheet[open]')).toBeVisible();

        //
        //And wait for the facts, not merely for the box.
        //
        //Every figure on it is read at runtime, so the box opens with a blurb and grows when the server
        //answers. It is centred, so growing moves it - which is a race the drag test lost by 82 pixels,
        //reporting a dialog that had moved on its own as one that had been dragged.
        //
        await expect(page.locator('dialog.sheet[open] dt')).toHaveCount(4);
    });

    ///
    ///Every figure on it is read at runtime, so nothing on it can go stale while looking authoritative.
    ///
    test('states the version, the build and the counted totals', async ({ page }) => {
        const sheet = page.locator('dialog.sheet[open]');

        await expect(sheet).toContainText('Lab Equipment Controller');
        await expect(sheet).toContainText(/Version \d+\.\d+\.\d+/);
        await expect(sheet.locator('.pill')).toHaveText('web build');
        await expect(sheet.locator('dd').first()).toContainText(/SCPI commands across \d+ instrument/);
    });

    ///
    ///Four terms, and the last of them says Source Code rather than Source.
    ///
    test('names its four terms', async ({ page }) => {
        await expect(page.locator('dialog.sheet[open] dt'))
            .toHaveText(['Catalogs', 'Server', 'Serving', 'Source Code']);
    });

    ///
    ///The last of them is a link you can follow, not an address to retype.
    ///
    ///The address is read off the assembly rather than written into the page - the same two lines
    ///Core's project file declares for the NuGet listing - so this asserts that something arrived and
    ///that it is the repository, not which repository it is.
    ///
    test('makes the source a link, with the licence beside it', async ({ page }) => {
        const row = page.locator('dialog.sheet[open] dd').last();
        const link = row.locator('a');

        await expect(link).toHaveAttribute('href', /^https?:\/\/.+/);
        await expect(link).toHaveAttribute('href', /github\.com/);
        await expect(link).toHaveText(await link.getAttribute('href'));

        //And the licence is beside it, as text rather than as part of the link: a licence that
        //looked clickable would be a promise to open something that is not there.
        await expect(row).toContainText(/licence/);
    });

    ///
    ///The colon belongs to the layout rather than to the word, so it is drawn rather than typed - which
    ///is why it is not in the text above and has to be asked for separately.
    ///
    test('draws a colon after each term', async ({ page }) => {
        //The list is not in the page until the server has answered - every figure on the box is read at
        //runtime. A locator would wait for that on its own; evaluate() does not, so it is waited for here.
        await expect(page.locator('dialog.sheet[open] dt')).toHaveCount(4);

        const drawn = await page.evaluate(() =>
            [...document.querySelectorAll('dialog.sheet[open] dt')]
                .map((dt) => getComputedStyle(dt, '::after').content));

        expect(drawn).toHaveLength(4);
        for (const c of drawn) expect(c.replace(/"/g, '')).toBe(':');
    });

    ///
    ///It is modal, and it does not move.
    ///
    ///There is nothing behind it to uncover - the page is greyed out and out of reach until it closes -
    ///so moving it would be moving it off the middle for no reason. The desktop's is ShowDialog.
    ///
    test('is modal and cannot be dragged', async ({ page }) => {
        expect(await isModal(page, 'dialog.sheet[open]')).toBe(true);

        //The drag is wired by adding .grip to the head. A modal never gets one.
        await expect(page.locator('dialog.sheet[open] .sheet-head')).not.toHaveClass(/grip/);

        const before = await boxOf(page, 'dialog.sheet[open]');
        const head = await boxOf(page, 'dialog.sheet[open] .sheet-head');
        await page.mouse.move(head.left + 40, head.top + 10);
        await page.mouse.down();
    await settle(page);
        await settle(page);   // the press has to be handled before the moves arrive
        await page.mouse.move(head.left + 240, head.top + 160, { steps: 8 });
        await page.mouse.up();

        const after = await boxOf(page, 'dialog.sheet[open]');
        expect(after.left).toBeCloseTo(before.left, 0);
        expect(after.top).toBeCloseTo(before.top, 0);
    });

    ///
    ///No OK button: it states facts and takes no decision. Esc is the way out, as on the desktop, where
    ///a form with no buttons has no CancelButton and the key had to be wired by hand.
    ///
    test('closes on Escape', async ({ page }) => {
        await closeDialog(page);
    });
});

test.describe('the AI connection box', () => {
    ///
    ///The same fields the desktop's has, and modal like it.
    ///
    test('carries the provider, endpoint, model, key and timeout, and is modal', async ({ page }) => {
        const menu = await openSettings(page);
        await menu.getByRole('button', { name: /AI settings/ }).click();

        const sheet = page.locator('dialog.sheet[open]');
        await expect(sheet).toBeVisible();

        for (const label of ['Provider:', 'Endpoint:', 'Model:', 'API key:', 'Timeout (s):', 'PDF extraction:']) {
            await expect(sheet).toContainText(label);
        }
        await expect(sheet.getByRole('button', { name: 'Apply' })).toBeVisible();

        expect(await isModal(page, 'dialog.sheet[open]')).toBe(true);
    });

    ///
    ///Esc shuts it, as it shuts the About box. A modal is one window, whichever of its fields has the focus.
    ///
    test('closes on Escape', async ({ page }) => {
        const menu = await openSettings(page);
        await menu.getByRole('button', { name: /AI settings/ }).click();
        await expect(page.locator('dialog.sheet[open]')).toBeVisible();
        await closeDialog(page);
    });
});

test.describe('a tool window', () => {
    //Room for a window and the console it came from at the same time. These windows are wide and
    //tall and they open in the middle, so on a short screen there is nowhere to put one that does
    //not cover the toolbar the next one is opened from - and this group is about two at once.
    test.use({ viewport: { width: 1440, height: 1100 } });

    test.beforeEach(async ({ page }) => {
        await connect(page, instrument.address);
        await pane(page).getByRole('button', { name: 'Discover Commands' }).click();
        await expect(page.locator('dialog.tool[open]')).toBeVisible();
    });

    ///
    ///It names the tool *and* the instrument. With several consoles open, which one this was opened
    ///from is the whole question.
    ///
    test('names the tool and the instrument it belongs to', async ({ page }) => {
        const head = page.locator('dialog.tool[open] .tool-head');
        await expect(head).toContainText('Command Reference');
        await expect(head).toContainText(instrument.address);
    });

    ///
    ///It is a window, and both halves of it are the height of the window.
    ///
    ///CommandReferenceForm opens at 1720x1120 clamped to the working area, with its list docked Fill;
    ///CommandLibraryForm docks the viewer Fill in the panel beside it. Here each stood at three fifths
    ///of the viewport whatever the window did - the list at its ceiling, the guide at its floor - so
    ///the shorter of the two carried a strip of empty card under it and the rest of the window height
    ///went to nothing at all.
    ///
    test('gives its height to the list and the guide', async ({ page }) => {
        await settle(page);

        //As much of the screen as there is, which is what the desktop clamp comes to.
        const box = await boxOf(page, 'dialog.tool[open]');
        const view = page.viewportSize();
        expect(box.height).toBeGreaterThan(view.height * 0.9);
        expect(box.width).toBeGreaterThan(view.width * 0.9);

        //The two columns end on the same line.
        const list = await boxOf(page, 'dialog.tool[open] .library > .group:first-child');
        const guide = await boxOf(page, 'dialog.tool[open] .library > .group.guide');
        expect(Math.abs(list.bottom - guide.bottom)).toBeLessThan(1);

        //And the foot of the window stands the same distance from them as its sides do.
        expect(Math.round(box.bottom - guide.bottom))
            .toBe(Math.round(list.left - box.left));

        //Nothing inside is scrolling to make that true.
        const spills = await page.evaluate(() => {
            const body = document.querySelector('dialog.tool[open] .tool-body');
            return body.scrollHeight - body.clientHeight;
        });
        expect(spills).toBeLessThanOrEqual(1);
    });

    ///
    ///Not modal. This is the difference that matters: on the desktop every tool is a real window, and a
    ///box that greyed out the console it was opened from would be greying out the thing you opened it
    ///to look at.
    ///
    test('is not modal, and the page behind it is still reachable', async ({ page }) => {
        expect(await isModal(page, 'dialog.tool[open]')).toBe(false);

        //A modal is in the top layer and puts an inert barrier over the whole page; a non-modal box
        //covers only the ground it stands on. So the question is whether something *outside* its box
        //answers a click - the gear, which is nowhere near a dialog centred in the window. Pressing a
        //quick command instead would prove nothing: the tool is centred, and the console it was opened
        //from is under it.
        await page.locator('.titlebar .ghost').click();
        await expect(page.locator('.settings .menu').first()).toBeVisible();

        //And the tool is still open, because nothing about that press concerned it.
        await expect(page.locator('dialog.tool[open]')).toBeVisible();
    });

    ///
    ///Which is exactly what a modal does not do.
    ///
    ///Stated as a contrast, because "not modal" only means something beside a box that is: with the
    ///About box open the same gear is behind the top layer, and a hit test at its centre lands on the
    ///dialog instead.
    ///
    test('unlike a modal, which puts the page out of reach', async ({ page }) => {
        await page.locator('dialog.tool[open] .shut').click();
        await expect(page.locator('dialog.tool[open]')).toHaveCount(0);

        const menu = await openSettings(page);
        await menu.getByRole('button', { name: 'About' }).click();
        await expect(page.locator('dialog.sheet[open]')).toBeVisible();

        const reachable = await page.evaluate(() => {
            const gear = document.querySelector('.titlebar .ghost').getBoundingClientRect();
            const hit = document.elementFromPoint(gear.left + gear.width / 2, gear.top + gear.height / 2);
            return hit !== null && hit.closest('.titlebar .ghost') !== null;
        });

        expect(reachable, 'the page behind a modal answered a hit test').toBe(false);
    });

    ///
    ///And it can be moved out of the way. A dialog opens in the middle and the middle is where the work
    ///is, so a tool routinely lands on top of the very thing it was opened to look at.
    ///
    test('can be dragged by its title bar', async ({ page }) => {
        await expect(page.locator('dialog.tool[open] .tool-head')).toHaveClass(/grip/);

        await settle(page);
        const before = await boxOf(page, 'dialog.tool[open]');
        const head = await boxOf(page, 'dialog.tool[open] .tool-head');

        //Away from the ✕ and any link in the head: the drag ignores a press that lands on a control.
        await page.mouse.move(head.left + 30, head.top + head.height / 2);
        await page.mouse.down();
    await settle(page);
        await settle(page);   // the press has to be handled before the moves arrive
        await page.mouse.move(head.left + 130, head.top + 90, { steps: 10 });
        await page.mouse.up();

        const after = await boxOf(page, 'dialog.tool[open]');
        expect(after.left - before.left).toBeGreaterThan(50);
        expect(after.top - before.top).toBeGreaterThan(40);
    });

    ///
    ///And it stays put when the drag begins.
    ///
    ///The window is centred by inset: 0 with auto margins, so taking the margins away on their own
    ///drops it into the top-left corner - where it sat until the first mousemove put it back under
    ///the cursor. One flick to the corner and back, on the first drag of every window.
    ///
    test('does not jump to the corner when the drag begins', async ({ page }) => {
        await settle(page);
        const before = await boxOf(page, 'dialog.tool[open]');
        const head = await boxOf(page, 'dialog.tool[open] .tool-head');

        await page.mouse.move(head.left + 30, head.top + head.height / 2);
        await page.mouse.down();
    await settle(page);
        await settle(page);   // the press has to be handled before the moves arrive

        const pressed = await boxOf(page, 'dialog.tool[open]');
        expect(pressed.left).toBeCloseTo(before.left, 0);
        expect(pressed.top).toBeCloseTo(before.top, 0);

        await page.mouse.up();
    });

    ///
    ///Several stand open at once, because on the desktop they are separate windows and nothing
    ///there stops both being up: a command reference is a thing you keep beside the script you are
    ///writing. One dialog switched between tools could only ever hold the last thing pressed.
    ///
    test('several stand open at once', async ({ page }) => {
        await moveAside(page);
        await pane(page).getByRole('button', { name: /Scripts/ }).click();

        await expect(page.locator('dialog.tool[open]')).toHaveCount(2);

        //As a set: which is in front is the next test, and the DOM order is not that anyway.
        //Direct children, because the script editor carries its own AI window inside it and a
        //descendant selector would read that one's title bar too, open or not.
        const names = await page.locator('dialog.tool[open] > .tool-head > .name').allTextContents();
        expect(names.map((n) => n.trim()).sort()).toEqual(['Command Reference', 'Script Editor']);
    });

    ///
    ///And it stays up when another instrument is brought forward. These windows belong to an
    ///instrument rather than to the tab it sits in - on the desktop they are windows, and looking
    ///at another instrument for a moment must not cost you the script you were writing.
    ///
    ///They used to be built inside the console, which is hidden whenever another tab is on top, so
    ///they had to be closed on every switch or be left where nobody could see or dismiss them.
    ///
    test('stays up when another instrument is brought forward', async ({ page }) => {
        const other = await startInstrument();
        try {
            await moveAside(page);

            await connect(page, other.address, { expectTabs: 2 });

            //Connecting brings the new one forward, so the console under the window is the other one.
            await expect(pane(page)).toContainText(other.address);

            //And the window is still up, still saying whose it is.
            await expect(page.locator('dialog.tool[open]')).toHaveCount(1);
            await expect(page.locator('dialog.tool[open] > .tool-head'))
                .toContainText(instrument.address);
        } finally {
            await other.stop();
        }
    });
    ///
    ///And pressing a tool whose window is already open raises it rather than starting a second - or
    ///rebuilding the one that is there, which would throw away whatever was being written in it.
    ///
    ///Dispatched rather than clicked: with two windows up, the console that owns the buttons is
    ///behind both of them, and what is being tested is what the press does, not what it lands on.
    ///
    ///Discover Commands asks the instrument again before it raises anything, and an instrument with
    ///no SYSTem:HELP:HEADers? answers by not answering - so the raise comes after a wait, and the
    ///line in the log is what says the wait is over.
    ///
    test('pressing the same tool again raises it rather than opening another', async ({ page }) => {
        await moveAside(page);
        await pane(page).getByRole('button', { name: /Scripts/ }).click();
        await expect(page.locator('dialog.tool[open]')).toHaveCount(2);
        expect(await zOf(page, 'Script Editor')).toBeGreaterThan(await zOf(page, 'Command Reference'));

        await pane(page).getByRole('button', { name: 'Discover Commands' }).dispatchEvent('click');
        await expect(pane(page).locator('.console'))
            .toContainText(/opening the built-in reference/, { timeout: 20000 });

        await expect(page.locator('dialog.tool[open]')).toHaveCount(2);
        await expect
            .poll(async () => (await zOf(page, 'Command Reference')) - (await zOf(page, 'Script Editor')))
            .toBeGreaterThan(0);
    });

    ///
    ///Esc shuts it, as it shuts everything else here. A non-modal <dialog> never gets that from the
    ///browser, so it is wired by hand.
    ///
    test('closes on Escape', async ({ page }) => {
        await page.locator('dialog.tool[open]').click({ position: { x: 10, y: 10 } });
        await page.keyboard.press('Escape');
        await expect(page.locator('dialog.tool[open]')).toBeHidden();
    });
});

//
//Esc where one thing is open inside another. It belongs to the innermost of them: the window it was
//pressed in, or the menu or list open in that window, and nothing further out. A window opened from
//another sits inside it on the page (the script editor draws its reference and its AI window inside
//itself), so the key bubbled on and shut both, and the script in the outer one went with it.
//
test.describe('Esc with one thing open inside another', () => {
    const SCRIPT = 'PRINT "still here"';

    ///Whether the focus is in a window that is itself inside another.
    const inNested = (page) => page.evaluate(() => !!document.activeElement.closest('dialog.tool dialog.tool'));

    async function openSequences(page) {
        await page.getByRole('button', { name: /Multi-Instrument Scripts/ }).click();
        await editorReady(page);
        await setScript(page, SCRIPT);
        return page.locator('dialog.tool[open]');
    }

    ///
    ///The reference opened from the multi-instrument editor's Snippets menu shuts, and the editor stays
    ///up with its script. On the desktop ScriptReferenceForm closes itself and nothing else.
    ///
    ///It leaves the focus on Snippets, where the desktop leaves it: a drop-down never takes the focus
    ///from its button, and a dialog that shuts gives the focus back. So the next Esc shuts the editor,
    ///with nothing pressed in between. The focus had gone with the menu item the reference was opened
    ///from, onto the page, where no window hears a key.
    ///
    test('shuts the reference and leaves the editor it was opened from', async ({ page }) => {
        const tool = await openSequences(page);
        const snippets = tool.getByRole('button', { name: /^Snippets/ });
        const over = page.locator('dialog.tool[open] dialog.tool[open]');
        const openReference = async () => {
            await snippets.click();
            await tool.getByRole('button', { name: /What all of this means/ }).click();
            await expect(over).toBeVisible();
            expect(await inNested(page)).toBe(true);
        };

        //By its ✕ first, which is the other way it shuts.
        await openReference();
        await over.locator('> .tool-head .shut').click();
        await expect(over).toHaveCount(0);
        await expect(snippets).toBeFocused();

        await openReference();
        await page.keyboard.press('Escape');

        await expect(over).toHaveCount(0);
        await expect(page.locator('dialog.tool[open]')).toHaveCount(1);
        await expect(page.locator('dialog.tool[open] > .tool-head')).toContainText('Multi-Instrument Scripts');
        expect(await scriptText(page)).toBe(SCRIPT);
        await expect(snippets).toBeFocused();

        await page.keyboard.press('Escape');
        await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
    });

    ///
    ///The same for the AI window over a console's Script Editor, which draws it the same way. The focus
    ///goes back to Script with AI, which opened it, and the next Esc shuts the editor.
    ///
    test('shuts the AI window and leaves the Script Editor', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /Scripts/).click();
            await editorReady(page);
            await setScript(page, SCRIPT);

            await page.locator('dialog.tool[open]').getByRole('button', { name: /Script with AI/ }).click();
            const writer = page.locator('dialog.tool[open] dialog.tool[open]');
            await expect(writer.locator('.chat')).toBeVisible();
            await writer.locator('textarea.prompt').click();
            expect(await inNested(page)).toBe(true);

            await page.keyboard.press('Escape');

            await expect(writer).toHaveCount(0);
            await expect(page.locator('dialog.tool[open]')).toHaveCount(1);
            await expect(page.locator('dialog.tool[open] > .tool-head')).toContainText('Script Editor');
            expect(await scriptText(page)).toBe(SCRIPT);
            await expect(page.locator('dialog.tool[open]').getByRole('button', { name: /Script with AI/ }))
                .toBeFocused();

            await page.keyboard.press('Escape');
            await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
        });
    });

    ///
    ///And when its script is used, the AI window shuts with the button that was pressed in it. The
    ///desktop leaves the caret at the start of what was written and the focus in the editor
    ///(Select(0, 0), then Focus()), and so does this: Enter opens a line above the script, and the
    ///next Esc shuts the editor. The focus had gone with the button.
    ///
    test('gives the editor the focus when the AI window\'s script is used', async ({ page, request }) => {
        await withStubModel(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /Scripts/).click();
            await setScript(page, SCRIPT);    // which leaves the caret at its end

            await page.locator('dialog.tool[open]').getByRole('button', { name: /Script with AI/ }).click();
            const writer = page.locator('dialog.tool[open] dialog.tool[open]');
            await expect(writer.locator('.chat')).toBeVisible();
            await ask(page, 'anything', 1);
            await writer.getByRole('button', { name: /Use This Script/ }).click();

            await expect(writer).toHaveCount(0);
            await expect.poll(() => page.evaluate(() => !!document.activeElement.closest('.code.monaco')))
                .toBe(true);
            await page.keyboard.press('Enter');
            expect(await scriptText(page)).toBe('\n# turn 1\n*IDN?');

            await page.keyboard.press('Escape');
            await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
        });
    });

    ///
    ///A menu open in the window takes the key before the window does, and the next Esc shuts the
    ///window. The desktop's Snippets is a drop-down, and a drop-down eats its own Esc.
    ///
    ///It leaves the focus on its button, too, when the focus was on one of its items. The item goes
    ///with the menu, and the focus had gone with it, so the next Esc reached no window.
    ///
    test('shuts a menu before the window it is in', async ({ page }) => {
        const tool = await openSequences(page);
        const snippets = tool.getByRole('button', { name: /^Snippets/ });
        await snippets.click();
        const menu = tool.locator('.menu[aria-label="Snippets"]');
        await expect(menu).toBeVisible();

        await page.keyboard.press('Escape');
        await expect(menu).toHaveCount(0);
        await expect(page.locator('dialog.tool[open]')).toHaveCount(1);
        expect(await scriptText(page)).toBe(SCRIPT);

        //Again, from its first item, reached with Tab.
        await snippets.click();
        await page.keyboard.press('Tab');
        await expect(menu.getByRole('button').first()).toBeFocused();
        await page.keyboard.press('Escape');
        await expect(menu).toHaveCount(0);
        await expect(snippets).toBeFocused();

        await page.keyboard.press('Escape');
        await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
    });

    ///
    ///And the completion list in the editor, which is SequenceForm's own rule: Esc shuts the window
    ///unless the list is showing, and then it shuts the list. Monaco keeps that key itself.
    ///
    test('shuts the completion list before the window', async ({ page }) => {
        await connect(page, instrument.address);
        await consoleButton(page, /Scripts/).click();
        await setScript(page, 'REP');
        await page.keyboard.press('Control+Space');
        const list = page.locator('.monaco-editor .suggest-widget');
        await expect(list).toBeVisible();

        await page.keyboard.press('Escape');
        await expect(list).toBeHidden();
        await expect(page.locator('dialog.tool[open]')).toHaveCount(1);

        await page.keyboard.press('Escape');
        await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
    });
});

///
///The windows that belong to the page rather than to an instrument: the command library and the
///script language. They open from the gear, which is in the title bar and clear of anything a
///centred window covers, so two of them go up without touching the first.
///
test.describe('several windows', () => {
    async function openFromHelp(page, name) {
        const menu = await openSettings(page);
        await menu.locator('.item.branch').hover();
        await menu.locator('.menu.sub').getByRole('button', { name }).click();
    }

    ///
    ///The second does not land exactly on the first. Both open in the middle, and a window sitting
    ///on the one under it to the pixel reads as the same window having changed rather than a new
    ///one having arrived.
    ///
    ///Left only: these are all the same width, so the step is exact there, while the tops differ by
    ///whatever the two windows happen to be tall.
    ///
    test('the second opens stepped off the first', async ({ page }) => {
        await openFromHelp(page, /Command library/);
        await expect(page.locator('dialog.tool[open]')).toHaveCount(1);
        const first = await boxOf(page, 'dialog.tool[open]');

        await openFromHelp(page, /Script language/);
        await expect(page.locator('dialog.tool[open]')).toHaveCount(2);

        const second = await page.locator('dialog.tool[open]').last().boundingBox();
        expect(Math.round(second.x - first.left)).toBe(28);
    });

    ///
    ///Whichever was touched last is in front, which is the whole of what a title bar is for.
    ///
    test('the one pressed comes to the front', async ({ page }) => {
        await openFromHelp(page, /Command library/);
        await openFromHelp(page, /Script language/);
        await expect(page.locator('dialog.tool[open]')).toHaveCount(2);

        //The newest is on top to begin with, as a window just opened should be.
        expect(await zOf(page, 'Script Language Reference'))
            .toBeGreaterThan(await zOf(page, 'Command Library'));

        //A press anywhere inside brings it forward - the handler is on the window, in the capture
        //phase, so a control that swallows the event still raises the window it is in.
        await page.locator('dialog.tool[open]').first().dispatchEvent('mousedown');

        expect(await zOf(page, 'Command Library'))
            .toBeGreaterThan(await zOf(page, 'Script Language Reference'));
    });
});

//
//A window is sized as well as moved, and it is sized by a corner you can see.
//
//Every window in the desktop app has a resizable border; these had `resize: both`, which the browser
//draws as four hairlines in the border colour, in the bottom-right corner - directly under the end of
//the body's scrollbar, where they read as the bottom of the scrollbar rather than as a control.
//
test.describe('sizing a tool window', () => {
    async function openFromHelp(page, name) {
        const menu = await openSettings(page);
        await menu.locator('.item.branch').hover();
        await menu.locator('.menu.sub').getByRole('button', { name }).click();
    }

    ///Drag the corner by this much, and say what the window measured before and after.
    async function dragCorner(page, dx, dy) {
        await settle(page);
        const before = await boxOf(page, 'dialog.tool[open]');
        const grip = await boxOf(page, 'dialog.tool[open] .sizer');

        await page.mouse.move(grip.left + grip.width / 2, grip.top + grip.height / 2);
        await page.mouse.down();
    await settle(page);
        await settle(page);   // the press has to be handled before the moves arrive
        await page.mouse.move(grip.left + grip.width / 2 + dx, grip.top + grip.height / 2 + dy,
                              { steps: 10 });
        await page.mouse.up();

        return { before, after: await boxOf(page, 'dialog.tool[open]') };
    }

    ///
    ///There is a grip, and it is a control rather than a texture: its own patch, its own edge, and
    ///far enough in from the scrollbar to be told apart from it.
    ///
    test('carries a grip in the corner', async ({ page }) => {
        await openFromHelp(page, /Script language/);
        await settle(page);

        const window = await boxOf(page, 'dialog.tool[open]');
        const grip = await boxOf(page, 'dialog.tool[open] .sizer');

        expect(grip.width).toBeGreaterThanOrEqual(16);
        expect(window.bottom - grip.bottom).toBeLessThan(8);
        expect(window.right - grip.right).toBeLessThan(8);
    });

    ///
    ///And dragging it sizes the window.
    ///
    test('the corner makes the window smaller and larger', async ({ page }) => {
        await openFromHelp(page, /Script language/);

        const shrunk = await dragCorner(page, -220, -160);
        expect(Math.round(shrunk.before.width - shrunk.after.width)).toBeCloseTo(220, -1);
        expect(Math.round(shrunk.before.height - shrunk.after.height)).toBeCloseTo(160, -1);

        const grown = await dragCorner(page, 120, 90);
        expect(Math.round(grown.after.width - grown.before.width)).toBeCloseTo(120, -1);
        expect(Math.round(grown.after.height - grown.before.height)).toBeCloseTo(90, -1);
    });

    ///
    ///In a script window the height goes to the editor, which is what ScriptForm does with it: the
    ///editor and the split below it are docked, and the window's corner is what gives them room.
    ///The editor stood at a fixed 46vh whatever the window did, so making the window taller bought
    ///a longer scrollbar and nothing else.
    ///
    test('a script window gives the height to the editor', async ({ page }) => {
        await connect(page, instrument.address);
        await consoleButton(page, /Scripts/).click();
        await editorReady(page);

        const editor = await boxOf(page, 'dialog.tool[open] .code.monaco');
        await dragCorner(page, 0, -240);
        const smaller = await boxOf(page, 'dialog.tool[open] .code.monaco');

        expect(smaller.height).toBeLessThan(editor.height - 40);
    });

    ///
    ///And dragged short, what is in it still fits.
    ///
    ///The plot carries a floor — a picture that can be zero tall is a picture that is — which in a
    ///card that has been given a height is not a floor but a wedge: the strip under the curve went
    ///past the bottom of the card and pushed the status line out of the window. The floor is for a
    ///card with no height of its own.
    ///
    test('what is in a short window still fits it', async ({ page }) => {
        await connect(page, instrument.address);
        await consoleButton(page, /Scripts/).click();
        await editorReady(page);
        await dragCorner(page, 0, -300);

        await page.locator('dialog.tool[open]').getByRole('button', { name: 'Plot', exact: true }).click();

        const spill = await page.evaluate(() => {
            const body = document.querySelector('dialog.tool[open] .tool-body');
            return body.scrollHeight - body.clientHeight;
        });
        expect(spill).toBeLessThanOrEqual(1);
    });

    ///
    ///A window that fills in after it opens is pulled back onto the screen.
    ///
    ///The reference arrives from the server a moment after its window does, and a window pinned
    ///where it was centred while it was still empty grows downwards from that pin. A third of it
    ///hung below the fold, reachable only by dragging the window up.
    ///
    test('stays on the screen when its content arrives', async ({ page }) => {
        await openFromHelp(page, /Script language/);
        await expect(page.locator('dialog.tool[open] pre.sample').first()).toBeVisible();

        const window = await boxOf(page, 'dialog.tool[open]');
        const height = await page.evaluate(() => window.innerHeight);
        expect(window.bottom).toBeLessThanOrEqual(height);
    });
});

//
//The AI script window: what is asked for on top, what came back underneath, both the same shape.
//
///
///Give the server a key, and take it away again.
    ///
///Either AI window shows why it cannot be used rather than the form itself when there is no
///connection, and this run has none: the fixture starts every server with `Ai__ApiKey` emptied so
///that these specs cannot pass or fail according to whether the machine happens to have a key
///saved. The key below is never spent - `Configured` is `ApiKey.Length > 0` and nothing here asks
///a model anything - and it lands in the worker's own temporary data directory, not a real one.
///
async function withKey(request, body) {
    const was = await (await request.get('/api/ai')).json();
    const set = async (apiKey) => {
        const reply = await request.put('/api/ai', {
            data: {
                provider: was.provider,
                endpoint: was.endpoint,
                model: was.model,
                extractTextLocally: was.extractTextLocally,
                timeoutSeconds: was.timeoutSeconds,
                apiKey
            }
        });
        expect(reply.ok()).toBeTruthy();
    };

    await set('e2e-never-spent');
    try { await body(); } finally { await set(''); }
}

test.describe('the AI script window', () => {
    ///
    ///Everything in the window is one measure, and that measure is the window.
    ///
    ///The contents were held to 52rem - a line of prose the width of a wide window is hard to read
    ///back. Asked for directly the other way: this window is dragged to the width its reader wants,
    ///and half of it standing empty is the worse answer. So the transcript, the box and both rows
    ///run edge to edge, and all four still line up with each other.
    ///
    test('runs the contents the full width of the window', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /Scripts/).click();
            await editorReady(page);

            await page.locator('dialog.tool[open]')
                .getByRole('button', { name: /Script with AI/ }).click();
            await expect(page.locator('dialog.tool[open] .chat')).toBeVisible();
            await settle(page);

            const card = await boxOf(page, 'dialog.tool[open] .group.tall');
            const ask = await boxOf(page, 'dialog.tool[open] textarea.prompt');
            const chat = await boxOf(page, 'dialog.tool[open] .chat');
            const row = await boxOf(page, 'dialog.tool[open] .row.promptrow');
            const head = await boxOf(page, 'dialog.tool[open] .row.chathead');

            //One left edge and one right edge for the lot of them.
            expect(chat.width).toBe(ask.width);
            expect(chat.left).toBe(ask.left);
            expect(row.right).toBe(ask.right);
            expect(head.right).toBe(ask.right);

            //And that edge is the card's own, give or take the card's padding — which is the
            //same on both sides, so nothing is a column sitting inside a wider card.
            expect(ask.left - card.left).toBeCloseTo(card.right - ask.right, 0);
            expect(ask.left - card.left).toBeLessThan(20);
            expect(ask.width).toBeGreaterThan(1000);
        });
    });

    ///
    ///The request box opens on a worked request, written in rather than greyed behind it.
    ///
    ///Both editors open on a worked script for the same reason: a blank box teaches nothing about
    ///what to put in it. A placeholder is not that — it cannot be edited, selected or sent, and it
    ///goes the moment you type over it. Written, the window opens on something that already works:
    ///press the button, or change the two numbers in it and press the button.
    ///
    test('opens on a request that already works', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /Scripts/).click();
            await editorReady(page);

            await page.locator('dialog.tool[open]')
                .getByRole('button', { name: /Script with AI/ }).click();
            await expect(page.locator('dialog.tool[open] .chat')).toBeVisible();

            const ask = page.locator('dialog.tool[open] textarea.prompt');
            await expect(ask).not.toHaveValue('');
            expect((await ask.inputValue()).length).toBeGreaterThan(40);

            //Which means the button that sends it is live, rather than waiting to be given a reason.
            await expect(page.locator('dialog.tool[open] .row.promptrow button.primary')).toBeEnabled();

            //And the caption that used to explain what a box is has gone with it. The AI window is
            //the one in front: it was opened from the editor, which is still behind it.
            await expect(page.locator('dialog.tool[open]').last())
                .not.toContainText('Describe what the script should do');
        });
    });

    ///
    ///The conversation takes the window; the box you type in keeps its own height.
    ///
    ///Which is the way round every chat is laid out, and the way ScriptAiForm's splitter now sits:
    ///the history scrolls and the composer does not move. The alternative - splitting the window
    ///evenly between them - gives a paragraph's worth of prose the same room as a transcript of
    ///six scripts, and on a tall window that is half a card of empty box.
    ///
    ///Use This Script then ends where the row above it ends, as Write Script ends where the words
    ///it sends end.
    ///
    test('gives the window to the conversation, with the composer under it', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /Scripts/).click();
            await editorReady(page);

            await page.locator('dialog.tool[open]')
                .getByRole('button', { name: /Script with AI/ }).click();
            await expect(page.locator('dialog.tool[open] .chat')).toBeVisible();
            await settle(page);

            const card = await boxOf(page, 'dialog.tool[open] .group.tall');
            const chat = await boxOf(page, 'dialog.tool[open] .chat');
            const ask = await boxOf(page, 'dialog.tool[open] textarea.prompt');
            const row = await boxOf(page, 'dialog.tool[open] .row.promptrow');
            const foot = await boxOf(page, 'dialog.tool[open] .row.userow');

            //The transcript takes what is left over; the box is one line until something in it
            //needs more.
            expect(chat.height).toBeGreaterThan(ask.height * 4);
            expect(ask.height).toBeLessThan(50);

            //With a clear step between them, rather than the two touching.
            expect(ask.top - chat.bottom).toBeGreaterThan(8);

            //Reading order: transcript, box, the row that sends it, the line that reports.
            expect(chat.bottom).toBeLessThanOrEqual(ask.top);
            expect(ask.bottom).toBeLessThanOrEqual(row.top + 1);
            expect(row.bottom).toBeLessThanOrEqual(foot.top + 1);

            //And the card ends with the last row rather than a hand below it.
            expect(card.bottom - foot.bottom).toBeLessThan(20);
        });
    });

    ///
    ///And the window is the size ScriptAiForm asks for, which is not the editor's.
    ///
    test('opens at the size its own desktop form asks for', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /Scripts/).click();
            await editorReady(page);

            await page.locator('dialog.tool[open]')
                .getByRole('button', { name: /Script with AI/ }).click();
            await expect(page.locator('dialog.tool[open] .chat')).toBeVisible();
            await settle(page);

            //Clamped to the viewport the same way the stylesheet clamps its own, so on a screen
            //smaller than the form asks for it is the screen less its margin.
            const view = page.viewportSize();
            const win = await boxOf(page, 'dialog.tool[open]');
            expect(win.width).toBe(Math.min(1320, view.width - 48));
            expect(win.height).toBe(Math.min(920, view.height - 48));

            //And not the editors' size, which is what a filling window otherwise takes.
            expect(win.width).toBeLessThan(1480);
        });
    });
});

//
//The AI script window as a conversation: every exchange kept, all of it sent back, and one
//button that forgets the lot.
//
///
///A model that answers, without a model.
///
///`withKey` above hands the server a key that is never spent, which is all the layout specs need.
///These are about what actually goes on the wire, so they need something at the other end of it:
///a connection pointed at an OpenAI-compatible endpoint this test is serving. The whole path is
///real - AiClient builds the body, sends it, and reads the reply back through ScriptAuthor - and
///what the stub records is exactly what the model would have been given.
///
async function withStubModel(request, body) {
    const seen = [];
    const server = http.createServer((req, res) => {
        let raw = '';
        req.on('data', (chunk) => { raw += chunk; });
        req.on('end', () => {
            seen.push(JSON.parse(raw));
            const answer = {
                script: `# turn ${seen.length}\n*IDN?`,
                notes: `wrote turn ${seen.length}`,
            };
            res.writeHead(200, { 'content-type': 'application/json' });
            res.end(JSON.stringify({
                choices: [{ message: { content: JSON.stringify(answer) } }],
            }));
        });
    });

    await new Promise((ready) => server.listen(0, '127.0.0.1', ready));
    const port = server.address().port;

    const was = await (await request.get('/api/ai')).json();
    const set = async (data) => {
        const reply = await request.put('/api/ai', { data });
        expect(reply.ok()).toBeTruthy();
    };

    await set({
        provider: 'OpenAiCompatible',
        endpoint: `http://127.0.0.1:${port}`,
        model: 'stub',
        extractTextLocally: was.extractTextLocally,
        timeoutSeconds: was.timeoutSeconds,
        apiKey: 'e2e-stub',
    });

    try {
        await body(seen);
    } finally {
        await set({
            provider: was.provider,
            endpoint: was.endpoint,
            model: was.model,
            extractTextLocally: was.extractTextLocally,
            timeoutSeconds: was.timeoutSeconds,
            apiKey: '',
        });
        await new Promise((closed) => server.close(closed));
    }
}

///Open the writer over a console's script editor, ready to type in.
async function openWriter(page) {
    await connect(page, instrument.address);
    await consoleButton(page, /Scripts/).click();
    await editorReady(page);

    await page.locator('dialog.tool[open]')
        .getByRole('button', { name: /Script with AI/ }).click();
    await expect(page.locator('dialog.tool[open] .chat')).toBeVisible();
}

///Ask for something, and wait for the answer to land in the transcript.
async function ask(page, text, turns) {
    const writer = page.locator('dialog.tool[open]').last();
    await writer.locator('textarea.prompt').fill(text);
    await writer.locator('.row.promptrow button.primary').click();
    await expect(writer.locator('.chat .turn')).toHaveCount(turns, { timeout: 20000 });
}

test.describe('the AI script window as a conversation', () => {
    ///
    ///Every exchange stays on screen, and every one of them goes back with the next request.
    ///
    ///Which is the difference between a chat and a form that happens to remember its last answer:
    ///"now do the same at 5 V" is only a request at all if the thing reading it can see what "the
    ///same" was.
    ///
    test('keeps every exchange, and sends them all back', async ({ page, request }) => {
        await withStubModel(request, async (seen) => {
            await openWriter(page);
            const writer = page.locator('dialog.tool[open]').last();

            await ask(page, 'set a 1 kHz sine at 2 Vpp', 1);
            await ask(page, 'now do the same at 5 Vpp', 2);

            //Both turns are on screen, oldest first, each with what was asked and what came back.
            await expect(writer.locator('.chat .turn')).toHaveCount(2);
            await expect(writer.locator('.chat')).toContainText('set a 1 kHz sine at 2 Vpp');
            await expect(writer.locator('.chat')).toContainText('now do the same at 5 Vpp');
            await expect(writer.locator('.chat')).toContainText('# turn 1');
            await expect(writer.locator('.chat')).toContainText('# turn 2');

            //The model's own note, which this build used to drop on the floor.
            await expect(writer.locator('.chat')).toContainText('wrote turn 1');

            //The box empties after sending, because what was in it is now in the transcript.
            await expect(writer.locator('textarea.prompt')).toHaveValue('');

            //And what the model was given the second time carries the first exchange whole.
            expect(seen).toHaveLength(2);
            const second = seen[1].messages[0].content;
            expect(second).toContain('conversation so far');
            expect(second).toContain('set a 1 kHz sine at 2 Vpp');
            expect(second).toContain('# turn 1');

            //The first request had nothing to carry, which is the other half of the claim.
            expect(seen[0].messages[0].content).not.toContain('conversation so far');
        });
    });

    ///
    ///Clear forgets it, and the next request goes on its own.
    ///
    ///The button exists to stop the request growing, so the test that matters is not that the
    ///screen empties - it is that the transcript stops being sent. A Clear that only cleared the
    ///view would be a button that lies about what it costs to ask the next question.
    ///
    test('clears the conversation, and stops sending it', async ({ page, request }) => {
        await withStubModel(request, async (seen) => {
            await openWriter(page);
            const writer = page.locator('dialog.tool[open]').last();

            await ask(page, 'set a 1 kHz sine at 2 Vpp', 1);

            //What it costs is said before it is spent, which is what makes Clear a decision.
            await expect(writer.locator('.row.chathead')).toContainText('1 turn(s)');

            await writer.locator('.clearchat').click();
            await expect(writer.locator('.chat .turn')).toHaveCount(0);
            await expect(writer.locator('.row.chathead')).toContainText('No conversation yet');
            await expect(writer.locator('.clearchat')).toBeDisabled();

            //And there is nothing left to give the editor, because the drafts went with it.
            await expect(writer.locator('.row.taketurn button')).toHaveCount(0);

            await ask(page, 'read the output back', 1);

            expect(seen).toHaveLength(2);
            expect(seen[1].messages[0].content).not.toContain('conversation so far');
            expect(seen[1].messages[0].content).not.toContain('set a 1 kHz sine at 2 Vpp');
        });
    });

    ///
    ///Closing the window is not clearing it.
    ///
    ///This window does not outlive one answer on the desktop - Use This Script closes it - so a
    ///conversation kept inside it would be a chat of exactly one turn: visible right up until the
    ///moment you took a draft and needed it.
    ///
    test('opens on the conversation it was closed with', async ({ page, request }) => {
        await withStubModel(request, async () => {
            await openWriter(page);
            await ask(page, 'set a 1 kHz sine at 2 Vpp', 1);

            await page.locator('dialog.tool[open]').last().locator('button.shut').click();
            await expect(page.locator('dialog.tool[open] .chat')).toHaveCount(0);

            await page.locator('dialog.tool[open]')
                .getByRole('button', { name: /Script with AI/ }).click();

            const writer = page.locator('dialog.tool[open]').last();
            await expect(writer.locator('.chat .turn')).toHaveCount(1);
            await expect(writer.locator('.chat')).toContainText('set a 1 kHz sine at 2 Vpp');

            //Including the draft it was closed with, and the button that takes it.
            await expect(writer.locator('.row.taketurn button')).toBeEnabled();
        });
    });

    ///
    ///Every answer carries its own Use This Script, and an older one is still reachable.
    ///
    ///One button at the foot of the window could only ever mean the newest answer, which in a
    ///conversation is the wrong one as often as it is the right one: you ask for a change, the
    ///change is worse, and the draft you wanted is three inches up the transcript with nothing to
    ///press. This is the whole reason it moved.
    ///
    test('takes the script from the answer whose button was pressed', async ({ page, request }) => {
        await withStubModel(request, async () => {
            await openWriter(page);
            const writer = page.locator('dialog.tool[open]').last();

            await ask(page, 'set a 1 kHz sine at 2 Vpp', 1);
            await ask(page, 'now do the same at 5 Vpp', 2);

            //One button per answer, and none left at the foot to mean "whichever was last".
            await expect(writer.locator('.row.taketurn button')).toHaveCount(2);
            await expect(writer.locator('.row.userow button')).toHaveCount(0);

            //Press the older one. The script that lands in the editor is that one, not the
            //newest - which is the only claim worth making here.
            await writer.locator('.chat .turn').first().locator('.row.taketurn button').click();

            await expect(page.locator('dialog.tool[open] .chat')).toHaveCount(0);
            expect(await scriptText(page)).toContain('# turn 1');
            expect(await scriptText(page)).not.toContain('# turn 2');
        });
    });

    ///
    ///The request box is one line, and as many as the text needs.
    ///
    ///It stood six lines deep whether or not there was anything in it, which put a paragraph's
    ///worth of empty between the conversation and the row that sends it - and most requests here
    ///are one sentence. A pasted script grows it to a ceiling and then scrolls inside it, rather
    ///than eating the transcript above.
    ///
    test('opens the request box at one line and grows it with what is in it', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /Scripts/).click();
            await editorReady(page);

            await page.locator('dialog.tool[open]')
                .getByRole('button', { name: /Script with AI/ }).click();
            await expect(page.locator('dialog.tool[open] .chat')).toBeVisible();
            await settle(page);

            const box = 'dialog.tool[open] textarea.prompt';
            const one = (await boxOf(page, box)).height;
            expect(one).toBeLessThan(50);

            //A paste is an input event carrying several lines, which is what this is.
            const paste = async (text) => await page.locator(box).evaluate((el, value) => {
                el.value = value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
            }, text);

            //Three more lines is three more line-heights — not three more boxes, because the
            //padding is paid once.
            const line = await page.locator(box).evaluate(
                (el) => parseFloat(getComputedStyle(el).lineHeight));

            await paste('one\ntwo\nthree\nfour');
            const four = (await boxOf(page, box)).height;
            expect(four - one).toBeCloseTo(line * 3, 0);

            //And a hundred lines stop at the ceiling and scroll, rather than taking the window.
            await paste(Array.from({ length: 100 }, (_, i) => 'line ' + i).join('\n'));
            const many = await page.locator(box).evaluate((el) => ({
                height: el.getBoundingClientRect().height,
                scrolls: el.scrollHeight > el.clientHeight,
            }));
            expect(many.scrolls).toBeTruthy();
            expect(many.height).toBeLessThan(300);

            //Emptied, it is one line again — nothing fires input when Blazor clears it, so this
            //is the re-measure after a send rather than a free ride on the paste above.
            await paste('back to one');
            expect((await boxOf(page, box)).height).toBe(one);
        });
    });

    ///
    ///Enter sends it; Shift+Enter starts a new line.
    ///
    ///Which is what Enter does in a chat. A textarea inserts its own newline unless the keystroke
    ///is stopped, and Blazor can only ask for that at render time rather than per-keystroke - so
    ///both halves live in lec.chatbox, and what is checked here is that the right one is stopped.
    ///
    test('sends on Enter and takes a new line on Shift+Enter', async ({ page, request }) => {
        await withStubModel(request, async () => {
            await openWriter(page);
            const writer = page.locator('dialog.tool[open]').last();
            const box = writer.locator('textarea.prompt');

            const enter = async (shift) => await box.evaluate((el, withShift) => {
                const e = new KeyboardEvent('keydown', {
                    key: 'Enter', shiftKey: withShift, bubbles: true, cancelable: true,
                });
                el.dispatchEvent(e);
                return e.defaultPrevented;
            }, shift);

            //Shift+Enter is left alone, so the box takes the line break itself.
            await box.fill('a request in two halves');
            expect(await enter(true)).toBeFalsy();
            await expect(writer.locator('.chat .turn')).toHaveCount(0);

            //Plain Enter is stopped, and sends instead.
            expect(await enter(false)).toBeTruthy();
            await expect(writer.locator('.chat .turn')).toHaveCount(1, { timeout: 20000 });
            await expect(writer.locator('.chat')).toContainText('a request in two halves');
            await expect(box).toHaveValue('');
        });
    });
});

//
//The AI datasheet window: the file on the left, what will read it on the right.
//
test.describe('the AI datasheet window', () => {
    ///
    ///A target, not a strip, and the only way in.
    ///
    ///The desktop has a path box with Browse beside it. A browser will not be talked out of drawing
    ///its own control on a file input, so the box is ours - and being ours it can be the shape of the
    ///thing it does. A second button opening the same picker only asks which of the two to press.
    ///
    test('takes the file in a box with no Browse beside it', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /AI Datasheet Extraction/).click();
            await expect(page.locator('dialog.tool[open] .drop')).toBeVisible();
            await settle(page);

            const tool = page.locator('dialog.tool[open]');
            await expect(tool).not.toContainText('Browse');
            await expect(tool).not.toContainText('Datasheet:');

            //Big enough to aim a dragged file at, and no bigger than that job.
            //
            //It was 22rem by 7.5 and is now 18 by 6, asked for directly: a drop target twice the
            //size of anything else in the window reads as the window's subject rather than as one
            //of its inputs. The floor here is what "a target rather than a strip" means, not the
            //size itself - SPEC 5 states the size.
            const drop = await boxOf(page, 'dialog.tool[open] .drop');
            expect(drop.width).toBeGreaterThan(240);
            expect(drop.height).toBeGreaterThan(80);

            //The two of them stand in a frame of their own, and the box is as tall as the frame -
            //which is to say the column sets the height and the target fills it. They are one
            //control between them, a file and what will be done with it, and a short box beside a
            //long column read as two unrelated things that happened to be next to each other.
            await expect(page.locator('dialog.tool[open] .dropcard')).toBeVisible();
            const side = await boxOf(page, 'dialog.tool[open] .dropside');
            expect(Math.abs(drop.height - side.height)).toBeLessThan(2);

            //And it is still the button that opens the picker.
            await expect(tool.locator('.drop')).toHaveAttribute('role', 'button');
        });
    });

    ///
    ///Which connection to spend is asked here, not stated.
    ///
    ///There can be several - the cheap fast model that suits reading commands out of a guide is not
    ///the one you want writing a sequence - and this is the window where you are looking at the file
    ///that decides which. Choosing here chooses everywhere: one selection for the app, so this window
    ///and the settings box cannot disagree about which key is being spent.
    ///
    test('offers the connections to spend, and picking one picks it everywhere', async ({ page, request }) => {
        await withKey(request, async () => {
            //A second connection to choose between, with a key of its own — adding one selects
            //it, and a selected connection with no key turns the tool off rather than showing it.
            const added = await request.post('/api/ai/connections?provider=Anthropic');
            expect(added.ok()).toBeTruthy();
            const second = (await added.json()).status.selectedId;
            await request.put('/api/ai', { data: {
                id: second, provider: 'Anthropic', endpoint: 'https://api.anthropic.com',
                model: 'claude-sonnet-5', extractTextLocally: null, timeoutSeconds: 300,
                apiKey: 'e2e-never-spent' } });

            try {
                await connect(page, instrument.address);
                await consoleButton(page, /AI Datasheet Extraction/).click();
                await expect(page.locator('dialog.tool[open] .dropside')).toBeVisible();
                await settle(page);

                const side = page.locator('dialog.tool[open] .dropside');
                await expect(side).toContainText('Using:');

                const picker = side.locator('select.conn');
                await expect(picker.locator('option')).toHaveText(['gemini-3.6-flash', 'claude-sonnet-5']);

                //It opens on what the server says is in force, which is the one just added.
                const status = await (await request.get('/api/ai')).json();
                await expect(picker).toHaveValue(status.selectedId);

                //And choosing the other one is choosing it for the server, not for this window.
                const other = status.connections.find(c => c.id !== status.selectedId);
                await picker.selectOption(other.id);
                await expect.poll(async () => {
                    const now = await (await request.get('/api/ai')).json();
                    return now.selectedId;
                }).toBe(other.id);
            } finally {
                const now = await (await request.get('/api/ai')).json();
                const extra = now.connections.find(c => c.provider.includes('Anthropic'));
                if (extra) await request.delete(`/api/ai/connections/${extra.id}`);
            }
        });
    });

    ///
    ///How hard the model works is asked here as well as in the settings box.
    ///
    ///Same reason as the PDF switch beside it: it is a decision taken while looking at the file it
    ///applies to. One scale for every provider - Core translates it - and the first step is the
    ///absence of the request rather than a level of it, which is what an endpoint that has never
    ///heard of the field needs.
    ///
    ///
    ///It opens at the size DatasheetExtractForm asks for, not at the whole viewport.
    ///
    ///A filling window with no size of its own takes the script editors', because they were the only
    ///ones that filled - which gave a drop box and a grid the whole screen. SPEC 5 names both sizes.
    ///
    test('opens at the size its own desktop form asks for', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /AI Datasheet Extraction/).click();
            await expect(page.locator('dialog.tool[open] .drop')).toBeVisible();
            await settle(page);

            const view = page.viewportSize();
            const box = await boxOf(page, 'dialog.tool[open]');

            //Clamped to the viewport the same way the rule is, so this asks for whichever is smaller.
            expect(box.width).toBeCloseTo(Math.min(980, view.width - 48), 0);
            expect(box.height).toBeCloseTo(Math.min(620, view.height - 48), 0);
        });
    });

    test('offers the effort the model will use, starting at the provider default', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /AI Datasheet Extraction/).click();
            await expect(page.locator('dialog.tool[open] .dropside')).toBeVisible();
            await settle(page);

            const side = page.locator('dialog.tool[open] .dropside');
            await expect(side).toContainText('Effort:');

            const effort = side.locator('select.effort');
            await expect(effort).toHaveValue('Default');
            await expect(effort.locator('option')).toHaveText(
                ['Provider default', 'Minimal', 'Low', 'Medium', 'High']);

            //Applied to the server rather than remembered here, so this window and the settings
            //box cannot disagree about what is in force.
            await effort.selectOption('High');
            await expect.poll(async () => {
                const status = await (await request.get('/api/ai')).json();
                return status.effort;
            }).toBe('High');

            await effort.selectOption('Default');
        });
    });

    ///
    ///What will read the file stands beside the file, not under it. Three short rows across the
    ///width of a wide window is three rows of mostly nothing.
    ///
    test('puts what reads it, the switch and Extract in a column beside the box', async ({ page, request }) => {
        await withKey(request, async () => {
            await connect(page, instrument.address);
            await consoleButton(page, /AI Datasheet Extraction/).click();
            await expect(page.locator('dialog.tool[open] .dropside')).toBeVisible();
            await settle(page);

            const side = page.locator('dialog.tool[open] .dropside');
            await expect(side).toContainText('Using:');
            await expect(side).toContainText('Extract text locally before sending');
            await expect(side.getByRole('button', { name: 'Extract', exact: true })).toBeVisible();

            const drop = await boxOf(page, 'dialog.tool[open] .drop');
            const column = await boxOf(page, 'dialog.tool[open] .dropside');
            expect(column.left).toBeGreaterThanOrEqual(drop.right);
            expect(column.top).toBeLessThan(drop.bottom);
        });
    });
});

//
//The command reference: CommandReferenceForm's list, which is the one list in the desktop app
//that is not drawn as a grid.
//
test.describe('the command reference', () => {
    async function openCatalog(page) {
        await connect(page, instrument.address);
        await consoleButton(page, /Discover Commands/).click();
        await expect(page.locator('dialog.tool[open] table.cmdref tbody tr').first()).toBeVisible();
        await settle(page);
        return page.locator('dialog.tool[open]');
    }

    ///
    ///Three columns, and the category is not one of them. The desktop builds a ListViewGroup per
    ///category and heads the group with its name; a column would print the same word on every row
    ///of a run of thirty.
    ///
    test('heads each category rather than repeating it down a column', async ({ page }) => {
        const tool = await openCatalog(page);

        expect((await tool.locator('table.cmdref thead th').allTextContents())
            .map((t) => t.trim())).toEqual(['✓', 'Command', 'Description']);

        const groups = await tool.locator('table.cmdref tr.cat').allTextContents();
        expect(groups.length).toBeGreaterThan(1);
        expect(new Set(groups).size).toBe(groups.length);   // each category headed once

        //A heading spans the list rather than sitting in a cell of it.
        expect(await tool.locator('table.cmdref tr.cat td').first().getAttribute('colspan')).toBe('3');
    });

    ///
    ///One line per command, at the size the window sets. A ListView row is one line; a description
    ///that wrapped to three turned a list you read by running your eye down the left edge into one
    ///you had to step through.
    ///
    test('gives every command one line, at the window\'s own size', async ({ page }) => {
        const tool = await openCatalog(page);

        const rows = await page.evaluate(() => {
            const body = document.querySelectorAll('dialog.tool[open] table.cmdref tbody tr:not(.cat)');
            return [...body].slice(0, 40).map((r) => Math.round(r.getBoundingClientRect().height));
        });
        expect(rows.length).toBeGreaterThan(10);

        //Every row the same height, and that height one line of 12px text plus its padding.
        expect(new Set(rows).size).toBe(1);
        expect(rows[0]).toBeLessThan(26);

        const size = await styleOf(page, 'dialog.tool[open] table.cmdref', 'font-size');
        expect(parseFloat(size)).toBe(12);

        //Nothing wraps: what will not fit is cut with an ellipsis, and carries the whole of it as
        //a tooltip, which is where the desktop puts it too.
        expect(await styleOf(page, 'dialog.tool[open] table.cmdref td.desc', 'white-space')).toBe('nowrap');
        expect(await styleOf(page, 'dialog.tool[open] table.cmdref td.desc', 'text-overflow')).toBe('ellipsis');
        await expect(tool.locator('table.cmdref td.desc').first()).toHaveAttribute('title', /\S/);
    });

    ///
    ///The filter belongs to the list, so it sits on the list's caption line and at the end of it.
    ///
    ///It was on the row that picks the catalog, where it read as a second thing to set before
    ///anything would appear. CommandReferenceForm gives it a strip of its own directly over the list.
    ///
    test('filters from the corner of the card it filters', async ({ page }) => {
        const tool = await openCatalog(page);
        const filter = tool.locator('.cap-row input[type=text]');

        await expect(tool.locator('.group').first().locator('input[type=text]')).toHaveCount(0);
        await expect(filter).toBeVisible();

        //Hard against the right-hand end of the line it shares with the caption.
        const line = await boxOf(page, 'dialog.tool[open] .cap-row');
        const box = await boxOf(page, 'dialog.tool[open] .cap-row input[type=text]');
        expect(line.right - box.right).toBeLessThan(2);

        //And it still filters.
        await filter.fill('CONFigure');
        await expect(tool.locator('table.cmdref td.cmd').first()).toContainText('CONFigure');
    });

    ///
    ///Double-click takes the command - CommandReferenceForm's
    ///`_list.DoubleClick += (_, _) => InsertSelected()`.
    ///
    ///Into the box, not onto the wire: what to send and when to send it stay two decisions.
    ///
    test('a double-click puts the command in the console box', async ({ page }) => {
        const tool = await openCatalog(page);

        const row = tool.locator('table.cmdref tbody tr:not(.cat)').nth(3);
        const wanted = (await row.locator('td.cmd').textContent()).trim();
        await row.dblclick();

        await expect(pane(page).locator('input.mono')).toHaveValue(wanted);

        //In the box only. Nothing went to the instrument.
        expect(instrument.received.some((c) => c.trim() === wanted)).toBe(false);
    });
});

//
//The language reference: two languages, one window, and examples painted by the editor's own
//colouriser - which is what the desktop's does, out of the tokenizer its editor is built on.
//
test.describe('the language reference', () => {
    ///Every colour used in the painted examples, as the browser computes them.
    ///
    ///Waits for the painting: Monaco has to be fetched before it can colour anything, so the words
    ///are on screen for a moment as plain text before they are anything else.
    async function inkOf(page) {
        await page.waitForSelector('dialog.tool[open] pre.sample span', { timeout: 30000 });
        return page.evaluate(() => {
            const ink = new Set();
            for (const el of document.querySelectorAll('dialog.tool[open] pre.sample span')) {
                if (el.textContent.trim()) ink.add(getComputedStyle(el).color);
            }
            return [...ink];
        });
    }

    async function openReference(page) {
        const menu = await openSettings(page);
        await menu.locator('.item.branch').hover();
        await menu.locator('.menu.sub').getByRole('button', { name: /Script language/ }).click();
        await expect(page.locator('dialog.tool[open] pre.sample').first()).toBeVisible();
    }

    ///
    ///A switch, not a link across to the other one. They are peers - two languages this one window
    ///shows one of - and a link says "go somewhere else", which is the wrong thing to say inside a
    ///window that is already open beside the script it is being read for.
    ///
    test('switches between the two languages', async ({ page }) => {
        await openReference(page);
        const tool = page.locator('dialog.tool[open]');

        await expect(tool.locator('.seg button')).toHaveText(['Single instrument', 'Multi-instrument']);
        await expect(tool.locator('.seg button.on')).toHaveText(['Multi-instrument']);
        await expect(tool.locator('pre.sample').first()).toContainText('DEVICE');

        await tool.getByRole('button', { name: 'Single instrument' }).click();

        await expect(tool.locator('.seg button.on')).toHaveText(['Single instrument']);
        await expect(tool.locator('pre.sample').first()).not.toContainText('DEVICE');
    });

    ///
    ///Every example carries its own Copy.
    ///
    ///They are things to try, and the way to try one is to have it in the editor. The desktop's
    ///reference is a rich text box, where this is select-and-Ctrl+C; here the words are painted
    ///spans and selecting them is fiddlier than it should be.
    ///
    test('copies an example to the clipboard', async ({ page }) => {
        await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
        await openReference(page);

        const tool = page.locator('dialog.tool[open]');
        const copies = tool.locator('.samplebox .copy');
        await expect(copies).toHaveCount(await tool.locator('pre.sample').count());

        //After the colouriser has finished with it. colorizeElement empties the element before it
        //writes the painted spans back, and a read landing in that gap sees an empty example and
        //compares it against a clipboard that is perfectly correct.
        await expect(tool.locator('pre.sample').first().locator('span').first())
            .toBeAttached({ timeout: 30000 });

        await copies.first().click();

        //It says so, because a copy that leaves no mark is one people press twice to be sure.
        await expect(copies.first()).toContainText('Copied');

        const clipboard = await page.evaluate(() => navigator.clipboard.readText());
        //innerText rather than textContent: the colouriser writes each line as a span with a <br>
        //between them, and textContent runs the lines together into one.
        const shown = await tool.locator('pre.sample').first().innerText();

        //Compared line by line rather than byte for byte: the colouriser writes runs of spaces as
        //non-breaking ones, so what is on screen and what went to the clipboard are the same text
        //spelt with two different space characters.
        const tidy = (t) => t
            .replace(new RegExp(String.fromCharCode(160), 'g'), ' ')
            .split(/\r?\n/)
            .map((line) => line.replace(/\s+$/, ''))
            .join('\n')
            .trim();
        expect(tidy(clipboard)).toBe(tidy(shown));
    });

    ///
    ///And the page still has its examples afterwards.
    ///
    ///Painting is done once per element and skipped after that, because painting an already-painted
    ///one destroys it: colorizeElement replaces the text with spans and writes runs of spaces as
    ///non-breaking ones, so feeding its own output back through it produces an empty element. The
    ///guard compared the text against what the element was painted *from*, which after painting it
    ///never equals again - so the first re-render for any reason wiped every example on the page,
    ///and pressing Copy is a re-render.
    ///
    test('and leaves every example still on the page', async ({ page }) => {
        await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
        await openReference(page);

        const tool = page.locator('dialog.tool[open]');
        await expect(tool.locator('pre.sample').first().locator('span').first())
            .toBeAttached({ timeout: 30000 });

        const before = await tool.locator('pre.sample').allTextContents();
        expect(before.filter((t) => t.trim().length > 0)).toHaveLength(before.length);

        await tool.locator('.samplebox .copy').first().click();
        await expect(tool.locator('.samplebox .copy').first()).toContainText('Copied');

        expect(await tool.locator('pre.sample').allTextContents()).toEqual(before);
    });

    ///
    ///And the examples are coloured, by the same grammar the editor uses, so that what is read here
    ///looks like what is typed there. Painted again when the language changes under them.
    ///
    test('paints its examples the way the editor paints them', async ({ page }) => {
        await openReference(page);

        //More than one colour among the words of an example is the whole claim: a keyword, a number
        //and a comment are three different things and the picture says so. Monaco paints through its
        //own token classes, so the colours are read back computed rather than off the elements.
        expect((await inkOf(page)).length).toBeGreaterThan(1);

        //And painted again when the language changes under them.
        await page.locator('dialog.tool[open]').getByRole('button', { name: 'Single instrument' }).click();
        await expect(page.locator('dialog.tool[open] pre.sample').first()).not.toContainText('DEVICE');
        expect((await inkOf(page)).length).toBeGreaterThan(1);
    });
});
