window.studioChat = {
    enterSend: function (event, buttonId) {
        if (event.key !== "Enter" || event.shiftKey) return;
        event.preventDefault();
        const button = document.getElementById(buttonId);
        if (button && !button.disabled) button.click();
    },
    scrollToBottom: function (id) {
        const element = document.getElementById(id);
        if (element) element.scrollTop = element.scrollHeight;
    },
    focus: function (id) {
        const element = document.getElementById(id);
        if (element) element.focus();
    },
    focusAndScroll: function (id) {
        const element = document.getElementById(id);
        if (!element) return;
        element.scrollIntoView({ behavior: "smooth", block: "center" });
        element.focus({ preventScroll: true });
    }
};


// Microphone capture for the chat composer. Records with MediaRecorder, decodes the
// captured blob with WebAudio and returns a 16-bit PCM WAV as base64, which the
// engine's WavFile reader accepts directly.
window.studioAudio = (() => {
    let stream = null;
    let recorder = null;
    let chunks = [];
    let context = null;

    function writeString(view, offset, text) {
        for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
    }

    function encodeWav(samples, sampleRate) {
        const buffer = new ArrayBuffer(44 + samples.length * 2);
        const view = new DataView(buffer);
        writeString(view, 0, "RIFF");
        view.setUint32(4, 36 + samples.length * 2, true);
        writeString(view, 8, "WAVE");
        writeString(view, 12, "fmt ");
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);
        view.setUint16(22, 1, true);
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, sampleRate * 2, true);
        view.setUint16(32, 2, true);
        view.setUint16(34, 16, true);
        writeString(view, 36, "data");
        view.setUint32(40, samples.length * 2, true);

        let offset = 44;
        for (let i = 0; i < samples.length; i++, offset += 2) {
            const sample = Math.max(-1, Math.min(1, samples[i]));
            view.setInt16(offset, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true);
        }

        const bytes = new Uint8Array(buffer);
        let binary = "";
        const chunk = 0x8000;
        for (let i = 0; i < bytes.length; i += chunk) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
        }
        return btoa(binary);
    }

    return {
        async start() {
            if (recorder) return;
            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                throw new Error("Microphone capture is not available in this browser.");
            }
            stream = await navigator.mediaDevices.getUserMedia({
                audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true }
            });
            chunks = [];
            recorder = new MediaRecorder(stream);
            recorder.ondataavailable = event => {
                if (event.data && event.data.size > 0) chunks.push(event.data);
            };
            recorder.start();
        },

        async stop() {
            if (!recorder || !stream) return "";
            const current = recorder;
            const mime = current.mimeType || "audio/webm";
            const blob = await new Promise(resolve => {
                current.onstop = () => resolve(new Blob(chunks, { type: mime }));
                current.stop();
            });
            recorder = null;
            chunks = [];
            stream.getTracks().forEach(track => track.stop());
            stream = null;

            const bytes = await blob.arrayBuffer();
            context = context || new (window.AudioContext || window.webkitAudioContext)();
            const decoded = await context.decodeAudioData(bytes);
            return encodeWav(decoded.getChannelData(0), decoded.sampleRate);
        },

        cancel() {
            try { if (recorder) recorder.stop(); } catch { }
            if (stream) stream.getTracks().forEach(track => track.stop());
            recorder = null;
            stream = null;
            chunks = [];
        }
    };
})();

