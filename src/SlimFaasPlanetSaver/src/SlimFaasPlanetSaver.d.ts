declare class SlimFaasPlanetSaver {
    private baseUrl: string;
    private updateCallback: (data: any) => void;
    private errorCallback: (error: any) => void;
    private interval: number;
    private overlayStartingMessage: string;
    private overlayNoActivityMessage: string;
    private overlayErrorMessage: string;
    private overlaySecondaryMessage: string;
    private overlayErrorSecondaryMessage: string;
    private overlayLoadingIcon: string;
    private noActivityTimeout: number;
    private wakeUpTimeout: number;
    private fetch: typeof fetch;
    private intervalId: number | null;
    private isDocumentVisible: boolean;
    private overlayElement: HTMLElement | null;
    private spanElement: HTMLElement | null;
    private styleElement: HTMLElement | null;
    private isReady: boolean;
    private id: number;
    private cleanned: boolean;
    private lastMouseMoveTime: number;
    private handleMouseMove: () => void;
    private handleVisibilityChange: () => void;

    constructor(baseUrl: string, options?: {
    updateCallback?: (data: any) => void,
    errorCallback?: (error: any) => void,
    interval?: number,
    overlayStartingMessage?: string,
    overlayNoActivityMessage?: string,
    overlayErrorMessage?: string,
    overlaySecondaryMessage?: string,
    overlayErrorSecondaryMessage?: string,
    overlayLoadingIcon?: string,
    fetch?: typeof fetch
    noActivityTimeout?: number
    wakeUpTimeout?: number
});

initialize(): void;
wakeUpPods(data: Array<{ Name: string, NumberReady: number }>): Promise<void>;
fetchStatus(): Promise<void>;
setReadyState(isReady: boolean): void;
startPolling(): void;
stopPolling(): void;
injectStyles(): void;
createOverlay(): void;
showOverlay(): void;
hideOverlay(): void;
updateOverlayMessage(newMessage: string, status?: 'waiting' | 'waiting-action' | 'error', secondaryMessage?: string | null): void;
cleanup(): void;
}

export default SlimFaasPlanetSaver;
