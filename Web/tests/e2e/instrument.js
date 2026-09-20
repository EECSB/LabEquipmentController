//A fake SCPI instrument, so a run needs no bench.
//
//The web server opens the socket, not the browser, and the server runs on this machine - so a TCP
//listener here is an instrument as far as the app is concerned. What it has to speak is small:
//ScpiClient writes `command\n` and, for anything holding a `?`, reads back to the next `\n`
//(Core/ScpiClient.NormalizeCommand and ReadLineAsync). There is no handshake and no framing beyond
//that.
//
//It answers as an SDM3065X because of where that lands in the classification table (SPEC §8, rule 12,
//`SDM`): the Multimeter family, which carries twelve quick commands and eight readout functions, and
//supports neither screen nor waveform capture. One identity therefore exercises both halves of the
//enabled-state rules - Live Readout live, Capture Screen and Capture Waveform greyed - which is the
//pairing UI-SPEC §7 is about.
//
//Every reply is deterministic. A random value would make a failure unreproducible, and the one thing
//worse than a flaky test is a flaky test that cannot be run again.
const net = require('net');

///The identity the fake answers to `*IDN?`. The serial says plainly what it is, so a stray connection
///from a real session is recognisable in a log.
const IDENTITY = 'Siglent Technologies,SDM3065X,LEC-E2E-0001,1.00.00.00';

///The family and profile name that identity is classified into, for specs that assert what the console
///says it is talking to. Kept beside the identity so the two cannot drift apart.
const PROFILE = 'Multimeter (SDM3065X)';

///
///Start one. Resolves once it is listening, with the port it was given and the handle to stop it.
///
///Port zero: the operating system picks a free one. Specs never have to agree on a number, and a fake
///left behind by a crashed run cannot collide with the next.
///
///`delayMs` is how long each query takes to answer. Zero for most specs; a few hundred for the ones
///about the queue, which need a command to still be in flight when the next is pressed.
///
///
///Wrap text as an IEEE 488.2 definite-length block: `#`, the number of length digits, the length,
///then the bytes. What `SYSTem:HELP:HEADers?` answers with, and what ScpiClient.ReadBlockAsync
///reads - which is why a header dump can hold newlines where an ordinary reply cannot.
///
function block(text) {
    const length = String(Buffer.byteLength(text, 'ascii'));
    return '#' + length.length + length + text;
}

///The header tree of an instrument that implements the query. Deliberately more than three lines
///and mostly header-shaped, because CommandDiscovery tells a real dump from an error line by
///exactly that: a bare "0" or one line of prose is a refusal however politely it is worded.
const HEADERS = [':MEASure:VOLTage:DC', ':MEASure:CURRent:DC', ':SENSe:VOLTage:DC:NPLC',
                 ':SYSTem:ERRor', '*IDN', '*RST'];

///
///A scope's trace, as a Rigol hands one back: `:WAVeform:PREamble?` and then `:WAVeform:DATA?`, one
///byte a sample. The ten preamble fields are format, type, points, count, then the time and the
///voltage scaling - a microsecond a sample from -0.7 ms, and twenty millivolts a code about code
///128 - which WaveformCapture.FromRigol turns into volts as `(raw - yreference - yorigin) * yincrement`.
///
///Each channel carries a different wave at a different size, so a spec can tell from the figures
///alone which channel it is reading: channel 1 a sine of 2 V peak to peak, five cycles across the
///record; channel 2 a square of 1 V, ten half-periods.
///
const POINTS = 1400;
const PREAMBLE = `0,0,${POINTS},1,1.000000e-06,-7.000000e-04,0,2.000000e-02,0,128`;

function traceOf(channel) {
    const raw = Buffer.alloc(POINTS);
    for (let i = 0; i < POINTS; i++) {
        raw[i] = channel === 1
            ? 128 + Math.round(50 * Math.sin(2 * Math.PI * 5 * i / POINTS))
            : (Math.floor(i / 140) % 2 === 0 ? 153 : 103);
    }
    return raw;
}

///The same wrapping as `block`, of bytes rather than text: a trace is binary, and a byte of it can
///be a line feed.
function blockOf(bytes) {
    const length = String(bytes.length);
    return Buffer.concat([Buffer.from('#' + length.length + length, 'ascii'), bytes]);
}

async function startInstrument({ delayMs = 0, identity = IDENTITY, headers = null, channels = 0 } = {}) {
    //What it was asked, in order, across every connection. The queue specs assert on the order the
    //instrument saw rather than on what the log printed: the log is the app's account of the exchange
    //and this is the exchange.
    const received = [];

    let readings = 0;

    ///Which channel `:WAVeform:DATA?` reads, as `:WAVeform:SOURce` last set it: one for the whole
    ///instrument, whichever connection set it, as a real scope keeps it.
    let source = 1;

    ///The answer to one command, or null for a command that takes no answer.
    ///
    ///A command holding `?` is a query and everything else is a write - the same test the app makes
    ///(SPEC §7), so a fake that answered on any other rule would be answering a different protocol.
    function reply(command) {
        //Started with `channels`, it is a scope with that many, and answers the Rigol :WAVeform tree
        //WaveformReader speaks. Asked for a channel past its last, it answers the way a two-channel
        //DS2202 does rather than refusing: with two samples, against fourteen hundred on the time base
        //(see ChannelCaptures).
        if (channels > 0) {
            const picked = /^:?WAV(eform)?:SOUR(ce)?\s+CHAN(nel)?(\d)/i.exec(command);
            if (picked) { source = Number(picked[4]); return null; }
            if (/^:?WAV(eform)?:PRE(amble)?\?/i.test(command)) return PREAMBLE;
            if (/^:?WAV(eform)?:DATA\?/i.test(command))
                return blockOf(source <= channels ? traceOf(source) : Buffer.from([128, 128]));
        }

        if (!command.includes('?')) return null;

        if (/^\*IDN\?/i.test(command)) return identity;
        if (/^\*OPC\?/i.test(command)) return '1';
        if (/SYST(em)?:ERR/i.test(command)) return '0,"No error"';

        //Only an instrument started with `headers` implements SCPI-99's list-your-commands query.
        //Most real ones do not - Rigol and Siglent among them - so the default is not to, which is
        //what makes the fallback to the bundled catalog the ordinary path here as well.
        if (/SYST(em)?:HELP:HEAD(ers)?\?/i.test(command))
            return headers ? block(headers.join('\n')) : '0';

        //A slow ramp with a wobble on it, so a plot of successive readings is a line rather than a flat
        //row of identical points - and so a spec can tell one reading from the next.
        readings += 1;
        const value = 1.0e-3 + readings * 1.0e-5 + (readings % 3) * 1.0e-6;
        return `${value >= 0 ? '+' : ''}${value.toExponential(8).toUpperCase().replace('E', 'E')}`;
    }

    const sockets = new Set();

    const server = net.createServer((socket) => {
        sockets.add(socket);
        socket.on('close', () => sockets.delete(socket));

        //A command can arrive split across packets, and two can arrive in one. Buffer, then take whole
        //lines - which is what a real instrument's parser does and what makes the burst in the queue
        //specs behave the same way twice.
        let buffer = '';

        socket.on('data', async (chunk) => {
            buffer += chunk.toString('ascii');

            let cut;
            while ((cut = buffer.indexOf('\n')) >= 0) {
                const command = buffer.slice(0, cut).replace(/\r$/, '').trim();
                buffer = buffer.slice(cut + 1);
                if (command.length === 0) continue;

                received.push(command);

                const answer = reply(command);
                if (answer === null) continue;

                if (delayMs > 0) await new Promise((r) => setTimeout(r, delayMs));
                if (socket.destroyed) continue;
                //A block goes out as the bytes it is, with the line feed a real instrument ends it on.
                socket.write(Buffer.isBuffer(answer) ? Buffer.concat([answer, Buffer.from('\n')]) : answer + '\n');
            }
        });

        //A reset from the app's side is an ordinary way for a session to end. Swallow it: an unhandled
        //ECONNRESET here takes the whole test process down, and the spec's failure would then be
        //"worker exited" rather than whatever it was actually checking.
        socket.on('error', () => {});
    });

    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', resolve);
    });

    const port = server.address().port;

    return {
        port,
        identity,
        profile: PROFILE,

        ///`host:port`, which is the spelling that picks the raw-socket transport - a bare host or a
        ///vxi:// address would ask for VXI-11, which this does not speak.
        address: `127.0.0.1:${port}`,

        ///Everything it has been asked, oldest first.
        received,

        ///Only the ones matching a pattern, for asserting on a burst without counting the *IDN? that
        ///every connection starts with.
        asked(pattern) {
            return received.filter((c) => pattern.test(c));
        },

        ///Stop listening and drop anything still connected. Sockets are destroyed rather than ended,
        ///because the app holds its connection open for the life of the session and waiting for it to
        ///close politely would hang the teardown.
        async stop() {
            for (const s of sockets) s.destroy();
            sockets.clear();
            await new Promise((resolve) => server.close(resolve));
        }
    };
}

module.exports = { startInstrument, IDENTITY, PROFILE, HEADERS };
