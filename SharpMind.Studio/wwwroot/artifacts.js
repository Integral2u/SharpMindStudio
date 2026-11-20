window.studioArtifacts = {
    open: function (base64, mime) {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const url = URL.createObjectURL(new Blob([bytes], { type: mime }));
        const opened = window.open(url, "_blank", "noopener");
        // Keep the blob alive long enough for the tab to load, then release it.
        setTimeout(function () { URL.revokeObjectURL(url); }, 60000);
        return opened !== null;
    }
};
