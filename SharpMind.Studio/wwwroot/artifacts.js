window.studioArtifacts = {
    open: function (base64, mime) {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const url = URL.createObjectURL(new Blob([bytes], { type: mime }));
        const opened = window.open(url, "_blank", "noopener");
        // Keep the blob alive long enough for the tab to load, then release it.
        setTimeout(function () { URL.revokeObjectURL(url); }, 60000);
        return opened !== null;
    },

    // Close an artifact popover on the first pointerdown outside it. The
    // listener runs in the capture phase but does not prevent the event, so
    // the prompt input still focuses and other controls still act.
    watchOutside: function (root, dotnet) {
        window.studioArtifacts.unwatchOutside(root);
        const handler = function (event) {
            if (!root || !root.contains(event.target)) {
                dotnet.invokeMethodAsync("CloseFromOutsideAsync").catch(function () { });
            }
        };
        root.__studioArtifactOutside = handler;
        document.addEventListener("pointerdown", handler, true);
    },

    unwatchOutside: function (root) {
        if (!root || !root.__studioArtifactOutside) return;
        document.removeEventListener("pointerdown", root.__studioArtifactOutside, true);
        delete root.__studioArtifactOutside;
    }
};
