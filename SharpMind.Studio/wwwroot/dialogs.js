(function () {
    function show(options) {
        return new Promise(resolve => {
            const backdrop = document.createElement('div');
            backdrop.className = 'studio-dialog-backdrop';

            const dialog = document.createElement('div');
            dialog.className = 'studio-dialog';
            dialog.setAttribute('role', 'dialog');
            dialog.setAttribute('aria-modal', 'true');

            const title = document.createElement('div');
            title.className = 'studio-dialog-title';
            title.textContent = options.title || 'SharpMind Studio';

            const message = document.createElement('div');
            message.className = 'studio-dialog-message';
            message.textContent = options.message || '';

            const input = document.createElement('input');
            input.className = 'studio-dialog-input';
            input.style.display = options.input ? 'block' : 'none';
            input.value = options.value || '';

            const actions = document.createElement('div');
            actions.className = 'studio-dialog-actions';

            const cancelButton = document.createElement('button');
            cancelButton.type = 'button';
            cancelButton.className = 'studio-dialog-button';
            cancelButton.textContent = options.cancelText || 'Cancel';

            const okButton = document.createElement('button');
            okButton.type = 'button';
            okButton.className = 'studio-dialog-button primary';
            okButton.textContent = options.confirmText || 'OK';

            actions.append(cancelButton, okButton);
            dialog.append(title, message, input, actions);
            backdrop.append(dialog);
            document.body.append(backdrop);

            const close = value => {
                backdrop.remove();
                document.removeEventListener('keydown', onKeyDown);
                resolve(value);
            };

            const accept = () => close(options.input ? input.value : true);
            const cancel = () => close(options.input ? null : false);

            const onKeyDown = event => {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    cancel();
                } else if (event.key === 'Enter') {
                    event.preventDefault();
                    accept();
                }
            };

            cancelButton.addEventListener('click', cancel);
            okButton.addEventListener('click', accept);
            backdrop.addEventListener('mousedown', event => {
                if (event.target === backdrop) cancel();
            });
            document.addEventListener('keydown', onKeyDown);

            window.setTimeout(() => {
                if (options.input) {
                    input.focus();
                    input.select();
                } else {
                    okButton.focus();
                }
            }, 0);
        });
    }

    window.confirm = function (message) {
        return show({ message: message, confirmText: 'OK', cancelText: 'Cancel' });
    };

    window.prompt = function (message, defaultValue) {
        return show({
            message: message,
            value: defaultValue || '',
            input: true,
            confirmText: 'Save',
            cancelText: 'Cancel'
        });
    };
})();

