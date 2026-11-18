"""A sampling failure must not shorten the accepted-message observation window."""
import time


def observe_window(seconds, read, validate, sample, *, clock=time.monotonic, sleep=time.sleep):
    deadline = clock() + seconds
    next_sample = clock()
    errors = {'sampling': [], 'receipts': [], 'integrity': []}
    while clock() < deadline:
        try:
            rows = read()
        except Exception as error:
            errors['receipts'].append({'at': clock(), 'error': repr(error)})
            rows = []
        for row in rows:
            try:
                validate(row)
            except Exception as error:
                value = {'id': row.get('id'), 'error': repr(error)}
                if value not in errors['integrity']:
                    errors['integrity'].append(value)
        if clock() >= next_sample:
            try:
                sample(deadline)
            except Exception as error:
                errors['sampling'].append({'at': clock(), 'error': repr(error)})
            next_sample = clock() + 5
        sleep(min(1, max(0, deadline - clock())))
    # The caller filters final receipt timestamps to its exact wall-clock cutoff.
    return errors
