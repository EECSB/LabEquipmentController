//
//The Command Library page: the catalog picker, and the guide beside the commands.
//
//The app ships none of the guides - they are the manufacturers' copyright - so the collection is the
//user's own. The desktop points at a folder on the machine it runs on; a server has nobody at a
//keyboard to point anywhere, so its folder is its own and the browser puts files into it. The
//matching from a catalog to a file is Core's either way, so the guide the desktop finds for a
//catalog is the guide this finds.
//
const { test, expect } = require('./fixtures');
const { freshBench, settle } = require('./helpers');

///
///A collection with nothing in it, whatever the last spec put there.
///
///A worker's LEC_DATA is its own, which is not the same as its being empty: the directory outlives the
///run that made it, and inside one run the spec that uploads a guide can land on the worker that is
///about to assert there is none. Both of those turned up as the no-copy spec finding a copy.
///
async function emptyCollection(request) {
    const held = await (await request.get('/api/datasheets')).json();
    for (const sheet of held) await request.delete('/api/datasheets/' + sheet.path);
}

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await emptyCollection(request);
    await page.goto('/catalog');
    await expect(page.locator('#cat')).toBeVisible();
    await settle(page);
});

test.describe('the catalog picker', () => {
    ///
    ///Grouped by manufacturer, which is the desktop's tree with its branches collapsed into a list.
    ///
    ///CommandReferenceForm has a node per maker with the instruments under it. Flat, this list repeated
    ///the maker on every one of thirty-six lines, so "which Rigol was it" meant reading the same word
    ///ten times down the page. The maker is said once at the head of its group and the browser indents
    ///what belongs to it.
    ///
    test('groups the catalogs under their manufacturer', async ({ page }) => {
        const groups = page.locator('#cat optgroup');
        await expect(groups.first()).toBeAttached();

        const count = await groups.count();
        expect(count).toBeGreaterThan(5);

        //Every catalog is under one of them, and none of them repeats the maker in its own entries.
        const makers = [];
        for (let i = 0; i < count; i++) {
            const label = await groups.nth(i).getAttribute('label');
            expect(label).toBeTruthy();
            makers.push(label);

            const first = (await groups.nth(i).locator('option').first().textContent()).trim();
            expect(first.startsWith(label + ' —')).toBeFalsy();
        }

        //And they are distinct: a group per maker, not a group per catalog.
        expect(new Set(makers).size).toBe(makers.length);
    });
});

test.describe('the programming guide beside the commands', () => {
    ///
    ///Two columns: the list, and the guide it was transcribed from.
    ///
    ///CommandLibraryForm splits its right half the same way, a little over half the width to the list.
    ///Reading a command against the page it came from is the reason to have the guide at hand at all.
    ///
    test('stands beside the command list, not under it', async ({ page }) => {
        await page.locator('#cat').selectOption({ index: 1 });
        await settle(page);

        const list = await page.locator('.library > .group').first().boundingBox();
        const guide = await page.locator('.library > .group.guide').boundingBox();

        //Side by side, tops level, and the list is the wider of the two.
        expect(Math.abs(list.y - guide.y)).toBeLessThan(4);
        expect(guide.x).toBeGreaterThan(list.x + list.width - 4);
        expect(list.width).toBeGreaterThan(guide.width);
    });

    ///
    ///A server holding no copy says so, and says what to do about it.
    ///
    ///This one holds none: every worker gets its own LEC_DATA, so the collection starts empty. Two
    ///answers, which are the desktop's two - fetch it from the vendor, or put the copy you already
    ///have where this build can read it.
    ///
    test('says when the server holds no copy, and offers both ways to get one', async ({ page }) => {
        await page.locator('#cat').selectOption({ index: 1 });
        await settle(page);

        const guide = page.locator('.library > .group.guide');
        await expect(guide.locator('.noguide')).toBeVisible();
        await expect(guide).toContainText('No copy of');
        await expect(guide).toContainText("manufacturers' copyright");
        await expect(guide.getByRole('button', { name: /Upload a PDF/ })).toBeVisible();

        //And nothing is pretending to be a viewer while there is nothing to view.
        await expect(guide.locator('object.pdf')).toHaveCount(0);
    });

    ///
    ///Uploaded, it is found - by the same matching the desktop uses, not by remembering what was sent.
    ///
    test('shows the guide once the server has a copy', async ({ page, request }) => {
        //A PDF named after the catalog it belongs to, which is what the matching looks for.
        const catalog = (await (await request.get('/api/catalogs')).json())[0];
        const pdf = Buffer.from('%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF');

        const sent = await request.post('/api/datasheets', {
            data: { fileName: `${catalog.instrument.replace(/[^A-Za-z0-9]+/g, '_')}.pdf`,
                    base64: pdf.toString('base64'), manufacturer: catalog.manufacturer },
        });
        expect(sent.ok()).toBeTruthy();

        await page.reload();
        await expect(page.locator('#cat')).toBeVisible();
        await page.locator('#cat').selectOption(catalog.family);
        await settle(page);

        const guide = page.locator('.library > .group.guide');
        const viewer = guide.locator('object.pdf');
        await expect(viewer).toHaveAttribute('data', /^api\/datasheets\/file\//);

        //Served as a PDF, and by a path that stays inside the collection.
        const url = await viewer.getAttribute('data');
        const file = await request.get('/' + url);
        expect(file.ok()).toBeTruthy();
        expect(file.headers()['content-type']).toContain('application/pdf');

        const escaped = await request.get('/api/datasheets/file/../../ai.json');
        expect(escaped.status()).toBe(404);
    });
});
