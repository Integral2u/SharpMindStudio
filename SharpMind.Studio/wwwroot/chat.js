window.studioChat = {
    scrollToBottom: function (id) {
        const element = document.getElementById(id);
        if (element) element.scrollTop = element.scrollHeight;
    },
    focus: function (id) {
        const element = document.getElementById(id);
        if (element) element.focus();
    }
};
