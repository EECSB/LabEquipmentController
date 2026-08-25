//Shared helpers for the web client's e2e specs: open the bench, put the fake instrument on it, find the
//console that is on top, and measure what was drawn.
//
//Two things shape all of this.
//
//**The bench lives on the server.** A session is a socket the server holds; the browser only draws it.
//So a reload does not disconnect anything, two tabs see the same instruments, and a spec cannot start
//clean merely by navigating - it has to say so. freshBench does.
//
//**Most of what is worth asserting here is a distance.** UI-SPEC §1 is a table of numbers, and the way
//to check a number is to read it back off the running page rather than to look at it. gap() and boxOf()
//are that, and every measurement in the specs goes through them.
const { expect } = require('@playwright/test');

///How long to wait for the WASM runtime before giving up on the page having loaded at all. Longer than
///an assertion timeout because the first navigation of a run pays for the runtime download.
const BOOT_MS = 45000;

///
///Open the bench and wait for Blazor to have rendered it.
///
///Waits on the interface picker rather than on `.workspace`: the shell is server-rendered and appears
///before the runtime has started, so a spec that waited for the card would go on to click a button that
///was not yet wired to anything.
///
async function gotoBench(page) {
    await page.goto('/');
    await expect(page.locator('#iface')).toBeVisible({ timeout: BOOT_MS });
    return page;
}

///
///Disconnect everything on the bench, whoever opened it.
///
///Through the API rather than through the tab's ✕, because the point is to be independent of what the
///last spec left behind - including a session whose browser tab is long gone. `DELETE /api/sessions/{id}`
///is the same path the ✕ takes, so this releases the instrument properly rather than dropping the socket.
///
async function clearBench(request) {
    const open = await (await request.get('/api/sessions')).json();
    for (const s of open) await request.delete(`/api/sessions/${s.id}`);
}

///
///A bench with nothing on it, then the page. Use this in beforeEach.
///
///Clearing before rather than after: a spec that fails part way through never reaches its own cleanup,
///and the next one should not inherit that.
///
async function freshBench(page, request) {
    await clearBench(request);
    return gotoBench(page);
}

///
///Type an address into the Address box and press Connect, then wait for its tab.
///
///Waits on the tab count rather than on a fixed pause: connecting opens a socket and asks `*IDN?`, and
///how long that takes is the instrument's business.
///
///
///Exact, because a role name matches on a substring by default and every open tab carries a
///`Disconnect <address> and close this tab` beside it - so connecting a *second* instrument
///found two buttons and refused to guess.
///
async function connect(page, address, { expectTabs = 1 } = {}) {
    await page.locator('#addr').fill(address);
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.tabstrip .tab')).toHaveCount(expectTabs, { timeout: 20000 });
}

///The console pane that is on top. The others stay in the render tree with `hidden` set - that is how a
///tab keeps its log - so every locator in a console spec has to start here rather than at the document.
function pane(page) {
    return page.locator('.group.consoles .pane:not([hidden])');
}

///Bring a tab to the front by the name on it.
async function selectTab(page, name) {
    await page.locator('.tabstrip .tab', { hasText: name }).locator('.pick').click();
    await expect(pane(page)).toContainText(name);
}

///
///One of the console's quick-command buttons, by the label printed on it.
///
///Anchored and escaped: the labels are SCPI-ish, so `*IDN?` reaches a regex carrying two metacharacters
///of its own - and unescaped, `N?` makes the N optional and the pattern matches half the strip.
///
function quick(page, label) {
    const literal = label.replace(/[\\^$.*+?()[\]{}|]/g, '\\$&');
    return pane(page).locator('button.quick').filter({ hasText: new RegExp(`^\\s*${literal}\\s*$`) });
}

///A button anywhere in the console that is on top.
function consoleButton(page, name) {
    return pane(page).getByRole('button', { name });
}

///The chips in the queue strip, oldest first.
function queueChips(page) {
    return pane(page).locator('.queue .chip');
}

///The two result tabs, and the pane under whichever is on.
function resultTab(page, name) {
    return pane(page).locator('.subtabs button', { hasText: name });
}

///
///The bounding box of one element, as the browser computed it.
///
///Playwright's own boundingBox() would do, but this returns the same shape for a locator or a selector
///string and rounds to a tenth - which is the precision every number in UI-SPEC §1 is stated to, and
///stops a sub-pixel from failing an assertion about a whole one.
///
async function boxOf(page, selector) {
    const box = await page.evaluate((sel) => {
        const el = typeof sel === 'string' ? document.querySelector(sel) : sel;
        if (!el) return null;
        const r = el.getBoundingClientRect();
        return { x: r.x, y: r.y, width: r.width, height: r.height, top: r.top, right: r.right, bottom: r.bottom, left: r.left };
    }, selector);

    if (box === null) throw new Error(`No element matched ${selector}`);
    for (const k of Object.keys(box)) box[k] = Math.round(box[k] * 10) / 10;
    return box;
}

///
///The distance between two elements, along one axis.
///
///`x` is the space between the right edge of the first and the left edge of the second - the gaps in
///UI-SPEC §1's row table. `y` is the space between the bottom of the first and the top of the second -
///the vertical rhythm.
///
///Negative means they overlap, which is a finding rather than an error: a tab deliberately overlaps the
///line under the strip by a pixel.
///
async function gap(page, first, second, axis = 'x') {
    const a = await boxOf(page, first);
    const b = await boxOf(page, second);
    return axis === 'x'
        ? Math.round((b.left - a.right) * 10) / 10
        : Math.round((b.top - a.bottom) * 10) / 10;
}

///A computed style property, as a string, exactly as the browser resolved it.
async function styleOf(page, selector, property) {
    return page.evaluate(([sel, prop]) => {
        const el = document.querySelector(sel);
        if (!el) return null;
        return getComputedStyle(el).getPropertyValue(prop).trim();
    }, [selector, property]);
}

///
///The gaps between every child of a row, in order, with the two elements each sits between named.
///
///A row's spacing is a sequence rather than a number - two pixels, then eight, then fifty-two - and
///asserting on it one pair at a time hides the case where a control went missing and two gaps merged
///into one that happens to measure right.
///
async function rowGaps(page, rowSelector) {
    return page.evaluate((sel) => {
        const row = document.querySelector(sel);
        if (!row) return null;

        const name = (el) => el.id || el.getAttribute('class') || el.tagName.toLowerCase();
        const kids = [...row.children].filter((el) => {
            const r = el.getBoundingClientRect();
            //Zero-width spacers are still part of the sequence: .answer is exactly that, and dropping it
            //would report one 52 where the markup has 9.6 + 32.8 + 9.6.
            return r.width > 0 || el.classList.contains('answer') || el.classList.contains('gap');
        });

        const out = [];
        for (let i = 1; i < kids.length; i++) {
            const a = kids[i - 1].getBoundingClientRect();
            const b = kids[i].getBoundingClientRect();
            out.push({
                from: name(kids[i - 1]),
                to: name(kids[i]),
                px: Math.round((b.left - a.right) * 10) / 10
            });
        }
        return out;
    }, rowSelector);
}

///
///The script editor's host element. Monaco builds its own DOM inside it, so there is nothing here to
///fill or read the way a textarea would be — see setScript and scriptText.
///
function scriptHost(page) {
    return page.locator('.code.monaco').first();
}

///
///Wait until the editor exists.
///
///It is not created with the component: Monaco has to be fetched, and then the host has to have a box —
///a tool window renders its body before it opens, so the first moment there is a div is not the first
///moment there is an editor in it.
///
async function editorReady(page) {
    await page.waitForFunction(
        () => typeof monaco !== 'undefined'
            && monaco.editor.getEditors().length > 0
            && !!document.querySelector('.code.monaco'),
        null,
        { timeout: 30000 });
}

///
///Put a whole script in the editor, as though it had been typed.
///
///Through the editor's own selection and the interop's `insert`, rather than `set`: `set` is how C#
///pushes a value in and is deliberately quiet — it does not tell C# what it just wrote, or the bind
///would chase its own tail and move the caret on every keystroke. A spec using it would change what is
///on screen and leave the component believing something else.
///
///Not through the keyboard either. Selecting all with Ctrl+A needs the editor focused, and focus in a
///page the browser is not compositing is not something to build a suite on.
///
async function setScript(page, text) {
    await editorReady(page);
    await page.evaluate((t) => {
        const el = document.querySelector('.code.monaco');
        const editor = monaco.editor.getEditors()[0];
        editor.setSelection(editor.getModel().getFullModelRange());
        window.lec.monaco.insert(el, t);
    }, text);
}

///What is in the editor, from the editor rather than from the DOM.
async function scriptText(page) {
    await editorReady(page);
    return page.evaluate(() => window.lec.monaco.get(document.querySelector('.code.monaco')));
}

///
///How the grammar reads what is in the editor: every token, with the type the grammar gave it.
///
///Asked of the tokenizer rather than read off the painted lines, and deliberately. Monaco draws on
///requestAnimationFrame, so a page the browser is not compositing — a background tab, a headless run,
///the preview pane while it is not on screen — holds the right text and paints nothing. Reading the DOM
///would make this spec a test of whether the tab was visible.
///
///It is also the better question. The grammar is ours and the painting is Monaco's; what is worth
///asserting is that a comment tokenizes as a comment.
///
async function scriptTokens(page) {
    await editorReady(page);
    return page.evaluate(() => {
        const editor = monaco.editor.getEditors()[0];
        const model = editor.getModel();
        const lines = model.getValue().split('\n');

        return monaco.editor.tokenize(model.getValue(), model.getLanguageId())
            .flatMap((tokens, line) =>
                tokens.map((t, i) => ({
                    type: t.type,
                    text: lines[line].slice(t.offset, tokens[i + 1] ? tokens[i + 1].offset : undefined)
                })))
            .filter((t) => t.text && t.text.trim().length > 0);
    });
}

///
///The settings menu, opened.
///
///Returns the menu locator. The gear is a toggle, so a spec that opens it twice closes it - which is
///worth knowing before writing a beforeEach that opens it.
///
async function openSettings(page) {
    await page.locator('.titlebar .ghost').click();
    const menu = page.locator('.settings .menu').first();
    await expect(menu).toBeVisible();
    return menu;
}

///Close whatever dialog is open, the way a person would.
async function closeDialog(page) {
    await page.keyboard.press('Escape');
    await expect(page.locator('dialog[open]')).toHaveCount(0);
}

///
///Wait until the window on top is ready to be dragged.
///
///A tool window is wired from C# through the interop, which under four browsers at once can be a
///frame or two behind the element existing. Mouse work started before that lands on a title bar
///with no listener on it and nothing moves - a slow machine failing a spec for being slow rather
///than for being wrong. The interop marks the element when it wires it (dataset.wired), so there
///is something exact to wait for; two frames after it is for the box to settle where it will stay.
///
async function settle(page) {
    await page.waitForFunction(() => {
        const top = document.querySelector('dialog.tool[open]');
        if (!top) return true;
        if (top.dataset.wired !== '1') return false;

        //And where it will stay. A window fills in after it opens — a catalog of 24,000 commands
        //arrives a moment after the window does — and is pulled back on screen when it does, so a
        //press aimed at where the title bar was lands on the page behind it.
        const box = top.getBoundingClientRect();
        const was = window.__lecBox;
        window.__lecBox = [box.left, box.top, box.width, box.height].join();
        return was === window.__lecBox;
    }, null, { timeout: 15000, polling: 120 });
    await page.evaluate(() => new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(done))));
}

module.exports = {
    settle,
    BOOT_MS,
    gotoBench,
    clearBench,
    freshBench,
    connect,
    pane,
    selectTab,
    quick,
    consoleButton,
    queueChips,
    resultTab,
    boxOf,
    gap,
    styleOf,
    rowGaps,
    scriptHost,
    editorReady,
    setScript,
    scriptText,
    scriptTokens,
    openSettings,
    closeDialog
};
