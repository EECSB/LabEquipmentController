//What the AI assistant's microphone sends a provider to transcribe: the recording made mono, brought down to 16 kHz and written as a
//16-bit PCM WAV - a format OpenAI's transcription and Gemini both take, where the WebM a browser records in is not one
//Gemini lists. 16 kHz is what speech models listen at, and a third the size of the 48 kHz a microphone gives. No DOM, so
//it can be unit-tested under Node (see tests/jstests/). Exposed as window.speechWav in the browser and as module.exports
//under Node; speech.js does the recording.
(function (factory) {
    const api = factory();

    if (typeof module !== 'undefined' && module.exports)
        module.exports = api;

    if (typeof window !== 'undefined')
        window.speechWav = api;
})(function () {
    'use strict';

    //The rate a speech model listens at.
    const SPEECH_RATE = 16000;

    //
    //One channel at a lower rate, each sample the mean of the ones it stands for - which is a crude low-pass as well,
    //enough for speech, whose sounds that matter are well under the new rate's half. A rate at or under the target is
    //left as it is.
    //
    function downsample(samples, fromRate, toRate) {
        if (fromRate <= toRate)
            return Float32Array.from(samples);

        const ratio = fromRate / toRate;
        const length = Math.floor(samples.length / ratio);
        const out = new Float32Array(length);

        for (let i = 0; i < length; i++) {
            const start = Math.floor(i * ratio);
            const end = Math.min(samples.length, Math.floor((i + 1) * ratio));
            let sum = 0;

            for (let j = start; j < end; j++)
                sum += samples[j];

            out[i] = sum / Math.max(1, end - start);
        }

        return out;
    }

    //The channels given, averaged into one.
    function mixDown(channels) {
        if (channels.length === 1)
            return Float32Array.from(channels[0]);

        const length = channels[0].length;
        const out = new Float32Array(length);

        for (let i = 0; i < length; i++) {
            let sum = 0;

            for (let c = 0; c < channels.length; c++)
                sum += channels[c][i];

            out[i] = sum / channels.length;
        }

        return out;
    }

    //
    //A 16-bit PCM WAV of one channel: the 44-byte RIFF header and the samples, each clipped to [-1, 1] and written
    //little-endian.
    //
    function encodeWav(samples, sampleRate) {
        const bytes = new Uint8Array(44 + (samples.length * 2));
        const view = new DataView(bytes.buffer);

        function text(at, value) {
            for (let i = 0; i < value.length; i++)
                bytes[at + i] = value.charCodeAt(i);
        }

        text(0, 'RIFF');
        view.setUint32(4, 36 + (samples.length * 2), true);
        text(8, 'WAVE');
        text(12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);
        view.setUint16(22, 1, true);
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, sampleRate * 2, true);
        view.setUint16(32, 2, true);
        view.setUint16(34, 16, true);
        text(36, 'data');
        view.setUint32(40, samples.length * 2, true);

        for (let i = 0; i < samples.length; i++) {
            const clipped = Math.max(-1, Math.min(1, samples[i]));
            let value = clipped * 0x7FFF;

            if (clipped < 0)
                value = clipped * 0x8000;

            view.setInt16(44 + (i * 2), Math.round(value), true);
        }

        return bytes;
    }

    //A recording's channels at their rate, as the WAV a provider is sent.
    function speechWav(channels, sampleRate) {
        return encodeWav(downsample(mixDown(channels), sampleRate, SPEECH_RATE), Math.min(sampleRate, SPEECH_RATE));
    }

    return {
        SPEECH_RATE: SPEECH_RATE,
        downsample: downsample,
        mixDown: mixDown,
        encodeWav: encodeWav,
        speechWav: speechWav
    };
});
