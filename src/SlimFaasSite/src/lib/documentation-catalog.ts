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
    'get-started-kubernetes': {
        sourcePath: 'docs/get-started-kubernetes.md', route: '/get-started/kubernetes',
        label: 'With Kubernetes', title: 'Get Started with Kubernetes',
        description: 'Install a three-node SlimFaas cluster and explore the dashboard on Kubernetes.',
    },
    'get-started-local': {
        sourcePath: 'docs/get-started-local.md', route: '/get-started/local',
        label: 'In Local', title: 'Get Started in Local',
        description: 'Run real SlimFaas nodes, functions and jobs as native processes.',
    },
    'get-started-docker-compose': {
        sourcePath: 'docs/get-started-docker-compose.md', route: '/get-started/docker-compose',
        label: 'With Docker Compose', title: 'Get Started with Docker Compose',
        description: 'Explore SlimFaas functions, events, jobs and data with Docker Compose.',
    },
    'guided-tour': {
        sourcePath: 'docs/guided-tour.md', route: '/guided-tour',
        label: 'Guided Tour', title: 'Discover SlimFaas Step by Step',
        description: 'Follow requests from cURL or Bruno through the live SlimFaas dashboard.',
    },
    'api-reference': {
        sourcePath: 'docs/api-reference.md', route: '/api-reference',
        label: 'API Reference', title: 'SlimFaas API Reference',
        description: 'HTTP routes, response contracts, streaming and internal interfaces.',
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
        description: 'Broadcast events to ready HTTP replicas and connected WebSocket clients.',
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

export const DOCUMENTATION_GROUPS: { label: string; ids: DocumentationId[] }[] = [
    { label: 'Start here', ids: ['get-started', 'get-started-kubernetes', 'get-started-local', 'get-started-docker-compose', 'guided-tour'] },
    { label: 'Features', ids: ['functions', 'events', 'jobs', 'data-sets', 'data-files', 'clients', 'planet-saver', 'kafka'] },
    { label: 'Operate', ids: ['user-interface', 'autoscaling', 'opentelemetry', 'benchmarking'] },
    { label: 'Reference', ids: ['api-reference', 'how-it-works', 'local-mode'] },
];

export const DOCUMENTATION_ORDER = DOCUMENTATION_GROUPS.flatMap(group => group.ids);
