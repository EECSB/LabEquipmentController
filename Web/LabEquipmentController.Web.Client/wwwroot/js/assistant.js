// The AI windows' interop, GDS Viewer's floatingWindow.js, camera.js and speech.js for this app:
// what the box does with its keys and a paste, files dropped on the window, the panels' dividers,
// a press outside a menu, a screenshot, the camera and the microphone. Everything that brings a
// file in ends in the window's one file input, as the paperclip does, so every check a chosen file
// meets these meet too: a DataTransfer written to the input and a change dispatched, which is what
// Blazor's InputFile listens for.
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

    // Why a browser will not hand a page the screen, the camera or the microphone. Most often it
    // is the address: these are offered only over https and on localhost, and a bench reached by
    // its LAN address over http has navigator.mediaDevices undefined.
    const notOffered = (what) => window.isSecureContext
        ? `This browser cannot ${what}.`
        : `A browser only lets a page ${what} over https or on localhost, and this page is at ${location.origin}.`;

    // A press anywhere but the element given: the menus' way of going away. One listener per
    // element, kept on it, so a window taken out of the page takes its listener with it.
    const outside = (el, ref, method) => {
        if (!el) return;
        outsideOff(el);
        el.lecOutside = (e) => {
            if (!el.contains(e.target)) ref.invokeMethodAsync(method);
        };
        document.addEventListener('pointerdown', el.lecOutside);
    };

    const outsideOff = (el) => {
        if (!el || !el.lecOutside) return;
        document.removeEventListener('pointerdown', el.lecOutside);
        el.lecOutside = null;
    };

    // Esc while a menu or a card is up is that menu's, not the window's, which shuts on Esc. Refused
    // here because lec.dialog listens natively and Blazor's stopPropagation only stops Blazor's own.
    const escape = (el, ref, method) => {
        if (!el || el.lecEscape) return;
        el.lecEscape = true;
        el.addEventListener('keydown', (e) => {
            if (e.key !== 'Escape') return;
            if (!el.matches('[data-popup]') && !el.querySelector('[data-popup]')) return;
            e.stopPropagation();
            ref.invokeMethodAsync(method);
        });
    };

    // The dividers between the chats, the conversation and the files: dragging one sizes the panel
    // on its side, kept in this browser for the next visit. The widths are custom properties on
    // the row rather than styles on the panels, which Blazor takes away and puts back as they
    // close and open: the row stays, and so does what is set on it.
    const splits = (row) => {
        if (!row || row.lecSplits) return;
        row.lecSplits = true;

        const panels = { chats: '.aiChats', files: '.aiFiles' };
        const key = (name) => 'lec.aiPanel.' + name;

        for (const name of Object.keys(panels)) {
            let kept = null;
            try { kept = window.localStorage.getItem(key(name)); } catch { /* storage refused: the default */ }
            if (kept && /^\d+$/.test(kept)) row.style.setProperty(`--ai-${name}-width`, kept + 'px');
        }

        row.addEventListener('pointerdown', (e) => {
            const split = e.target.closest('.aiSplit');
            if (!split || !row.contains(split) || e.button !== 0) return;

            const name = split.dataset.split;
            const panel = row.querySelector(panels[name]);
            if (!panel) return;

            e.preventDefault();

            // The chats are left of their divider and grow as it goes right; the files are right of theirs.
            const sign = name === 'files' ? -1 : 1;
            const startX = e.clientX;
            const startWidth = panel.getBoundingClientRect().width;
            const most = Math.max(160, row.getBoundingClientRect().width * 0.6);
            let width = startWidth;

            split.setPointerCapture(e.pointerId);
            split.classList.add('aiSplitDragging');

            const move = (m) => {
                width = Math.round(Math.max(120, Math.min(most, startWidth + (sign * (m.clientX - startX)))));
                row.style.setProperty(`--ai-${name}-width`, width + 'px');
            };

            const done = () => {
                split.removeEventListener('pointermove', move);
                split.removeEventListener('pointerup', done);
                split.removeEventListener('pointercancel', done);
                split.classList.remove('aiSplitDragging');
                try { window.localStorage.setItem(key(name), String(width)); } catch { /* not kept, then */ }
            };

            split.addEventListener('pointermove', move);
            split.addEventListener('pointerup', done);
            split.addEventListener('pointercancel', done);
        });
    };

    // One frame of a canvas, as a file named for when it was taken, into the input.
    const attach = async (canvas, input, prefix, type, quality) => {
        if (!input) return 'There is nowhere to attach the picture.';
        const blob = await new Promise((r) => canvas.toBlob(r, type, quality));
        if (!blob) return 'The picture could not be taken.';
        const extension = type === 'image/jpeg' ? 'jpg' : 'png';
        feed(input, [new File([blob], `${prefix}-${stamp()}.${extension}`, { type })]);
        return '';
    };

    let cameraStream = null;

    const stopCamera = () => {
        if (cameraStream) for (const track of cameraStream.getTracks()) track.stop();
        cameraStream = null;
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

            // The caret after the words, once the window has written them: an example, a command's
            // name written out by Tab, or the start of a request to finish.
            caretToEnd: (el) => {
                if (!el) return;
                el.focus();
                el.selectionStart = el.selectionEnd = el.value.length;
            },

            // Files dropped anywhere on the window go to the paperclip's input, and the conversation
            // lights up while one is held over it. A drop that lands on the window and not in a
            // field is refused here, or the browser would navigate to the file and close the app.
            drop: (zone, input) => {
                if (!zone || !input || zone.dataset.lecDrop) return;
                zone.dataset.lecDrop = '1';

                const lit = zone.querySelector('.aiDrop') || zone;
                const stop = (e) => { e.preventDefault(); e.stopPropagation(); };
                for (const name of ['dragenter', 'dragover']) {
                    zone.addEventListener(name, (e) => {
                        if (!e.dataTransfer || ![...e.dataTransfer.types].includes('Files')) return;
                        stop(e);
                        e.dataTransfer.dropEffect = 'copy';
                        lit.classList.add('fileDropOver');
                    });
                }
                zone.addEventListener('dragleave', (e) => {
                    if (e.relatedTarget && zone.contains(e.relatedTarget)) return;
                    lit.classList.remove('fileDropOver');
                });
                zone.addEventListener('drop', (e) => {
                    if (!e.dataTransfer || e.dataTransfer.files.length === 0) return;
                    stop(e);
                    lit.classList.remove('fileDropOver');
                    feed(input, [...e.dataTransfer.files]);
                });
            },

            splits,
            outside,
            outsideOff,
            escape,

            // What the person chose under AI settings, kept in this browser. Storage can be refused
            // outright, in a private window for one; a setting is not worth an error over.
            stored: (key) => { try { return localStorage.getItem(key); } catch { return null; } },
            keep: (key, value) => { try { localStorage.setItem(key, value); } catch { /* not kept, then */ } },

            // The code selected, for a page that was not allowed the clipboard.
            selectText: (el) => {
                if (!el) return;
                const range = document.createRange();
                range.selectNodeContents(el);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
            },

            // The paperclip: the input's own picker, which a browser opens only from a real press.
            pick: (input) => input?.click(),

            // A screenshot: the browser's own picker of a screen, a window or a tab, one frame of
            // it as a PNG scaled to 1,568 pixels on its long side, and the sharing ended as soon as
            // the frame is taken, so the browser's "sharing" bar goes with it. Answers "" when it
            // went into the input, null when the picker was closed without a choice (somebody
            // changing their mind, which is said nowhere), or why it could not be taken.
            screenshot: async (input) => {
                if (!navigator.mediaDevices || !navigator.mediaDevices.getDisplayMedia)
                    return notOffered('take a screenshot');

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

                    return await attach(canvas, input, 'screenshot', 'image/png');
                } finally {
                    for (const track of stream.getTracks()) track.stop();
                }
            },

            // The camera, live in a card over the box; Capture takes the frame on screen as a JPEG.
            camera: {
                // Shows the camera in the video given. Answers why it could not, or '' once it is showing.
                open: async (video) => {
                    if (!video) return 'There is nothing to show the camera in.';
                    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return notOffered('use a camera');

                    stopCamera();
                    try {
                        cameraStream = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 1280 }, height: { ideal: 720 } }, audio: false });
                    } catch (e) {
                        if (e && e.name === 'NotFoundError') return 'No camera was found.';
                        return 'The browser was not allowed to use the camera. Allow it in the address bar, and press the camera again.';
                    }

                    video.srcObject = cameraStream;
                    try { await video.play(); } catch { /* it shows when it can */ }
                    return '';
                },

                capture: async (video, input) => {
                    if (!video || !cameraStream || video.videoWidth === 0) return 'The camera has no picture yet.';
                    const canvas = document.createElement('canvas');
                    canvas.width = video.videoWidth;
                    canvas.height = video.videoHeight;
                    canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
                    return await attach(canvas, input, 'camera', 'image/jpeg', 0.9);
                },

                close: stopCamera,
            },
        },

        // The microphone, two ways. The browser's own recognition: the words appear as they are
        // said (Chrome and Edge send the audio to Google and Microsoft to be recognized; other
        // browsers have none, and the button says so). Or a recording for the AI provider to write
        // out, made a 16 kHz WAV here (speechWav.js), since what a browser records is not a format
        // Gemini lists. A level meter in the box's corner either way, so a muted microphone shows
        // at once. Calls OnSpeechEnded on the window when it ends.
        speech: (() => {
            const MOST_SECONDS = 120;   // The longest recording sent to a provider. A question, not a meeting.

            let recognition = null;
            let caller = null;
            let meter = null;
            let recorder = null;
            let stream = null;
            let chunks = [];
            let limit = null;

            const LEVEL_MS = 70;      // How often the bars move along.
            const FULL_RMS = 0.12;    // How loud a voice fills one: speech near the microphone is around 0.1 RMS.

            const recognizer = () => window.SpeechRecognition || window.webkitSpeechRecognition || null;

            const stopMeter = () => {
                if (!meter) return;
                clearInterval(meter.timer);
                if (meter.own) for (const track of meter.stream.getTracks()) track.stop();
                meter.context.close();
                meter = null;
            };

            // From the recording's own stream when there is one. The recognizer hands the page no
            // sound, so with that the meter opens the microphone a second time for itself.
            const startMeter = async (element) => {
                stopMeter();
                if (!element || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return;
                if (!window.AudioContext && !window.webkitAudioContext) return;

                const own = !stream;
                let source = stream;
                if (own) {
                    try {
                        source = await navigator.mediaDevices.getUserMedia({ audio: true });
                    } catch {
                        return;
                    }
                }

                const context = new (window.AudioContext || window.webkitAudioContext)();
                const analyser = context.createAnalyser();
                analyser.fftSize = 1024;
                context.createMediaStreamSource(source).connect(analyser);

                const samples = new Float32Array(analyser.fftSize);
                const bars = [...element.children];
                const levels = bars.map(() => 0);

                const timer = setInterval(() => {
                    if (!element.isConnected) { stopMeter(); return; }

                    analyser.getFloatTimeDomainData(samples);
                    let sum = 0;
                    for (const sample of samples) sum += sample * sample;

                    // The square root again, so a quiet voice still lifts the bars and a loud one does not pin them.
                    const level = Math.min(1, Math.sqrt(Math.sqrt(sum / samples.length) / FULL_RMS));
                    levels.shift();
                    levels.push(level);
                    bars.forEach((bar, i) => { bar.style.height = (15 + 85 * levels[i]).toFixed(0) + '%'; });
                }, LEVEL_MS);

                meter = { context, stream: source, timer, own };
            };

            const ended = (problem) => {
                const told = caller;
                caller = null;
                stopMeter();
                if (told) told.invokeMethodAsync('OnSpeechEnded', problem || '');
            };

            // Puts words into the box as if they were typed, so the page's binding and the box's growing both hear them.
            const show = (box, value) => {
                box.value = value;
                box.dispatchEvent(new Event('input', { bubbles: true }));
            };

            return {
                browserSupported: () => recognizer() !== null,

                // Listens, writing what is heard after what the box already holds: the words settled
                // so far and the guess at the ones still being said, which the next result replaces.
                startListening: (box, dotnet) => {
                    const Recognition = recognizer();
                    if (!Recognition || !box || recognition) return false;

                    caller = dotnet;

                    let before = box.value;
                    if (before.length > 0 && !/\s$/.test(before)) before += ' ';
                    let settled = '';

                    recognition = new Recognition();
                    recognition.continuous = true;
                    recognition.interimResults = true;
                    recognition.lang = navigator.language || 'en-US';

                    recognition.onresult = (event) => {
                        let guess = '';
                        for (let i = event.resultIndex; i < event.results.length; i++) {
                            const said = event.results[i][0].transcript;
                            if (event.results[i].isFinal) settled += said;
                            else guess += said;
                        }
                        show(box, before + settled + guess);
                    };

                    recognition.onerror = (event) => {
                        let problem = `Speech could not be recognized: ${event.error}.`;
                        if (event.error === 'not-allowed' || event.error === 'service-not-allowed') {
                            problem = window.isSecureContext
                                ? 'The browser was not allowed to use the microphone. Allow it in the address bar, and press the microphone again.'
                                : notOffered('use the microphone');
                        } else if (event.error === 'no-speech') {
                            problem = '';
                        } else if (event.error === 'network') {
                            problem = "The browser's speech recognition could not reach its server. Try again.";
                        }
                        recognition = null;
                        ended(problem);
                    };

                    recognition.onend = () => {
                        recognition = null;
                        ended('');
                    };

                    try {
                        recognition.start();
                    } catch {
                        recognition = null;
                        caller = null;
                        return false;
                    }
                    return true;
                },

                stopListening: () => {
                    stopMeter();
                    if (recognition) recognition.stop();
                },

                showLevel: startMeter,

                // Starts recording for the provider. Answers why it could not, or '' once it is recording.
                startRecording: async (dotnet) => {
                    if (recorder) return '';
                    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !window.MediaRecorder)
                        return notOffered('record from a microphone');

                    try {
                        stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                    } catch {
                        return 'The browser was not allowed to use the microphone. Allow it in the address bar, and press the microphone again.';
                    }

                    caller = dotnet;
                    chunks = [];
                    recorder = new MediaRecorder(stream);
                    recorder.ondataavailable = (event) => { if (event.data && event.data.size > 0) chunks.push(event.data); };
                    recorder.start();

                    // A recording left going is ended at the limit, and handed over as if the button had been pressed.
                    limit = setTimeout(() => {
                        if (caller) caller.invokeMethodAsync('OnSpeechEnded', 'limit');
                    }, MOST_SECONDS * 1000);
                    return '';
                },

                // Ends the recording and hands it back as a 16 kHz mono WAV in base64, '' for nothing recorded.
                stopRecording: async () => {
                    if (!recorder) return '';

                    clearTimeout(limit);
                    stopMeter();

                    const done = new Promise((r) => { recorder.onstop = r; });
                    recorder.stop();
                    await done;

                    for (const track of stream.getTracks()) track.stop();
                    const blob = new Blob(chunks, { type: recorder.mimeType || 'audio/webm' });
                    recorder = null;
                    stream = null;
                    chunks = [];
                    caller = null;

                    if (blob.size === 0) return '';

                    const context = new (window.AudioContext || window.webkitAudioContext)();
                    try {
                        const audio = await context.decodeAudioData(await blob.arrayBuffer());
                        const channels = [];
                        for (let c = 0; c < audio.numberOfChannels; c++) channels.push(audio.getChannelData(c));

                        const bytes = window.speechWav.speechWav(channels, audio.sampleRate);
                        let binary = '';
                        for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
                        return btoa(binary);
                    } finally {
                        context.close();
                    }
                },

                // Stops whichever is going, keeping nothing: the window closed, or the page left.
                cancel: () => {
                    caller = null;
                    clearTimeout(limit);
                    stopMeter();
                    if (recognition) {
                        recognition.onend = null;
                        recognition.abort();
                        recognition = null;
                    }
                    if (recorder) {
                        recorder.onstop = null;
                        try { recorder.stop(); } catch { /* it had stopped */ }
                        recorder = null;
                    }
                    if (stream) {
                        for (const track of stream.getTracks()) track.stop();
                        stream = null;
                    }
                    chunks = [];
                },
            };
        })(),
    });
})();
