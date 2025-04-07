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
import { SlimFaasPlanetSaver } from "@axa-fr/slimfaas-planet-saver";

const PlanetSaver = ({ children, baseUrl, fetch }) => {
  const [isFirstStart, setIsFirstStart] = useState(true);
  const environmentStarterRef = useRef(null);

  useEffect(() => {
    if (!baseUrl) return;
    if (environmentStarterRef.current) return;

    const instance = new SlimFaasPlanetSaver(baseUrl, {
      interval: 2000,
      fetch,
      updateCallback: (data) => {
        const allReady = data.every((item) => item.NumberReady >= 1);
        if (allReady && isFirstStart) {
          setIsFirstStart(false);
        }
      },
      errorCallback: (error) => {
        console.error('Error detected :', error);
      },
      overlayStartingMessage: '🌳 Starting the environment.... 🌳',
      overlayNoActivityMessage: 'Waiting for activity to start environment...',
      overlayErrorMessage: 'An error occurred when starting the environment. Please contact an administrator.',
      overlaySecondaryMessage: 'Startup should be fast, but if no machines are available it can take several minutes.',
      overlayLoadingIcon: '🌍',
      overlayErrorSecondaryMessage: 'If the error persists, please contact an administrator.'
    });

    environmentStarterRef.current = instance;
    instance.initialize();
    instance.startPolling();

    return () => {
      instance.cleanup();
      environmentStarterRef.current = null;
    };
  }, [baseUrl, fetch, isFirstStart]);

  // Until the environment is confirmed ready, don't render children
  if (isFirstStart) {
    return null;
  }

  return <>{children}</>;
};

export default PlanetSaver;
```
---

## Usage:

```jsx
<PlanetSaver baseUrl="http://slimfaas.mycompany.com" fetch={window.fetch}>
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
