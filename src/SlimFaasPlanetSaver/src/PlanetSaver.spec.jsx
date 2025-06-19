import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import PlanetSaver from './PlanetSaver.jsx';
import mockFetch, {
    setAlternateStatusFunctionsBody,
    alternateStatusFunctionsBodyOn,
    alternateStatusFunctionsBodyOff
} from './mockFetch.js';
import React from 'react';

describe('PlanetSaver Component', () => {
    const baseUrl = 'https://slimfaas/';

    function setDocumentVisibility(state) {
        Object.defineProperty(document, 'visibilityState', {
            value: state,
            writable: true,
        });
        document.dispatchEvent(new Event('visibilitychange'));
    }

    it('Should display SlimFaasPlanetSaver', async () => {
        const handleVisibilityChange = vi.fn();
        const { unmount } = render(
            <PlanetSaver baseUrl={baseUrl} fetch={mockFetch(false)} noActivityTimeout={5000}>
                Child Component
            </PlanetSaver>
        );

        // Wait for the overlay
        await waitFor(() => screen.getByText('🌳 Starting the environment.... 🌳'));
        expect(screen.getByText('🌳 Starting the environment.... 🌳')).toBeTruthy();
        screen.debug();

        // Switch status to ON
        setAlternateStatusFunctionsBody(alternateStatusFunctionsBodyOn);
        await waitFor(() => screen.getByText('Child Component'), { timeout: 4000 });
        expect(screen.getByText('Child Component')).toBeTruthy();
        screen.debug();

        // Switch status to OFF, hide page
        setAlternateStatusFunctionsBody(alternateStatusFunctionsBodyOff);
        setDocumentVisibility('hidden');
        screen.debug();

        // Show page again
        setDocumentVisibility('visible');
        await waitFor(() => screen.getByText('Waiting activity to start environment...'), { timeout: 5000 });
        expect(screen.getByText('Waiting activity to start environment...')).toBeTruthy();
        screen.debug();

        // Mouse movement triggers wake-up
        document.dispatchEvent(new MouseEvent('mousemove', { clientX: 100, clientY: 100 }));
        await waitFor(() => screen.getByText('🌳 Starting the environment.... 🌳'), { timeout: 10000 });
        expect(screen.getByText('🌳 Starting the environment.... 🌳')).toBeTruthy();
        screen.debug();

        unmount();
    }, { timeout: 40000 });

    it('Should display SlimFaasPlanetSaver Error', async () => {
        const { unmount } = render(
            <PlanetSaver baseUrl={baseUrl} fetch={mockFetch(true, 1)} noActivityTimeout={10000}>
                Child Component
            </PlanetSaver>
        );
        await waitFor(
            () =>
                screen.getByText('An error occurred when starting environment. Please contact an administrator.'),
            { timeout: 10000 }
        );
        expect(
            screen.getByText('An error occurred when starting environment. Please contact an administrator.')
        ).toBeTruthy();
        screen.debug();

        unmount();
    }, { timeout: 20000 });

    //
    // NEW TEST: checks that a function with "None" behavior does NOT block the UI
    //
    it('Should skip blocking UI if function has behavior=None', async () => {
        // Suppose we only switch fibonacci2 to 'None'; the rest remain default
        // (i.e., 'WakeUp+BlockUI' if not specified).
        // We'll configure our environment so that fibonacci1 is ready, but fibonacci2 is NOT.
        setAlternateStatusFunctionsBody([
            { NumberReady: 1, numberRequested: 1, PodType: 'Deployment', Visibility: 'Public', Name: 'fibonacci1' },
            { NumberReady: 0, numberRequested: 1, PodType: 'Deployment', Visibility: 'Public', Name: 'fibonacci2' }
        ]);

        // Render with an explicit behavior override
        const { unmount } = render(
            <PlanetSaver
                baseUrl={baseUrl}
                fetch={mockFetch(false)}
                noActivityTimeout={3000}
                behavior={{ fibonacci2: 'None' }}
            >
                Child Component
            </PlanetSaver>
        );

        // Because fibonacci2 has behavior=None, the UI should NOT be blocked,
        // even though NumberReady=0 for fibonacci2
        // Wait for Child Component to appear
        await waitFor(() => screen.getByText('Child Component'), { timeout: 5000 });
        expect(screen.getByText('Child Component')).toBeTruthy();
        screen.debug();

        unmount();
    }, { timeout: 15000 });
});
