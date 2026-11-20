window.studioTheme = {
    apply: function (theme, appearance) {
        if (!document.body) return;
        document.body.dataset.theme = theme;
        document.body.dataset.appearance = appearance;
    }
};
