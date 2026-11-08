// The AI assistant window's interop: what the box does with its keys and a paste, files dropped
// on the window, and a screenshot. Everything that brings a file in ends in the window's one file
// input, as Attach Files… does, so every check a chosen file meets these meet too: a DataTransfer
// written to the input and a change dispatched, which is what Blazor's InputFile listens for.
(function () {
    const feed = (input, files) => {
        if (!input || !files || files.length === 0) return;
        const carrier = new DataTransfer();
        for (const file of files) carrier.items.add(file);
        input.files = carrier.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    // The keys the command list answers while it is up. The box carries data-slash-menu="open"
    // then, and these are refused here rather than left to the box, for the reason Enter's newline
    // is: a Blazor handler's preventDefault is fixed when it renders, not decided per key.
    const listKeys = ['ArrowUp', 'ArrowDown', 'Tab', 'Escape', 'Enter'];

    const stamp = () => {
        const d = new Date(), two = (n) => String(n).padStart(2, '0');
        return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}-${two(d.getHours())}${two(d.getMinutes())}${two(d.getSeconds())}`;
    };

    window.lec = Object.assign(window.lec || {}, {
        assistant: {
            // The box: one line and as many as the words need, Enter to send and Shift+Enter for a
            // new line, the command list's keys while it is up, and a paste of pictures or files
            // into the paperclip's input. Text on the clipboard as well wins: cells copied out of a
            // spreadsheet bring a picture of themselves along, and the cells are what was meant.
            box: (el, input, ref) => {
                if (!el || el.dataset.lecAssistant === '1') return;
                el.dataset.lecAssistant = '1';

                const fit = () => window.lec.fitBox(el);
                el.addEventListener('input', fit);

                el.addEventListener('keydown', (e) => {
                    if (e.isComposing || e.altKey || e.ctrlKey || e.metaKey) return;
                    if (el.dataset.slashMenu === 'open' && listKeys.includes(e.key) && !(e.key === 'Enter' && e.shiftKey)) {
                        e.preventDefault();
                        // Esc in the list is the list's, not the window's: the window shuts on Esc.
                        e.stopPropagation();
                        ref.invokeMethodAsync('SlashKey', e.key);
                        return;
                    }
                    if (e.key !== 'Enter' || e.shiftKey) return;
                    e.preventDefault();
                    ref.invokeMethodAsync('SendRequest');
                });

                el.addEventListener('paste', (e) => {
                    const data = e.clipboardData;
                    if (!data || data.files.length === 0) return;
                    if ((data.getData('text/plain') || '').length > 0) return;
                    e.preventDefault();
                    feed(input, [...data.files]);
                });

                fit();
            },

            fit: (el) => window.lec.fitBox(el),

            // The box takes the keyboard as the window opens; the question it opens on is selected,
            // so Enter sends it and the first key typed replaces it.
            focusSelect: (el, select) => {
                if (!el) return;
                el.focus();
                if (select) el.select();
            },

            // The caret after the words, once the window has written them: a command's name written
            // out by Tab, or the start of a request to finish.
            caretToEnd: (el) => {
                if (!el) return;
                el.focus();
                el.selectionStart = el.selectionEnd = el.value.length;
            },

            // Files dropped anywhere on the window go to the paperclip's input. A drop that lands
            // on the window and not in a field is refused here, or the browser would navigate to
            // the file and close the app.
            drop: (zone, input) => {
                if (!zone || !input || zone.dataset.lecDrop) return;
                zone.dataset.lecDrop = '1';

                const stop = (e) => { e.preventDefault(); e.stopPropagation(); };
                for (const name of ['dragenter', 'dragover']) {
                    zone.addEventListener(name, (e) => {
                        if (!e.dataTransfer || ![...e.dataTransfer.types].includes('Files')) return;
                        stop(e);
                        e.dataTransfer.dropEffect = 'copy';
                        zone.classList.add('dropping');
                    });
                }
                zone.addEventListener('dragleave', (e) => {
                    if (e.relatedTarget && zone.contains(e.relatedTarget)) return;
                    zone.classList.remove('dropping');
                });
                zone.addEventListener('drop', (e) => {
                    if (!e.dataTransfer || e.dataTransfer.files.length === 0) return;
                    stop(e);
                    zone.classList.remove('dropping');
                    feed(input, [...e.dataTransfer.files]);
                });
            },

            // Attach Files…: the input's own picker, which a browser opens only from a real press.
            pick: (input) => input?.click(),

            // A screenshot: the browser's own picker of a screen, a window or a tab, one frame of
            // it as a PNG scaled to 1,568 pixels on its long side, and the sharing ended as soon as
            // the frame is taken, so the browser's "sharing" bar goes with it. Answers "" when it
            // went into the input, null when the picker was closed without a choice (somebody
            // changing their mind, which is said nowhere), or why it could not be taken.
            screenshot: async (input) => {
                if (!navigator.mediaDevices || !navigator.mediaDevices.getDisplayMedia)
                    return 'This browser cannot take a screenshot for a page.';

                let stream;
                try {
                    stream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: false });
                } catch (e) {
                    if (e && (e.name === 'NotAllowedError' || e.name === 'AbortError')) return null;
                    return String(e && e.message || e);
                }

                try {
                    const video = document.createElement('video');
                    video.muted = true;
                    video.playsInline = true;
                    video.srcObject = stream;
                    await video.play();
                    // One frame drawn, so the first one is not the black the stream opens on.
                    await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 150)));

                    const w = video.videoWidth, h = video.videoHeight;
                    if (!w || !h) return 'The screen came back empty.';
                    const scale = Math.min(1, 1568 / Math.max(w, h));
                    const canvas = document.createElement('canvas');
                    canvas.width = Math.max(1, Math.round(w * scale));
                    canvas.height = Math.max(1, Math.round(h * scale));
                    canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);

                    const blob = await new Promise((r) => canvas.toBlob(r, 'image/png'));
                    if (!blob) return 'The screen could not be made a picture.';
                    feed(input, [new File([blob], `screenshot-${stamp()}.png`, { type: 'image/png' })]);
                    return '';
                } finally {
                    for (const track of stream.getTracks()) track.stop();
                }
            },
        },
    });
})();
