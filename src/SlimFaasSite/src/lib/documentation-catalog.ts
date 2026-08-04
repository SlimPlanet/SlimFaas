export interface DocumentationEntry {
    sourcePath: `docs/${string}.md`;
    route: `/${string}` | '/';
    label: string;
    title: string;
    description: string;
}

export const DOCUMENTATION_CATALOG = {
    home: {
        sourcePath: 'docs/home.md',
        route: '/',
        label: 'Home',
        title: 'SlimFaas: The slimmest and simplest Function as a Service',
        description: 'Deploy functions effortlessly with SlimFaas, the ultra-light FaaS platform.',
    },
    'get-started': {
        sourcePath: 'docs/get-started.md',
        route: '/get-started',
        label: 'Get Started',
        title: 'Get Started with SlimFaas',
        description: 'Install SlimFaas and deploy your first functions.',
    },
    'local-mode': {
        sourcePath: 'docs/native-local-mode.md',
        route: '/local-mode',
        label: 'Local Mode',
        title: 'Native Local Development Mode',
        description:
            'Run SlimFaas functions, jobs, development processes, and a real local cluster without Kubernetes or Docker.',
    },
    functions: {
        sourcePath: 'docs/functions.md',
        route: '/functions',
        label: 'Functions',
        title: 'SlimFaas Functions',
        description: 'Call SlimFaas functions synchronously and asynchronously.',
    },
    'user-interface': {
        sourcePath: 'docs/user-interface.md',
        route: '/user-interface',
        label: 'User Interface',
        title: 'SlimFaas User Interface',
        description: 'Monitor functions, queues, jobs, and real-time messages.',
    },
    clients: {
        sourcePath: 'docs/clients.md',
        route: '/clients',
        label: 'Clients',
        title: 'SlimFaas Clients',
        description: 'Use the official SlimFaas clients from your applications.',
    },
    events: {
        sourcePath: 'docs/events.md',
        route: '/events',
        label: 'Events',
        title: 'SlimFaas Events',
        description: 'Publish and subscribe to synchronous and asynchronous events.',
    },
    jobs: {
        sourcePath: 'docs/jobs.md',
        route: '/jobs',
        label: 'Jobs',
        title: 'SlimFaas Jobs',
        description: 'Define, schedule, and run one-off SlimFaas jobs.',
    },
    opentelemetry: {
        sourcePath: 'docs/opentelemetry.md',
        route: '/opentelemetry',
        label: 'OpenTelemetry',
        title: 'SlimFaas OpenTelemetry',
        description: 'Configure distributed traces, metrics, and logs for SlimFaas.',
    },
    benchmarking: {
        sourcePath: 'docs/benchmarking.md',
        route: '/benchmarking',
        label: 'Benchmarks',
        title: 'SlimFaas Latency and Autoscaling Benchmark',
        description:
            'Measure synchronous proxy overhead, asynchronous delivery, and native-local autoscaling speed.',
    },
    autoscaling: {
        sourcePath: 'docs/autoscaling.md',
        route: '/autoscaling',
        label: 'Autoscaling',
        title: 'SlimFaas Autoscaling',
        description: 'Configure scale-to-zero, scale-out, PromQL triggers, and metrics.',
    },
    kafka: {
        sourcePath: 'docs/kafka.md',
        route: '/kafka',
        label: 'Kafka Connector',
        title: 'SlimFaas Kafka Connector',
        description: 'Wake and scale SlimFaas functions from Kafka topic lag.',
    },
    'planet-saver': {
        sourcePath: 'docs/planet-saver.md',
        route: '/planet-saver',
        label: 'Planet Saver',
        title: 'SlimFaas Planet Saver',
        description: 'Start and monitor function replicas from a JavaScript frontend.',
    },
    'data-files': {
        sourcePath: 'docs/data-files.md',
        route: '/data-files',
        label: 'Data Files',
        title: 'SlimFaas Data Files',
        description: 'Ingest, store, and serve temporary binary artifacts.',
    },
    'data-sets': {
        sourcePath: 'docs/data-sets.md',
        route: '/data-sets',
        label: 'Data Sets',
        title: 'SlimFaas Data Sets',
        description: 'Store small replicated key-value payloads with optional TTL.',
    },
    'how-it-works': {
        sourcePath: 'docs/how-it-works.md',
        route: '/how-it-works',
        label: 'How It Works',
        title: 'How SlimFaas Works',
        description: 'Understand the SlimFaas architecture and request flows.',
    },
    mcp: {
        sourcePath: 'docs/mcp.md',
        route: '/mcp',
        label: 'MCP',
        title: 'SlimFaas MCP',
        description: 'Convert OpenAPI definitions into MCP-ready tools.',
    },
} as const satisfies Record<string, DocumentationEntry>;

export type DocumentationId = keyof typeof DOCUMENTATION_CATALOG;

export function getDocumentationEntry(id: DocumentationId): DocumentationEntry {
    return DOCUMENTATION_CATALOG[id];
}

export function getPublicRouteForSource(sourcePath: string): string | undefined {
    return Object.values(DOCUMENTATION_CATALOG).find(
        (entry) => entry.sourcePath === sourcePath,
    )?.route;
}
