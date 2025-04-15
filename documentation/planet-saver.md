# SlimFaas Planet Saver [![npm version](https://badge.fury.io/js/%40axa-fr%2Fslimfaas-planet-saver.svg)](https://badge.fury.io/js/%40axa-fr%2Fslimfaas-planet-saver)

> **Important Note**: Starting from **0 replicas** to **1 replica** can be challenging because if no machine is available, the application must wait for a new machine to start. This startup process may exceed typical HTTP timeouts (for example, 7 minutes). **SlimPlanet** (via SlimFaas) solves this issue by providing a user-friendly interface that informs users the backend is starting, while the infrastructure wakes up in the background.


**@axa-fr/slimfaas-planet-saver** is a Vanilla JavaScript project to help “save the planet” by scaling your backends to zero when not in use, and waking them on demand. It is built around [SlimFaas](https://github.com/SlimPlanet/slimfaas), the slimmest and simplest Function-as-a-Service for Kubernetes.

SlimFaas provides an API to give your frontend detailed information about the state of your backend infrastructure. This makes **@axa-fr/slimfaas-planet-saver** a true **“mind changer”** — in production, you can have zero replicas running for your API backend, and display a friendly message in the UI that the backend is starting (instead of showing an error). When the backend is ready, the user seamlessly continues their journey.

![SlimFaasPlanetSaver.gif](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimfaasPlanetSaver.gif?raw=true)

---

## Why Use @axa-fr/slimfaas-planet-saver?

- **User-Friendly Zero-Replica**: Show users a “starting up” message instead of an error when your backend is scaling from zero.
- **Easy Integration**: A lightweight library that quickly plugs into your frontend code.
- **Real-Time Monitoring**: Continually checks your SlimFaas environment status and updates the UI when your services are ready.
- **Mind Changer**: Lets you remove always-on (idle) replicas. This can save costs and reduce carbon footprint.

---

## Installation

Install via npm:

```bash
npm install @axa-fr/slimfaas-planet-saver
```

---

## Basic Usage (Vanilla JS Example)

```js
import { SlimFaasPlanetSaver } from '@axa-fr/slimfaas-planet-saver';

const planetSaver = new SlimFaasPlanetSaver('http://slimfaas.mycompany.com', {
    interval: 2000,
    fetch: window.fetch, // or any fetch polyfill
    updateCallback: (data) => {
        console.log('Update callback data:', data);
    },
    errorCallback: (error) => {
        console.error('Error detected:', error);
    },
    overlayStartingMessage: '🌳 Starting the environment... 🌳',
    overlayNoActivityMessage: 'No activity yet — environment is sleeping.',
    overlayErrorMessage: 'An error occurred while starting the environment. Please try again later.',
});

// Initialize and begin polling
planetSaver.initialize();
planetSaver.startPolling();

// When you no longer need it:
planetSaver.cleanup();

```

This example:

1. Initializes a SlimFaasPlanetSaver instance with a base URL to your SlimFaas server.
2. Configures callbacks for status updates and errors.
3. Starts periodic polling to monitor environment readiness.

---
## React.js Example

Below is a simplified code snippet showing how you might wrap your React app in a `PlanetSaver` component that checks if your environment is ready before rendering children:

```javascript
import React, { useState, useEffect, useRef } from 'react';
import { SlimFaasPlanetSaver } from '@axa-fr/slimfaas-planet-saver';

const PlanetSaver = ({ children, baseUrl, fetch, noActivityTimeout=60000, behavior={} }) => {
    const [isFirstStart, setIsFirstStart] = useState(true);
    const environmentStarterRef = useRef(null);

    useEffect(() => {
        if (!baseUrl) return;

        if (environmentStarterRef.current) return;

        const instance = new SlimFaasPlanetSaver(baseUrl, {
            interval: 2000,
            fetch,
            behavior,
            updateCallback: (data) => {
                // Filter only the items that block the UI (WakeUp+BlockUI)
                const blockingItems = data.filter(
                    (item) => instance.getBehavior(item.Name) === 'WakeUp+BlockUI'
                );

                // If all blocking items are ready, set isFirstStart to false
                const allBlockingReady = blockingItems.every(
                    (item) => item.NumberReady >= 1
                );
                if (allBlockingReady && isFirstStart) {
                    setIsFirstStart(false);
                }
            },
            errorCallback: (error) => {
                console.error('Error detected :', error);
            },
            overlayStartingMessage: '🌳 Starting the environment.... 🌳',
            overlayNoActivityMessage: 'Waiting activity to start environment...',
            overlayErrorMessage: 'An error occurred when starting environment. Please contact an administrator.',
            overlaySecondaryMessage: 'Startup should be fast, but if no machines are available it can take several minutes.',
            overlayLoadingIcon: '🌍',
            overlayErrorSecondaryMessage: 'If the error persists, please contact an administrator.',
            noActivityTimeout
        });

        environmentStarterRef.current = instance;

        // Initialiser les effets de bord
        instance.initialize();
        instance.startPolling();

        return () => {
            instance.cleanup();
            environmentStarterRef.current = null;
        };
    }, [baseUrl]);

    if (isFirstStart) {
        return null;
    }

    return <>{children}</>;
};

export default PlanetSaver;

```

### Usage:

```jsx
const behavior: {
    "api-speech-to-text": "WakeUp",
    "heavy-pdf-service": "WakeUp+BlockUI",
    "deprecated-service": "None"
}

<PlanetSaver baseUrl="http://slimfaas.mycompany.com" fetch={window.fetch} behavior={behavior}>
  <App />
</PlanetSaver>
```

---

## Running the Demo Locally
To see @axa-fr/slimfaas-planet-saver in action:

```bash
git clone https://github.com/SlimPlanet/slimfaas.git
cd slimfaas/src/SlimFaasPlanetSaver
npm install
npm run dev
```

Then open your browser at the address shown in the console to view the demo.

---

## Configuration Options
When you create a `new SlimFaasPlanetSaver(baseUrl, options)`, you can provide the following optional properties in the `options` object:

| Property                  | Type                                                                 | Default                                                                                   | Description                                                                                                                                                                                                                     |
|---------------------------|----------------------------------------------------------------------|-------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| updateCallback            | `(data: any[]) => void`                                              | `() => {}`                                                                                | Function called after a successful fetch of the functions’ status. The array data includes objects with info about each function, for example: `[{ Name: 'myFunc', NumberReady: 1 }, ...]`.                                     |
| errorCallback             | `(errorMessage: string) => void`                                     | `() => {}`                                                                                | Function called if an error occurs during the status fetch (e.g., network error). Receives an errorMessage string.                                                                                                              |
| interval                  | `number`                                                             | `5000`                                                                                    | How frequently (in ms) the polling should run.                                                                                                                                                                                  |
| overlayStartingMessage    | `string`                                                             | `"🌳 Starting the environment.... 🌳"`                                                    | Main message shown on the overlay when the environment is waking up.                                                                                                                                                            |
| overlayNoActivityMessage  | `string`                                                             | `"Waiting activity to start environment..."`                                              | Message shown if there is no user activity (mouse movement) for too long, but the environment is not ready yet.                                                                                                                 |
| overlayErrorMessage       | `string`                                                             | `"An error occurred while starting the environment."`                                     | Main message shown on the overlay if an error occurs (e.g., network error).                                                                                                                                                     |
| overlaySecondaryMessage   | `string`                                                             | `"Startup should be fast, but if no machines are available it can take several minutes."` | Secondary message shown on the overlay when the environment is waking up.                                                                                                                                                       |
| overlayErrorSecondaryMessage | `string`                                                             | `"If the error persists, please contact an administrator."`                               | Secondary message shown on the overlay when an error occurs.                                                                                                                                                                    |
| overlayLoadingIcon        | `string`                                                             | `"🌍"`                                                                                    | Text or icon shown on the overlay. By default, it is animated to spin.                                                                                                                                                          |
| noActivityTimeout         | `number`                                                             | `60000`                                                                                   | How long (in ms) to wait for mouse movement before concluding there is no activity. If no activity is detected, a different overlay message is displayed.                                                                       |
| wakeUpTimeout             | `number`                                                             | `60000`                                                                                   | If a function was recently “woken up,” we’ll skip re-calling wake-up for that function within this timeout window (in ms).                                                                                                      |
| fetch                     | `typeof fetch`                                                       | Global fetch                                                                              | Custom fetch function if you want to provide your own (e.g., for SSR, or if your environment doesn't have a global fetch).                                                                                                      |
| behavior                  | `{ [functionName: string]: 'WakeUp+BlockUI' \| 'WakeUp' \| 'None' }` | *Not set; defaults each function to "WakeUp+BlockUI" if unspecified*                      | Allows you to override how each function is handled: 1. `"WakeUp+BlockUI"`: wakes the function and blocks the UI with the overlay until it’s ready. 2. `"WakeUp"`: wakes without blocking the UI. 3. `"None"`: no wake-up call. |

### Notes on Behavior
If a function is **not** specified in the behavior map, it defaults to `"WakeUp+BlockUI"`.

- `"WakeUp+BlockUI"` means the overlay will be shown until that function is `NumberReady >= 1`.
- `"WakeUp"` means we attempt to wake up the function, but do not keep the overlay shown specifically for that function.
- `"None"` means the function will neither be woken up nor block the UI.

### Lifecycle

1. **Initialize**
   Call `instance.initialize()` to create the overlay elements, inject styles, and bind event listeners (e.g., for mouse movement).

2. **Start Polling**
   Call `instance.startPolling()` to begin the periodic checks of the environment. If the environment is not ready, the overlay will appear.

3. **Stop Polling / Cleanup**
   When your component unmounts or you no longer need to monitor the environment, call `instance.cleanup()`. This removes the overlay, styles, and any timers or event listeners.
