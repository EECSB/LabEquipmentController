//
//The windows that are also routes, moving between a dialog and a browser tab.
//
//Three of the desktop's windows are pages here as well - the command library, the script language
//reference and the multi-instrument editor - so each can be a dialog over the bench or a tab beside
//it. "Open in a tab" moves it: the window closes here as the tab opens, because a window that moved
//and a window that multiplied look the same until you close one of them.
//
//And closing the tab moves it back. That is the round trip a detached console already makes, and
//without it the window has simply gone: getting it back means remembering which menu it came from.
//
//It is a roll-call rather than a farewell from the tab. A postMessage sent from `pagehide` does not
//survive the document being torn down - tried, and it does not arrive - and the one occasion it
//would survive, a reload, is the one occasion it would be a lie.
//
const { test, expect } = require('./fixtures');
const { freshBench, openSettings, settle, BOOT_MS } = require('./helpers');

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
});

///The Command Library, as a window over the bench.
async function openLibrary(page) {
    const menu = await openSettings(page);
    await menu.locator('.item.branch').hover();
    await menu.locator('.menu.sub').getByRole('button', { name: /Command library/ }).click();
    await expect(page.locator('dialog.tool[open] .tool-head')).toContainText('Command Library');
}

///The Script Language Reference, as a window over the bench.
async function openLanguage(page) {
    const menu = await openSettings(page);
    await menu.locator('.item.branch').hover();
    await menu.locator('.menu.sub').getByRole('button', { name: /Script language/ }).click();
    await expect(page.locator('dialog.tool[open] .tool-head')).toContainText('Script Language Reference');
}

///Move the window on top into a tab of its own, and wait for the tab to be there.
async function moveOut(page, context, ready) {
    const [tab] = await Promise.all([
        context.waitForEvent('page'),
        page.locator('dialog.tool[open] > .tool-head a.btn').click(),
    ]);
    await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
    await expect(tab.locator(ready)).toBeVisible({ timeout: BOOT_MS });
    return tab;
}

///Close the tab, and wait for the bench to notice and bring the window back.
async function bringHome(page, tab, title) {
    await tab.close();
    await page.bringToFront();
    await expect(page.locator('dialog.tool[open] > .tool-head')).toContainText(title, { timeout: 20000 });
}

test.describe('open in a tab', () => {
    ///
    ///The window moves rather than multiplying: it closes here as the tab opens.
    ///
    test('moves the window into the tab', async ({ page, context }) => {
        await openLibrary(page);

        const [tab] = await Promise.all([
            context.waitForEvent('page'),
            page.locator('dialog.tool[open] .tool-head a.btn').click(),
        ]);

        try {
            await expect(page.locator('dialog.tool[open]')).toHaveCount(0);

            await tab.waitForLoadState();
            await expect(tab.locator('#cat')).toBeVisible({ timeout: BOOT_MS });
            expect(new URL(tab.url()).pathname).toBe('/catalog');
        } finally {
            await tab.close();
        }
    });

    ///
    ///And closing that tab brings it back, on the tab that sent it out.
    ///
    ///The roll-call goes out when the bench is looked at again, which is exactly when a tab that has
    ///just been closed needs noticing: whatever was heard from and is no longer there has gone.
    ///
    test('closing the tab brings the window back', async ({ page, context }) => {
        await openLibrary(page);

        const [tab] = await Promise.all([
            context.waitForEvent('page'),
            page.locator('dialog.tool[open] .tool-head a.btn').click(),
        ]);

        //Heard from: the tab announces itself as it loads, and only a window that has been heard
        //from is one whose silence means anything.
        await expect(tab.locator('#cat')).toBeVisible({ timeout: BOOT_MS });
        await settle(page);

        await tab.close();

        //Coming back to the bench is what asks the question.
        await page.bringToFront();
        await expect(page.locator('dialog.tool[open] .tool-head'))
            .toContainText('Command Library', { timeout: 20000 });
    });

    ///
    ///A tab that is still open is left alone, however often the bench is looked at.
    ///
    test('leaves the window out while its tab is still open', async ({ page, context }) => {
        await openLibrary(page);

        const [tab] = await Promise.all([
            context.waitForEvent('page'),
            page.locator('dialog.tool[open] .tool-head a.btn').click(),
        ]);

        try {
            await expect(tab.locator('#cat')).toBeVisible({ timeout: BOOT_MS });

            //Three roll-calls, and the window stays where it was put.
            for (let i = 0; i < 3; i++) {
                await page.bringToFront();
                await settle(page);
                await tab.bringToFront();
                await settle(tab);
            }

            await page.bringToFront();
            await settle(page);
            await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
        } finally {
            await tab.close();
        }
    });
});

test.describe('what goes with the window', () => {
    ///
    ///The library takes the catalog it is showing and the filter over it, both ways: the tab opens on
    ///them, and the window comes home on whatever the tab was left on. Opened afresh after that, it
    ///is a new window again, on the list of catalogs.
    ///
    test('the library takes its catalog and its filter, and brings them back', async ({ page, context }) => {
        await openLibrary(page);
        const dialog = page.locator('dialog.tool[open]');
        await dialog.locator('#cat').selectOption({ index: 1 });
        const family = await dialog.locator('#cat').inputValue();
        expect(family).not.toBe('');
        await dialog.locator('input.filter').fill('VOLT');

        const tab = await moveOut(page, context, '#cat');
        await expect(tab.locator('#cat')).toHaveValue(family);
        await expect(tab.locator('input.filter')).toHaveValue('VOLT');

        await tab.locator('input.filter').fill('CURR');
        await expect.poll(() => page.evaluate(() => localStorage.getItem('lec.carried.catalog')))
            .toContain('CURR');

        await bringHome(page, tab, 'Command Library');
        await expect(dialog.locator('#cat')).toHaveValue(family);
        await expect(dialog.locator('input.filter')).toHaveValue('CURR');

        await dialog.locator('> .tool-head .shut').click();
        await expect(dialog).toHaveCount(0);
        await openLibrary(page);
        await expect(dialog.locator('#cat')).toHaveValue('');
        await expect(dialog.locator('input.filter')).toHaveValue('');
    });

    ///
    ///And the reference takes the language it is showing. The link it moves by cannot say which, so
    ///the tab puts its own address right as it opens; switched in the tab, it comes home on the one
    ///it was switched to.
    ///
    test('the reference takes its language, and brings it back', async ({ page, context }) => {
        await openLanguage(page);
        const dialog = page.locator('dialog.tool[open]');
        const single = (where) => where.getByRole('button', { name: 'Single instrument', exact: true });
        const multi = (where) => where.getByRole('button', { name: 'Multi-instrument', exact: true });
        await single(dialog).click();
        await expect(single(dialog)).toHaveClass(/\bon\b/);

        const tab = await moveOut(page, context, '.seg');
        await expect(single(tab)).toHaveClass(/\bon\b/);
        expect(new URL(tab.url()).search).toContain('sequence=false');

        await multi(tab).click();
        await expect(multi(tab)).toHaveClass(/\bon\b/);
        expect(new URL(tab.url()).search).toContain('moved=1');   // still the window, after the switch
        await expect.poll(() => page.evaluate(() => localStorage.getItem('lec.carried.script-language')))
            .toContain('true');

        await single(tab).click();
        await expect(single(tab)).toHaveClass(/\bon\b/);
        await expect.poll(() => page.evaluate(() => localStorage.getItem('lec.carried.script-language')))
            .toContain('false');

        await bringHome(page, tab, 'Script Language Reference');
        await expect(single(dialog)).toHaveClass(/\bon\b/);
    });
});
