//
//What happens when the server is not there.
//
//A Blazor WebAssembly app is a program running in the page, and an exception thrown out of a click
//handler is an unhandled component exception. Blazor's answer to one of those is to tear the whole app
//down and put up "Something went wrong. Reload the page." — losing every console, every log and every
//other tab along with it.
//
//So the calls that reach the server have to expect it not to answer. The server is restarted often
//while working on this, and the page in front of it does not know: the tabs it is showing were drawn
//from a list the server gave while it was still there, and pressing ✕ on one of them is the first the
//page hears of the server having gone.
//
//The failures here are made rather than waited for. Aborting the route is what a fetch to a dead server
//does — a TypeError, not a status code — and it can be aimed at one call without taking the real server
//down under the other tests.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane, quick } = require('./helpers');
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
});

///Blazor's own crash banner. Hidden until the app has fallen over, and once it is up the page is done.
function crashed(page) {
    return page.locator('#blazor-error-ui');
}

test.describe('when the server stops answering', () => {
    ///
    ///Closing a tab must never be able to take the page down.
    ///
    ///Least of all when the reason it failed is that the thing you were connected to is already gone,
    ///which is the case this exists for: come back to a page left open, press ✕ on a tab whose server
    ///was restarted underneath it, and the whole app used to go.
    ///
    test('closing a tab survives a server that has gone', async ({ page }) => {
        await page.route('**/api/sessions/**', (route) => route.abort('connectionrefused'));

        await page.locator('.tabstrip .tab .shut').click();

        //The tab goes: there is nothing left to disconnect from, and leaving it up would offer a
        //console onto an instrument nothing is holding.
        await expect(page.locator('.tabstrip')).toHaveCount(0);

        //And the page says what happened rather than pretending it worked. Scoped to the card the
        //address box is on: `.error` on its own also finds the About box's, which is in the markup
        //whether or not that dialog has ever been opened. Under the row rather than at the end of
        //it — a sentence there would decide how wide the block is.
        await expect(page.locator('.group:has(#addr) > p.error'))
            .toContainText('the server did not answer');

        await expect(crashed(page)).toBeHidden();
    });

    ///
    ///A command that cannot be sent is a line in the log, not the end of the page.
    ///
    ///The console already prints the instrument refusing or timing out — that is the server answering
    ///and saying so. This is the server not answering at all, and it used to be fatal.
    ///
    test('a command that cannot be sent lands in the log', async ({ page }) => {
        await page.route('**/api/sessions/*/command', (route) => route.abort('connectionrefused'));

        await quick(page, 'DC V').click();

        const log = pane(page).locator('.console');
        await expect(log).toContainText('> MEASure:VOLTage:DC?');
        await expect(log).toContainText('could not reach the server');

        await expect(crashed(page)).toBeHidden();

        //A call that could not reach the server also raises the notice over the page — one failure,
        //two things said about it: what happened to this command, and what it means for the rest.
        await expect(page.locator('.offline')).toBeVisible();

        //And the console is still a console. The route is lifted, the notice is cleared by asking
        //again, and the next command goes through.
        await page.unroute('**/api/sessions/*/command');
        await page.locator('.offline').getByRole('button', { name: /Retry connection/ }).click();
        await expect(page.locator('.offline')).toHaveCount(0);

        await quick(page, '*IDN?').click();
        await expect(log).toContainText(instrument.identity);
    });

    ///
    ///An instrument that answers with an error is a different thing, and still reads as one.
    ///
    ///Worth holding apart: "the instrument said no" and "nothing is listening" are two different
    ///failures, and a console that printed the same line for both would be hiding which.
    ///
    test('an error from the instrument still reads as one', async ({ page }) => {
        await page.route('**/api/sessions/*/command', (route) =>
            route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({ reply: null, isQuery: true, error: 'Timed out waiting for a reply.' })
            }));

        await quick(page, 'DC V').click();

        const log = pane(page).locator('.console');
        await expect(log).toContainText('ERROR: Timed out waiting for a reply.');
        await expect(log).not.toContainText('could not reach the server');
        await expect(crashed(page)).toBeHidden();

        //And it is the instrument that failed, not the server, so nothing says the server has gone.
        await expect(page.locator('.offline')).toHaveCount(0);
    });
});

test.describe('the server is not answering', () => {
    ///
    ///One box over everything, saying the one thing that matters.
    ///
    ///Not a stack trace and not a strip at the foot of the window: the server is not there, and it
    ///might come back. It says so, says the instruments are unaffected — they are held by the server
    ///rather than by this page — and offers the two things that can be done about it.
    ///
    test('says so, over everything, with a way to try again', async ({ page }) => {
        await page.route('**/api/**', (route) => route.abort('connectionrefused'));
        await quick(page, 'DC V').click();

        const box = page.locator('.offline');
        await expect(box).toBeVisible();
        await expect(box).toContainText('The server is not answering');
        await expect(box).toContainText('held by the server');
        await expect(box.getByRole('button', { name: /Retry connection/ })).toBeEnabled();
        await expect(box.getByRole('button', { name: /Reload the page/ })).toBeEnabled();

        await expect(crashed(page)).toBeHidden();
    });

    ///
    ///And nothing behind it is reachable while it is up.
    ///
    ///Everything on the bench is a question asked of the server, so a page that let you go on
    ///pressing would be collecting failures rather than reporting one.
    ///
    test('puts the page behind it out of reach', async ({ page }) => {
        await page.route('**/api/**', (route) => route.abort('connectionrefused'));
        await quick(page, 'DC V').click();
        await expect(page.locator('.offline')).toBeVisible();

        const reachable = await page.evaluate(() => {
            const gear = document.querySelector('.titlebar .ghost').getBoundingClientRect();
            const hit = document.elementFromPoint(gear.left + gear.width / 2, gear.top + gear.height / 2);
            return hit !== null && hit.closest('.titlebar .ghost') !== null;
        });

        expect(reachable, 'the page behind the notice answered a hit test').toBe(false);
    });

    ///
    ///Retry asks again, and the box goes when the answer comes.
    ///
    ///Nothing clears the state by hand: it is the call getting through that clears it, through the
    ///same handler every other call goes through. There is no second path to being reachable.
    ///
    test('Retry brings it back when the server returns', async ({ page }) => {
        await page.route('**/api/**', (route) => route.abort('connectionrefused'));
        await quick(page, 'DC V').click();
        await expect(page.locator('.offline')).toBeVisible();

        //The server was there all along; it is this page that could not see it.
        await page.unroute('**/api/**');
        await page.locator('.offline').getByRole('button', { name: /Retry connection/ }).click();

        await expect(page.locator('.offline')).toHaveCount(0);

        //And the bench works again.
        await quick(page, '*IDN?').click();
        await expect(pane(page).locator('.console')).toContainText(instrument.identity);
    });

    ///
    ///Retry that fails leaves the box up, rather than flickering and coming back.
    ///
    test('Retry says nothing new when the server is still gone', async ({ page }) => {
        await page.route('**/api/**', (route) => route.abort('connectionrefused'));
        await quick(page, 'DC V').click();

        const box = page.locator('.offline');
        await expect(box).toBeVisible();

        await box.getByRole('button', { name: /Retry connection/ }).click();
        await expect(box).toBeVisible();
        await expect(box.getByRole('button', { name: /Retry connection/ })).toBeEnabled();
    });
});
