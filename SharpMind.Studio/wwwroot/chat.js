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
    }
};

